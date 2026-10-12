using System.Windows.Media;
using System.Windows.Threading;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private void QueueTerminalTitle(string title)
    {
        if (Dispatcher.CheckAccess())
        {
            lock (_outputLock)
                _pendingTitle = null;
            if (!_disposed)
            {
                TitleRawChanged?.Invoke(title);
                TitleChanged?.Invoke(title);
            }
            return;
        }
        // Build tools can update their title on every output chunk. Only the
        // newest title matters; intermediate UI work would delay input.
        lock (_outputLock)
        {
            if (_disposed) return;
            _pendingTitle = title;
            if (_titleUpdateQueued) return;
            _titleUpdateQueued = true;
        }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            string? latest;
            lock (_outputLock)
            {
                latest = _pendingTitle;
                _pendingTitle = null;
                _titleUpdateQueued = false;
            }
            if (!_disposed && latest is not null)
            {
                TitleRawChanged?.Invoke(latest);
                TitleChanged?.Invoke(latest);
            }
        });
    }

    private bool _interactiveRedrawScheduled;
    private bool _outputRenderingSubscribed;
    private readonly record struct OutputCompositionFrame(TimeSpan RenderingTime, TimeSpan NextFrameTime);
    private OutputCompositionFrame? _outputCompositionFrame;
    private TimeSpan _outputFrameInterval = TimeSpan.FromSeconds(1.0 / TerminalDisplayRefreshRate.DefaultRefreshRate);
    private long _nextDisplayRefreshQuery;
    private const int OutputFallbackIntervalMs = 100;

    private void ScheduleRedraw(bool interactive = false)
    {
        lock (_outputLock)
        {
            if (_disposed || (_redrawScheduled && (!interactive || _interactiveRedrawScheduled)))
                return;
            _redrawScheduled = true;
            _interactiveRedrawScheduled = interactive;
        }

        Action callback = interactive ? FlushScheduledOutput : ScheduleOutputFrame;
        _ = Dispatcher.BeginInvoke(interactive ? DispatcherPriority.Input : DispatcherPriority.Background, callback);
    }

    private void ScheduleOutputFrame()
    {
        if (_disposed)
            return;
        lock (_outputLock)
        {
            if (!_redrawScheduled || _interactiveRedrawScheduled)
                return;
        }

        var elapsed = Environment.TickCount64 - _lastOutputRedraw;
        // The first output after idle should not wait for a composition tick.
        if (elapsed >= OutputFallbackIntervalMs)
        {
            FlushScheduledOutput();
            return;
        }
        if (IsVisible)
        {
            var now = Environment.TickCount64;
            if (now >= _nextDisplayRefreshQuery)
            {
                _outputFrameInterval = TimeSpan.FromSeconds(1.0 / TerminalDisplayRefreshRate.ReadRefreshRate(this));
                _nextDisplayRefreshQuery = now + 1000;
            }
            if (!_outputRenderingSubscribed)
            {
                CompositionTarget.Rendering += OnOutputRendering;
                _outputRenderingSubscribed = true;
            }
        }
        if (_outputRedrawTimer is null)
        {
            _outputRedrawTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _outputRedrawTimer.Tick += (_, _) => FlushScheduledOutput();
        }
        // Minimized/disconnected windows may stop delivering composition ticks.
        // Hidden panes need mode notifications, but no display-rate snapshots.
        _outputRedrawTimer.Interval = TimeSpan.FromMilliseconds(IsVisible
            ? OutputFallbackIntervalMs
            : Math.Max(1, OutputFallbackIntervalMs - elapsed));
        _outputRedrawTimer.Start();
    }

    private void OnOutputRendering(object? sender, EventArgs args)
    {
        if (args is RenderingEventArgs frame && !TryAdvanceOutputFrame(frame.RenderingTime))
            return;
        FlushScheduledOutput();
    }

    private bool TryAdvanceOutputFrame(TimeSpan time)
    {
        var next = time + _outputFrameInterval;
        if (_outputCompositionFrame is { } previous && time >= previous.RenderingTime)
        {
            // RenderingTime is millisecond-quantized. Keep a display-rate
            // deadline instead of dropping every rounded-down 60 Hz tick.
            if (time == previous.RenderingTime || time + TimeSpan.FromMilliseconds(1) < previous.NextFrameTime)
                return false;
            next = previous.NextFrameTime + _outputFrameInterval;
            if (next <= time)
                next = time + _outputFrameInterval;
        }
        _outputCompositionFrame = new OutputCompositionFrame(time, next);
        return true;
    }

    private void StopOutputRedraw()
    {
        _outputRedrawTimer?.Stop();
        if (_outputRenderingSubscribed)
        {
            CompositionTarget.Rendering -= OnOutputRendering;
            _outputRenderingSubscribed = false;
        }
    }

    private void FlushScheduledOutput()
    {
        StopOutputRedraw();
        // Clear before flushing so output arriving during the snapshot
        // schedules the next frame, including its tail.
        lock (_outputLock)
        {
            if (!_redrawScheduled)
                return;
            _redrawScheduled = false;
            _interactiveRedrawScheduled = false;
        }
        _lastOutputRedraw = Environment.TickCount64;
        if (!_disposed)
            FlushRedraw();
    }

    private void FlushRedraw()
    {
        if (_disposed)
            return;
        // Copy the static so the null state of the stopwatch matches the
        // guard below without flow-tracking a static field read.
        var diagPath = DiagPath;
        var flushStarted = diagPath is null ? null : System.Diagnostics.Stopwatch.StartNew();
#if DEBUG || VEX_SELFTEST
        var performance = _performanceSample;
        var performanceStarted = performance is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        // A busy reader must not stall every tab by making the UI wait for
        // native parsing. Retry a later frame instead; keep the terminal gate
        // for this pass so subsequent mode/selection reads cannot block either.
        if (!Monitor.TryEnter(_terminal.SyncRoot))
        {
#if DEBUG || VEX_SELFTEST
            if (performance is not null) performance.BusyParserSkips++;
#endif
            ScheduleRedraw();
            return;
        }
        try
        {
            // Detect alternate-screen transitions (TUI start/exit) and notify
            // the pane model so it can update the state indicator. Force a
            // full rebuild: alt enter/exit rematerializes every row and dirty
            // bits alone have under-reported the swap.
            var isAlt = _terminal.IsAlternateScreen;
            if (isAlt != _wasAlternateScreen)
            {
                _wasAlternateScreen = isAlt;
                _needsFullRedraw = true;
                _terminal.InvalidateCellCache();
                TuiModeChanged?.Invoke(isAlt);
            }

            // Hidden tabs keep parsing output and publishing their TUI state,
            // but defer snapshots and drawing until the view is shown again.
            if (!IsVisible && !_renderingPreview)
            {
                _needsFullRedraw = true;
                return;
            }

#if DEBUG || VEX_SELFTEST
            var snapshotStarted = performance is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            _terminal.UpdateFrame();
#if DEBUG || VEX_SELFTEST
            if (performance is not null)
                performance.SnapshotMilliseconds.Add(System.Diagnostics.Stopwatch.GetElapsedTime(snapshotStarted).TotalMilliseconds);
#endif
            UpdateBlinkTimer();
            var dirty = _terminal.FrameDirty;
            _mouseTracking = _terminal.MouseTracking;
            var scrollbar = _terminal.Scrollbar;
            var viewportMoved = scrollbar.Offset != _lastScrollOffset;
            _lastScrollOffset = scrollbar.Offset;

            if (flushStarted is not null)
                Diag($"flush dirty={dirty} full={_needsFullRedraw} scroll={viewportMoved} offset={scrollbar.Offset}/{scrollbar.Total} rows={_rows} cols={_cols} ms={flushStarted.Elapsed.TotalMilliseconds:F4}");

            // Synchronized output (DEC 2026) publishes one atomic frame, and
            // the emulator already exposes the rows the application has
            // written so far. Painting mid-block shows a torn screen: new text
            // in the rows reached so far, the previous frame in the rest — the
            // duplicated/ghost lines seen while a TUI or an agent streams.
            // Defer instead; the flag forces a complete repaint the moment the
            // block closes, so nothing half-applied is ever shown.
            if (_terminal.SynchronizedOutput)
            {
                var now = Environment.TickCount64;
                if (_syncDeferSince == 0)
                    _syncDeferSince = now;
                // One bounded deferral per continuous block: an application
                // that leaves the mode set paints normally from here on
                // instead of batching every frame behind the limit.
                if (now - _syncDeferSince < SyncDeferralLimitMs)
                {
                    _needsFullRedraw = true;
                    if (diagPath is not null)
                        Diag("flush sync-deferred");
                    return;
                }
            }
            else
            {
                _syncDeferSince = 0;
            }

            if (_needsFullRedraw || dirty == FrameDirty.Full || viewportMoved)
            {
                // Preview catch-up scans every row, but only repaints rows
                // whose content or render settings changed while hidden.
                RedrawAll(force: !_renderingPreview);
            }
            else if (dirty == FrameDirty.Partial)
            {
                // Scan all rows: unchanged rows skip in microseconds via RowHash;
                // any row that shifted, scrolled, or updated repaints cleanly.
                // Do not trust FrameRow.Dirty alone — it under-reports shifts.
                for (var row = 0; row < Math.Min(_rows, _terminal.FrameRows.Length); row++)
                {
                    RedrawRow(row, force: false);
                }
                InvalidateCaretCache();
            }

            _needsFullRedraw = false;

            DrawSelection();
            RefreshLinkHover();
            DrawCaret();
            if (!IsScrollbarVisible() && _scrollbarTargetOpacity > 0)
                SetScrollbarOpacity(0);
            else
                DrawScrollbar();
            if (_pathCompletionOpen)
                DrawPathCompletion(trackCursorOnly: true);
#if DEBUG || VEX_SELFTEST
            _promptPainted?.Invoke();
#endif
        }
        catch (Exception e)
        {
            // Guard against emulator state issues; schedule a full redraw on
            // the next pump cycle.
            Diag($"flush-EXCEPTION {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n')[0]}");
            _renderFailures++;
            _needsFullRedraw = true;
        }
        finally
        {
            Monitor.Exit(_terminal.SyncRoot);
#if DEBUG || VEX_SELFTEST
            if (performance is not null)
                performance.FlushMilliseconds.Add(System.Diagnostics.Stopwatch.GetElapsedTime(performanceStarted).TotalMilliseconds);
#endif
        }
    }
}
