using System.ComponentModel;
using System.Windows.Threading;
using Vex.App.Model;
using Vex.Terminal;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private volatile TerminalSession? _session;
    private Action<int>? _sessionExitedHandler;
    private TerminalSessionPrewarmer.Lease? _prewarmLease;
    private bool _sessionStarting;
    private List<byte[]>? _pendingSessionInput;
    private int _firstOutputNoted;

    // After a burst of size changes (window drag, pane split), one settle pass
    // re-syncs ConPTY and forces a full cell read so wrapped lines do not
    // stay broken if an intermediate resize failed or raced the paint.
    private DispatcherTimer? _resizeSettleTimer;
    private short _pendingSessionCols;
    private short _pendingSessionRows;
    private bool _sessionResizePending;

    /// <summary>PID of the ConPTY shell process; null before the session starts.</summary>
    public int? ProcessId => _session?.ProcessId;

    private void ApplySessionResize(short cols, short rows)
    {
        var session = _session;
        if (session is null || _sessionStarting)
            return;
        _pendingSessionCols = cols;
        _pendingSessionRows = rows;
        // In-band resize (DEC 2048) reports must follow a *successful* ConPTY
        // resize: under ConPTY the child has no other way to learn the new
        // grid, and libghostty-vt answers the app's DECRQM probe with
        // "supported", which promised the report. The emulator's mode state
        // already reflects ?2048h by the time a resize happens, because apps
        // enable the mode during startup queries.
        var report = _terminal.InBandResize;
        if (!session.Resize(cols, rows, report))
        {
            _sessionResizePending = true;
            Diag($"conpty-resize-failed {cols}x{rows}");
        }
        else
        {
            _sessionResizePending = false;
            if (report)
                Diag($"in-band-resize-report {cols}x{rows}");
        }
    }

    private void ScheduleResizeSettle()
    {
        _resizeSettleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _resizeSettleTimer.Tick -= OnResizeSettled;
        _resizeSettleTimer.Tick += OnResizeSettled;
        _resizeSettleTimer.Stop();
        _resizeSettleTimer.Start();
    }

    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _resizeSettleTimer?.Stop();
        if (_disposed)
            return;

        // Heal ConPTY desync and any frame that painted mid-reflow with a
        // stale cell cache — the common "every line looks broken" after a
        // fast window drag.
        if (_session is not null)
            ApplySessionResize((short)_cols, (short)_rows);
        _terminal.InvalidateCellCache();
        _needsFullRedraw = true;
        FlushRedraw();

        if (_sessionResizePending)
            Diag($"conpty-resize-still-pending {_cols}x{_rows}");
    }

    private void WriteUserInput(ReadOnlySpan<byte> input)
    {
        if (_disposed)
            return;
        if (_sessionStarting)
        {
            // Only the UI thread queues input. Replay waits until startup
            // output has answered ConPTY queries and attached the reader.
            (_pendingSessionInput ??= new List<byte[]>()).Add(input.ToArray());
            return;
        }
        _session?.Write(input);
    }

    private void FlushPendingSessionInput()
    {
        var pending = _pendingSessionInput;
        _pendingSessionInput = null;
        if (pending is null || _session is not { } session)
            return;
        foreach (var input in pending)
            session.Write(input);
    }

    private void StartSessionIfReady()
    {
        if (_session is not null || _sessionStarting || RenderSelfTest.ReportPath is not null)
            return;
        StartSession();
        _terminal.ScrollToBottom();
    }

    private void StartSession()
    {
        if (_disposed || _session is not null || _sessionStarting)
            return;

        _sessionStarting = true;
        // A replacement may already have initialized the shell for a new tab.
        // If no prewarm matches, direct startup uses this pane's current grid.
        var cols = _cols >= 20 ? (short)_cols : (short)80;
        var rows = _rows >= 5 ? (short)_rows : (short)24;
        var workingDirectory = _workingDirectory;
        var shellId = AppSettings.Instance.ShellId;

        var prewarmLease = TerminalSessionPrewarmer.Take(workingDirectory, shellId);
        _prewarmLease = prewarmLease;
        if (prewarmLease != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var prewarmed = await prewarmLease.SessionTask.ConfigureAwait(false);
                    if (!TryAttachPrewarmedSession(prewarmLease, prewarmed))
                        throw new OperationCanceledException("Prewarm output was invalidated before attachment.");
                    prewarmLease.Dispose();
                    _prewarmLease = null;
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        _sessionStarting = false;
                        if (_disposed)
                        {
                            _session = null;
                            prewarmed.Dispose();
                            return;
                        }
                        if (_cols != cols || _rows != rows)
                            ApplySessionResize((short)_cols, (short)_rows);
                        FlushPendingSessionInput();
                    });
                }
                catch
                {
                    _session = null;
                    prewarmLease.Dispose();
                    _prewarmLease = null;
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        _sessionStarting = false;
                        StartSession();
                    });
                }
            });
            return;
        }

        _ = Task.Run(() =>
        {
            var resolved = ShellRegistry.Resolve(shellId);
            var shellProgram = SelfTestShell ?? resolved?.Program ?? TerminalSession.DefaultShell();
            var shellArguments = SelfTestShell is null ? resolved?.Arguments : null;

            TerminalSession session;
            try
            {
                session = CreateAndStartSession(workingDirectory, cols, rows, shellProgram, shellArguments);
            }
            catch
            {
                try
                {
                    session = CreateAndStartSession(workingDirectory, cols, rows, TerminalSession.DefaultShell(), null);
                }
                catch
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        _sessionStarting = false;
                        _pendingSessionInput = null;
                        _session = null;
                    });
                    return;
                }
            }
            StartupMark.Note("terminal spawn end");
            if (_disposed)
            {
                _session = null;
                session.Dispose();
                _ = Dispatcher.BeginInvoke(() => _sessionStarting = false);
                return;
            }
            _ = Dispatcher.BeginInvoke(() =>
            {
                _sessionStarting = false;
                if (_disposed)
                {
                    _session = null;
                    session.Dispose();
                    return;
                }
                if (_cols != cols || _rows != rows)
                    ApplySessionResize((short)_cols, (short)_rows);
                FlushPendingSessionInput();
            });
        });
    }

    private TerminalSession CreateAndStartSession(string workingDirectory, short cols, short rows, string? shell, string? arguments)
    {
        var session = new TerminalSession();
        var buffered = new List<ArraySegment<byte>>();
        var outputLock = new object();
        Action<ArraySegment<byte>> outputHandler = data =>
        {
            if (data.Array is not { } array) return;
            lock (outputLock)
            {
                var copy = System.Buffers.ArrayPool<byte>.Shared.Rent(data.Count);
                Buffer.BlockCopy(array, data.Offset, copy, 0, data.Count);
                System.Buffers.ArrayPool<byte>.Shared.Return(array);
                buffered.Add(new ArraySegment<byte>(copy, 0, data.Count));
            }
        };

        session.OutputReceived += data => outputHandler(data);
        session.OutputReceived += data =>
        {
            if (Interlocked.Exchange(ref _firstOutputNoted, 1) == 0)
                StartupMark.Note("first terminal output");
        };

        try
        {
            session.Start(workingDirectory, cols, rows, shell, arguments);
        }
        catch
        {
            lock (outputLock)
            {
                foreach (var chunk in buffered)
                {
                    if (chunk.Array is { } array)
                        System.Buffers.ArrayPool<byte>.Shared.Return(array);
                }
                buffered.Clear();
            }
            session.Dispose();
            throw;
        }

        // Feeding startup output can synchronously answer ConPTY's DA query.
        // Publish the session before replay so the answer has a destination.
        _session = session;
        lock (outputLock)
        {
            outputHandler = OnSessionOutput;
            foreach (var chunk in buffered)
                OnSessionOutput(chunk);
            buffered.Clear();
        }

        ObserveSessionExit(session);
        return session;
    }

    /// <summary>Only accepted prewarm streams may report process exit and close this pane.</summary>
    internal bool TryAttachPrewarmedSession(TerminalSessionPrewarmer.Lease lease, TerminalSession session)
    {
        // Query responses during replay need the session before it is accepted.
        _session = session;
        if (!lease.AttachOutputHandler(OnSessionOutput))
        {
            _session = null;
            return false;
        }
        ObserveSessionExit(session);
        return true;
    }

    /// <summary>Replays an exit that raced acceptance, without double-reporting the event.</summary>
    internal void ObserveSessionExit(TerminalSession session)
    {
        var reported = 0;
        Action<int> handler = code =>
        {
            if (Interlocked.Exchange(ref reported, 1) == 0)
                OnSessionExited(session, code);
        };
        _sessionExitedHandler = handler;
        session.Exited += handler;
        if (session.ExitCode is { } exitCode)
            handler(exitCode);
    }

    private void OnSessionOutput(ArraySegment<byte> chunk)
    {
        if (chunk.Array is not { } buffer)
            return;
        try
        {
            // Feed on this PTY reader thread, not on the UI thread. The
            // emulator answers queries (DSR, OSC color reports, DA) through
            // WritePty synchronously inside Feed, so responses reach ConPTY
            // in microseconds instead of waiting behind a UI-thread pump and
            // a full render pass — the wait Neovim's 100 ms
            // "Did not detect DSR response" timeout tripped on. Order is
            // preserved: chunks arrive here in pipe order and Feed serializes
            // them through the terminal lock.
            lock (_terminal.SyncRoot)
            {
                if (_disposed)
                    return;
                try
                {
                    _terminal.Feed(buffer, chunk.Offset, chunk.Count);
                }
                catch (Exception e)
                {
                    // Guard against malformed input states; the next redraw
                    // repaints everything from the emulator state.
                    var tail = Convert.ToHexString(buffer, Math.Max(0, chunk.Count - 24), Math.Min(24, chunk.Count));
                    Diag($"feed-EXCEPTION {e.GetType().Name}: {e.Message} chunk={chunk.Count} tail={tail}");
                    _needsFullRedraw = true;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        ScheduleRedraw();
    }

    private void OnSessionExited(TerminalSession source, int exitCode)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            // A rejected or replaced session can already have queued an exit.
            if (_disposed || !ReferenceEquals(_session, source))
                return;
            _terminal.Feed($"\r\n\x1b[2m[process exited with code {exitCode}]\x1b[m\r\n");
            FlushRedraw();
            ProcessExited?.Invoke(exitCode);
        });
    }

    /// <summary>
    /// Writes one emulator response (DSR, OSC/DA report, ...) into ConPTY.
    /// ConPTY parses each input write as a single key encoding and drops
    /// ESC-prefixed chunks that are not valid keys — an atomically written
    /// <c>ESC[0n</c> never reaches the child, which is exactly Neovim's
    /// "Did not detect DSR response" warning. Fragmenting at every ESC makes
    /// each chunk decode to plain key events (a lone ESC, then ordinary
    /// characters) that the child reassembles into the response bytes.
    /// Keystroke/mouse/paste input must stay atomic (arrows are <c>ESC[A</c>)
    /// and never goes through here.
    /// </summary>
    private void WriteResponseToPty(byte[] data, int length)
    {
        _session?.WriteResponse(data.AsSpan(0, length));
    }

}
