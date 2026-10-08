using System.ComponentModel;
using Vex.App.Model;
using Vex.Terminal;

namespace Vex.App.Terminal.Native;

/// <summary>
/// Keeps one shell session ahead of the next terminal pane. Taking a session
/// immediately starts its replacement, so new tabs do not pay interactive
/// shell initialization while the user is waiting for a prompt.
/// </summary>
internal static class TerminalSessionPrewarmer
{
    internal sealed class Slot
    {
        internal Slot(string workingDirectory, string? shellId)
        {
            WorkingDirectory = workingDirectory;
            ShellId = shellId;
        }

        internal string WorkingDirectory { get; }
        internal string? ShellId { get; }
        internal Task<TerminalSession>? SessionTask { get; set; }
        internal TerminalSession? Session { get; set; }
        internal Action<ArraySegment<byte>>? OutputHandler { get; set; }
        internal List<ArraySegment<byte>> BufferedOutput { get; } = new();
        internal int BufferedByteCount { get; set; }
        internal bool Claimed { get; set; }
        internal bool Invalidated { get; set; }
    }

    internal sealed class Lease : IDisposable
    {
        private readonly Slot _slot;
        private bool _attached;
        private bool _disposed;

        internal Lease(Slot slot, Task<TerminalSession> sessionTask)
        {
            _slot = slot;
            SessionTask = sessionTask;
        }

        internal Task<TerminalSession> SessionTask { get; }

        /// <summary>Returns false for an invalidated VT stream; the caller must cold-start.</summary>
        internal bool AttachOutputHandler(Action<ArraySegment<byte>> outputHandler)
        {
            lock (Sync)
            {
                if (_disposed || _slot.Invalidated)
                {
                    ReturnBufferedOutputLocked(_slot);
                    return false;
                }

                _slot.OutputHandler = outputHandler;
                FlushBufferedOutputLocked(_slot);
                _attached = true;
                return true;
            }
        }

        public void Dispose()
        {
            TerminalSession? session = null;
            lock (Sync)
            {
                if (_disposed || _attached)
                    return;

                _disposed = true;
                _slot.Invalidated = true;
                session = _slot.Session;
                _slot.Session = null;
                ReturnBufferedOutputLocked(_slot);

            }

            session?.Dispose();
        }
    }

    // PTY reads rent 64 KiB even for a short prompt; bound retained capacity,
    // not just payload size. Normal prompts need only a handful of buffers.
    internal const int MaxBufferedOutputBytes = 1024 * 1024;
    private static readonly object Sync = new();
    private static Slot? _slot;
    private static Slot? _overflowedSlot;
    private static bool _disposed;

    public static void StartPrewarm(string workingDirectory, string? shellId)
    {
        lock (Sync)
        {
            _disposed = false;
            if (IsPrewarmDisabledLocked(workingDirectory, shellId))
                return;
            if (_slot is { } current && !current.Claimed && !current.Invalidated
                && Matches(current, workingDirectory, shellId))
                return;

            StartPrewarmLocked(workingDirectory, shellId);
        }
    }

