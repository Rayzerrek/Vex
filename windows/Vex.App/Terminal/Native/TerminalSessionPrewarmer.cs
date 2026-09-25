using System.ComponentModel;
using System.Windows;
using Vex.App.Model;
using Vex.Terminal;

namespace Vex.App.Terminal.Native;

internal static class TerminalSessionPrewarmer
{
    private static TerminalSession? _session;
    private static string? _workingDirectory;
    private static string? _shellId;
    private static readonly List<ArraySegment<byte>> _bufferedOutput = new();
    private static readonly object _sync = new();
    private static Action<ArraySegment<byte>>? _outputHandler;

    public static void StartPrewarm(string workingDirectory, string? shellId)
    {
        _ = Task.Run(() =>
        {
            StartupMark.Note("terminal prewarm spawn begin");
            var resolved = ShellRegistry.Resolve(shellId);
            var shellProgram = resolved?.Program ?? TerminalSession.DefaultShell();
            var shellArguments = resolved?.Arguments;

            var session = new TerminalSession();
            session.OutputReceived += OnPrewarmOutput;
            
            try
            {
                session.Start(workingDirectory, 80, 24, shellProgram, shellArguments);
            }
            catch (Win32Exception)
            {
                session = new TerminalSession();
                session.OutputReceived += OnPrewarmOutput;
                try { session.Start(workingDirectory, 80, 24, TerminalSession.DefaultShell(), null); }
                catch { return; }
            }
            StartupMark.Note("terminal prewarm spawn end");

            lock (_sync)
            {
                if (_outputHandler != null)
                {
                    session.Dispose();
                    return;
                }
                _session = session;
                _workingDirectory = workingDirectory;
                _shellId = shellId;
            }
        });
    }

    private static void OnPrewarmOutput(ArraySegment<byte> data)
    {
        lock (_sync)
        {
            if (_outputHandler == null)
            {
                if (_bufferedOutput.Count == 0)
                    StartupMark.Note("prewarmed first terminal output");

                var copy = System.Buffers.ArrayPool<byte>.Shared.Rent(data.Count);
                Buffer.BlockCopy(data.Array!, data.Offset, copy, 0, data.Count);
                System.Buffers.ArrayPool<byte>.Shared.Return(data.Array!);
                _bufferedOutput.Add(new ArraySegment<byte>(copy, 0, data.Count));
            }
            else
            {
                _outputHandler(data);
            }
        }
    }

    public static TerminalSession? Take(string workingDirectory, string? shellId, Action<ArraySegment<byte>> outputHandler)
    {
        lock (_sync)
        {
            if (_outputHandler != null || _session == null || _workingDirectory != workingDirectory || _shellId != shellId)
                return null;

            _outputHandler = outputHandler;
            var session = _session;
            _session = null;
            
            // Replay buffered chunks to the actual handler. Since we are in the
            // lock, the background reader thread will wait if it's currently
            // firing OnPrewarmOutput. After the replay, future chunks will be
            // forwarded to _outputHandler directly by OnPrewarmOutput.
            foreach (var chunk in _bufferedOutput)
            {
                outputHandler(chunk);
            }
            _bufferedOutput.Clear();
            
            return session;
        }
    }
}