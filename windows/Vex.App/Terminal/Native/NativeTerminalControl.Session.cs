using System.ComponentModel;
using System.Windows.Threading;
using Vex.App.Model;
using Vex.Terminal;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private volatile TerminalSession? _session;
    private TerminalSessionPrewarmer.Lease? _prewarmLease;
    private bool _sessionStarting;
    private int _firstOutputNoted;

    // After a burst of size changes (window drag, sidebar), one settle pass
    // re-syncs ConPTY and forces a full cell read so wrapped lines do not
    // stay broken if an intermediate resize failed or raced the paint.
    private DispatcherTimer? _resizeSettleTimer;
    private short _pendingSessionCols;
    private short _pendingSessionRows;
    private bool _sessionResizePending;

    // While the sidebar animates, the window width changes every frame and
    // each size event would resize the VT emulator (buffer reflow) plus the
    // ConPTY session. MainWindow suspends resizes for the animation duration
    // and calls ResumeResizes() when it settles.
    internal static bool ResizeSuspended { get; private set; }

    /// <summary>Raised on the UI thread when resize suspension ends. Every
    /// live control then recalculates its grid exactly once — necessary
    /// because the suspension swallows all intermediate size events and the
    /// final width usually equals the last animated frame, which fires no
    /// SizeChanged at all. Without this the grid (and the ConPTY size behind
    /// it) stays at the pre-animation dimensions: a fullscreen TUI like nvim
    /// then covers only part of the control and the rest renders as bare
    /// background.</summary>
    private static event Action? ResizeSuspensionEnded;

    internal static void SuspendResizes() => ResizeSuspended = true;

    internal static void ResumeResizes()
    {
        if (!ResizeSuspended)
            return;
        ResizeSuspended = false;
        ResizeSuspensionEnded?.Invoke();
    }

    /// <summary>PID of the ConPTY shell process; null before the session starts.</summary>
    public int? ProcessId => _session?.ProcessId;

    private void ApplySessionResize(short cols, short rows)
    {
        var session = _session;
        if (session is null)
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

    private void StartSessionIfReady()
    {
        if (_session is not null || _sessionStarting || RenderSelfTest.ReportPath is not null)
            return;
        StartSession();
        _terminal.ScrollToBottom();
    }

    private void StartSession()
    {
        if (_session is not null || _sessionStarting)
            return;

        _sessionStarting = true;
        // New tabs should not create a second, short-lived prewarm. The
        // prewarmer is single-use and only belongs to the initial restored
        // pane; subsequent panes start directly with their actual geometry.
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
                    _session = prewarmed;
                    prewarmed.Exited += OnSessionExited;
                    prewarmLease.AttachOutputHandler(OnSessionOutput);
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
                    });
                }
                catch
                {
                    _session = null;
                    prewarmLease.Dispose();
                    _prewarmLease = null;
                    _sessionStarting = false;
                    _ = Dispatcher.BeginInvoke(StartSession);
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
            catch (Win32Exception)
            {
                session = CreateAndStartSession(workingDirectory, cols, rows, TerminalSession.DefaultShell(), null);
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
            });
        });
    }

    private TerminalSession CreateAndStartSession(string workingDirectory, short cols, short rows, string? shell, string? arguments)
    {
        var session = new TerminalSession();
        _session = session;
        session.OutputReceived += OnSessionOutput;
        session.Exited += OnSessionExited;
        session.OutputReceived += data =>
        {
            if (Interlocked.Exchange(ref _firstOutputNoted, 1) == 0)
                StartupMark.Note("first terminal output");
        };
        session.Start(workingDirectory, cols, rows, shell, arguments);
        return session;
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

    private void OnSessionExited(int exitCode)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
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

    private void OnResizeSuspensionEnded()
    {
        if (_disposed)
            return;
        if (Dispatcher.CheckAccess())
            RecalculateGridSize();
        else
            _ = Dispatcher.BeginInvoke(RecalculateGridSize);
    }
}
