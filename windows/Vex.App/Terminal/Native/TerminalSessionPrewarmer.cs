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

        internal void AttachOutputHandler(Action<ArraySegment<byte>> outputHandler)
        {
            lock (Sync)
            {
                if (_disposed || _slot.Invalidated)
                {
                    ReturnBufferedOutputLocked(_slot);
                    return;
                }

                _slot.OutputHandler = outputHandler;
                FlushBufferedOutputLocked(_slot);
                _attached = true;
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

                if (session is null && SessionTask.IsCompletedSuccessfully)
                    session = SessionTask.Result;
            }

            session?.Dispose();
        }
    }

    private static readonly object Sync = new();
    private static Slot? _slot;
    private static bool _disposed;

    public static void StartPrewarm(string workingDirectory, string? shellId)
    {
        lock (Sync)
        {
            if (_disposed)
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
        {
            InvalidateLocked(current);
            _slot = null;
        }

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
        var resolved = ShellRegistry.Resolve(slot.ShellId);
        var shellProgram = resolved?.Program ?? TerminalSession.DefaultShell();
        var shellArguments = resolved?.Arguments;

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
                session.Start(slot.WorkingDirectory, 80, 24, TerminalSession.DefaultShell(), null);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        StartupMark.Note("terminal prewarm spawn end");
        var disposeAfterBuild = false;
        lock (Sync)
        {
            if (slot.Invalidated)
            {
                disposeAfterBuild = true;
            }
            else
            {
                slot.Session = session;
            }
        }

        if (disposeAfterBuild)
            session.Dispose();

        return session;
    }

    private static void OnOutput(Slot slot, ArraySegment<byte> data)
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

            if (slot.BufferedOutput.Count == 0)
                StartupMark.Note("prewarmed first terminal output");

            var copy = System.Buffers.ArrayPool<byte>.Shared.Rent(data.Count);
            Buffer.BlockCopy(array, data.Offset, copy, 0, data.Count);
            System.Buffers.ArrayPool<byte>.Shared.Return(array);
            slot.BufferedOutput.Add(new ArraySegment<byte>(copy, 0, data.Count));
        }
    }

    public static Lease? Take(string workingDirectory, string? shellId)
    {
        lock (Sync)
        {
            if (_slot is not { } slot || slot.Claimed || slot.Invalidated
                || !Matches(slot, workingDirectory, shellId)
                || slot.SessionTask is not { } sessionTask)
                return null;

            slot.Claimed = true;
            _slot = null;
            StartPrewarmLocked(workingDirectory, shellId);
            return new Lease(slot, sessionTask);
        }
    }

    public static void Dispose()
    {
        lock (Sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_slot is not { } slot)
                return;

            InvalidateLocked(slot);
            _slot = null;
        }
    }

    private static void InvalidateLocked(Slot slot)
    {
        slot.Invalidated = true;
        slot.Session?.Dispose();
        slot.Session = null;
        ReturnBufferedOutputLocked(slot);
    }

    private static void FlushBufferedOutputLocked(Slot slot)
    {
        if (slot.OutputHandler is not { } handler)
            return;

        foreach (var chunk in slot.BufferedOutput)
            handler(chunk);
        slot.BufferedOutput.Clear();
    }

    private static void ReturnBufferedOutputLocked(Slot slot)
    {
        foreach (var chunk in slot.BufferedOutput)
        {
            if (chunk.Array is { } array)
                System.Buffers.ArrayPool<byte>.Shared.Return(array);
        }
        slot.BufferedOutput.Clear();
    }
}