    private static void StartPrewarmLocked(string workingDirectory, string? shellId)
    {
        if (_slot is { } current)
            DisposeSessionInBackground(InvalidateLocked(current));

        var slot = new Slot(workingDirectory, shellId);
        _slot = slot;
        slot.SessionTask = Task.Run(() => Build(slot));
        _ = slot.SessionTask.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static bool Matches(Slot slot, string workingDirectory, string? shellId)
        => string.Equals(slot.WorkingDirectory, workingDirectory, StringComparison.Ordinal)
            && string.Equals(slot.ShellId, shellId, StringComparison.Ordinal);

    private static TerminalSession Build(Slot slot)
    {
        lock (Sync)
        {
            if (slot.Invalidated || _disposed)
                throw new OperationCanceledException("Prewarm slot was replaced.");
        }

        StartupMark.Note("terminal prewarm spawn begin");
        var launch = ShellLaunchBuilder.BuildShellLaunch(slot.ShellId);
        var shellProgram = launch.Program;
        var shellArguments = launch.Arguments;

        var session = new TerminalSession();
        session.OutputReceived += data => OnOutput(slot, data);

        try
        {
            session.Start(slot.WorkingDirectory, 80, 24, shellProgram, shellArguments);
        }
        catch (Win32Exception)
        {
            session.Dispose();
            session = new TerminalSession();
            session.OutputReceived += data => OnOutput(slot, data);
            try
            {
                var fallback = ShellLaunchBuilder.BuildShellLaunch("system");
                session.Start(slot.WorkingDirectory, 80, 24, fallback.Program, fallback.Arguments);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
        catch
        {
            session.Dispose();
            throw;
        }

        StartupMark.Note("terminal prewarm spawn end");
        var disposeAfterBuild = false;
        lock (Sync)
        {
            if (slot.Invalidated)
                disposeAfterBuild = true;
            else
                slot.Session = session;
        }

        if (disposeAfterBuild)
        {
            session.Dispose();
            throw new OperationCanceledException("Prewarm stream was invalidated during startup.");
        }

        return session;
    }

    /// <summary>Takes ownership of a pooled PTY buffer, or transfers it to the attached handler.</summary>
    internal static void OnOutput(Slot slot, ArraySegment<byte> data)
    {
        if (data.Array is not { } array)
            return;

        lock (Sync)
        {
            if (slot.Invalidated)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(array);
                return;
            }

            if (slot.OutputHandler is { } handler)
            {
                handler(data);
                return;
            }

            if (array.Length > MaxBufferedOutputBytes - slot.BufferedByteCount)
            {
                // Dropping individual chunks corrupts VT parsing. Disable this
                // configuration until it changes and let panes cold-start.
                _overflowedSlot = slot;
                System.Buffers.ArrayPool<byte>.Shared.Return(array);
                DisposeSessionInBackground(InvalidateLocked(slot));
                if (_slot is { } replacement && Matches(replacement, slot.WorkingDirectory, slot.ShellId))
                {
                    DisposeSessionInBackground(InvalidateLocked(replacement));
                    _slot = null;
                }
                return;
            }

            if (slot.BufferedOutput.Count == 0)
                StartupMark.Note("prewarmed first terminal output");

            slot.BufferedOutput.Add(data);
            slot.BufferedByteCount += array.Length;
        }
    }

    public static Lease? Take(string workingDirectory, string? shellId)
    {
        lock (Sync)
        {
            if (IsPrewarmDisabledLocked(workingDirectory, shellId))
                return null;
            if (_slot is { } existing && !existing.Claimed && !existing.Invalidated && !Matches(existing, workingDirectory, shellId))
            {
                DisposeSessionInBackground(InvalidateLocked(existing));
                _slot = null;
            }

            if (_slot is not { } slot || slot.Claimed || slot.Invalidated
                || !Matches(slot, workingDirectory, shellId)
                || slot.SessionTask is not { } sessionTask)
            {
                if (!_disposed)
                    StartPrewarmLocked(workingDirectory, shellId);
                return null;
            }

            slot.Claimed = true;
            _slot = null;
            StartPrewarmLocked(workingDirectory, shellId);
            return new Lease(slot, sessionTask);
        }
    }

    public static void Dispose()
    {
        TerminalSession? session;
        lock (Sync)
        {
            _disposed = true;
            _overflowedSlot = null;
            session = _slot is { } slot ? InvalidateLocked(slot) : null;
            _slot = null;
        }
        session?.Dispose();
    }

    private static bool IsPrewarmDisabledLocked(string workingDirectory, string? shellId)
    {
        if (_overflowedSlot is null)
            return false;
        if (Matches(_overflowedSlot, workingDirectory, shellId))
            return true;
        _overflowedSlot = null;
        return false;
    }

    private static TerminalSession? InvalidateLocked(Slot slot)
    {
        slot.Invalidated = true;
        var session = slot.Session;
        slot.Session = null;
        slot.OutputHandler = null;
        ReturnBufferedOutputLocked(slot);
        return session;
    }

    private static void DisposeSessionInBackground(TerminalSession? session)
    {
        // ClosePseudoConsole can wait for the reader. Never close from that
        // reader itself or under the lock its output callback needs.
        if (session is not null)
            _ = Task.Run(session.Dispose);
    }

    private static void FlushBufferedOutputLocked(Slot slot)
    {
        if (slot.OutputHandler is not { } handler)
            return;

        for (var index = 0; index < slot.BufferedOutput.Count; index++)
        {
            var chunk = slot.BufferedOutput[index];
            // The handler owns this buffer even if it throws. Leave only
            // unreplayed chunks for cancellation cleanup to return to the pool.
            slot.BufferedOutput[index] = default;
            slot.BufferedByteCount -= chunk.Array!.Length;
            handler(chunk);
        }
        slot.BufferedOutput.Clear();
        slot.BufferedByteCount = 0;
    }

    private static void ReturnBufferedOutputLocked(Slot slot)
    {
        foreach (var chunk in slot.BufferedOutput)
        {
            if (chunk.Array is { } array)
                System.Buffers.ArrayPool<byte>.Shared.Return(array);
        }
        slot.BufferedOutput.Clear();
        slot.BufferedByteCount = 0;
    }
}
