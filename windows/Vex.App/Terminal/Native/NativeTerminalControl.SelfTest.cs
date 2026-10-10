#if DEBUG || VEX_SELFTEST
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

/// <summary>
/// Test-only access into the control for <see cref="RenderSelfTest"/>. Kept in
/// a partial file so the diagnostics surface does not crowd the renderer.
/// Nothing in here runs unless the selftest harness drives it.
/// </summary>
public sealed partial class NativeTerminalControl
{
    /// <summary>Shell override for live self-test scenarios; null uses the
    /// configured default.</summary>
    internal string? SelfTestShell;

    private bool _selfTestCaret;

    internal int SelfTestCols => _cols;
    internal int SelfTestRows => _rows;
    internal double SelfTestCellWidth => _cellWidth;
    internal double SelfTestCellHeight => _cellHeight;

    /// <summary>Changes rendering metrics without persisting the user's font settings.</summary>
    internal void SelfTestFontMetrics(string family, double size)
    {
        ApplyTypefaces(family);
        _fontSize = size;
        RebuildFontMetrics();
        FlushRedraw();
    }

    /// <summary>Checks retained fallback glyphs are rebuilt for a monitor's new DPI.</summary>
    internal bool SelfTestFallbackDpiChange()
    {
        var originalDpi = _pixelsPerDip;
        var changedDpi = originalDpi * 1.2;
        SelfTestFeed("\u001b[2J\u001b[He\u0301 \U0001F9EA\u001b[2;1H");
        try
        {
            OnDpiChanged(new DpiScale(originalDpi, originalDpi), new DpiScale(changedDpi, changedDpi));
            FlushRedraw();
            var glyphRuns = _fallbackCellDrawings.Values.SelectMany(FallbackGlyphRuns).ToArray();
            return glyphRuns.Length > 0 && glyphRuns.All(run => Math.Abs(run.PixelsPerDip - changedDpi) < 0.001);
        }
        finally
        {
            OnDpiChanged(new DpiScale(changedDpi, changedDpi), new DpiScale(originalDpi, originalDpi));
            FlushRedraw();
        }

        static IEnumerable<GlyphRun> FallbackGlyphRuns(Drawing drawing)
        {
            if (drawing is GlyphRunDrawing { GlyphRun: { } run })
                yield return run;
            else if (drawing is DrawingGroup group)
                foreach (var child in group.Children)
                    foreach (var childRun in FallbackGlyphRuns(child))
                        yield return childRun;
        }
    }

    /// <summary>Checks shared glyph advances stay immutable and follow the monitor's pixel grid.</summary>
    internal bool SelfTestGlyphAdvanceDpiChange()
    {
        var originalDpi = _pixelsPerDip;
        SelfTestFeed("\x1b[2J\x1b[Habcdefghijklmnop\x1b[31mqrstuvwxyz\x1b[0m");
        var retained = _rowVisuals[0].Drawing.Children.OfType<GlyphRunDrawing>()
            .Select(drawing => drawing.GlyphRun).OfType<GlyphRun>().ToArray();
        var savedAdvances = retained.Select(run => run.AdvanceWidths.ToArray()).ToArray();
        try
        {
            var changedDpi = originalDpi * 1.2;
            OnDpiChanged(new DpiScale(originalDpi, originalDpi), new DpiScale(changedDpi, changedDpi));
            SelfTestFeed("\x1b[H0123456789abcdef\x1b[31mghijklmnop\x1b[0m");
            var current = _rowVisuals[0].Drawing.Children.OfType<GlyphRunDrawing>()
                .Select(drawing => drawing.GlyphRun).OfType<GlyphRun>().ToArray();
            return retained.Length >= 2 && current.Length >= 2
                && retained.Select((run, i) => run.AdvanceWidths.SequenceEqual(savedAdvances[i])).All(equal => equal)
                && current.All(run =>
                {
                    var endCol = Math.Round(run.BaselineOrigin.X / _cellWidth) + run.GlyphIndices.Count;
                    var expectedEnd = Math.Round(endCol * _cellWidth * changedDpi) / changedDpi;
                    return Math.Abs(run.BaselineOrigin.X + run.AdvanceWidths.Sum() - expectedEnd) < 0.001;
                });
        }
        finally
        {
            OnDpiChanged(new DpiScale(_pixelsPerDip, _pixelsPerDip), new DpiScale(originalDpi, originalDpi));
            FlushRedraw();
        }
    }

    /// <summary>Measures forced row rendering on the UI thread, including retained WPF allocations.</summary>
    internal string SelfTestBenchRendering(int iterations)
    {
        for (var warmup = 0; warmup < 10; warmup++)
            RedrawAll(force: true);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var iteration = 0; iteration < iterations; iteration++)
            RedrawAll(force: true);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        return FormattableString.Invariant($"render ms/frame={elapsed.TotalMilliseconds / iterations:F3} bytes/frame={(GC.GetAllocatedBytesForCurrentThread() - allocated) / iterations}");
    }

    /// <summary>The visible frame row for a viewport row, or null.</summary>
    internal FrameRow? SelfTestLine(int row)
    {
        if (row < 0 || row >= _terminal.FrameRows.Length)
            return null;
        return _terminal.FrameRows[row];
    }

    /// <summary>Viewport cell currently occupied by the drawn caret, or null.</summary>
    internal (int Row, int Col)? SelfTestCaretCell()
    {
        var cursor = _terminal.Cursor;
        if (!cursor.Visible || cursor.Y < 0 || cursor.Y >= _rows)
            return null;
        return (cursor.Y, Math.Min(cursor.X, _cols - 1));
    }

    /// <summary>Colors a cell renders with: the resolved background brush
    /// color, or the theme background when the cell carries no background
    /// (the row leaves the base layer visible).</summary>
    internal (Color? Background, Color Base) SelfTestResolveCell(in CellInfo cell)
    {
        _palette.Resolve(cell.FgTag, cell.FgValue, cell.BgTag, cell.BgValue, cell.Flags, out _, out var bg);
        var baseColor = _palette.Background is SolidColorBrush baseBrush ? baseBrush.Color : default;
        return (bg is SolidColorBrush brush ? brush.Color : (Color?)null, baseColor);
    }

    /// <summary>Checks VT cursor mode changes stop and restart blinking immediately.</summary>
    internal bool SelfTestCursorBlinkModes()
    {
        var cursor = _terminal.Cursor;
        var blinkSetting = _cursorBlinkSetting;
        var selfTestCaret = _selfTestCaret;
        var restoreStyle = cursor.Shape switch
        {
            CursorShape.Block => 1,
            CursorShape.Underline => 3,
            _ => 5,
        };
        if (!cursor.Blinking)
            restoreStyle++;
        try
        {
            Window.GetWindow(this)?.Activate();
            Focus();
            if (!IsKeyboardFocused)
                return false;
            _cursorBlinkSetting = true;
            _selfTestCaret = true;
            SelfTestFeed("\x1b[?25h\x1b[1 q");
            var blinking = _terminal.Cursor.Blinking && _blinkTimer.IsEnabled;
            SelfTestFeed("\x1b[2 q");
            _caretBlinkVisible = false;
            DrawCaret();
            var steady = !_terminal.Cursor.Blinking && !_blinkTimer.IsEnabled && _caretCacheShouldDraw;
            SelfTestFeed("\x1b[?25l\x1b[1 q");
            var hidden = !_terminal.Cursor.Visible && !_blinkTimer.IsEnabled && !_caretCacheShouldDraw;
            SelfTestFeed("\x1b[?25h");
            return blinking && steady && hidden && _blinkTimer.IsEnabled && _caretCacheShouldDraw;
        }
        finally
        {
            _cursorBlinkSetting = blinkSetting;
            _selfTestCaret = selfTestCaret;
            SelfTestFeed($"\x1b[{restoreStyle} q\x1b[?25{(cursor.Visible ? "h" : "l")}");
            UpdateBlinkTimer();
        }
    }

    /// <summary>Checks input precedence and eventual application of a queued output flush.</summary>
    internal bool SelfTestInputPrecedesOutputRedraw()
    {
        _terminal.Feed("\x1b[H!");
        var inputSawPendingRedraw = false;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            inputSawPendingRedraw = _redrawScheduled);
        ScheduleRedraw();
        SelfTestWaitForOutputRedraw();
        return inputSawPendingRedraw && !_redrawScheduled
            && _terminal.FrameRows[0].Cells[0].Text == "!";
    }

    private void SelfTestWaitForOutputRedraw()
    {
        if (!_redrawScheduled)
            return;
        var frame = new DispatcherFrame();
        // Output is frame-paced; dispatcher idle can precede the final redraw.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var deadline = new DispatcherTimer(DispatcherPriority.ContextIdle, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        deadline.Tick += (_, _) =>
        {
            if (_redrawScheduled && started.Elapsed < TimeSpan.FromSeconds(2))
                return;
            deadline.Stop();
            frame.Continue = false;
        };
        deadline.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Verifies hidden output is deferred while terminal events stay live.</summary>
    internal bool SelfTestHiddenOutputCatchUp(Visibility hiddenVisibility)
    {
        SelfTestFeed("\x1b[?1049l\x1b[2J\x1b[Hbefore");
        var hashes = _rowHashes.ToArray();
        var modes = new List<bool>();
        var bell = false;
        var title = "";
        var response = "";
        void OnMode(bool alternate) => modes.Add(alternate);
        void OnBell() => bell = true;
        void OnTitle(string value) => title = value;
        void OnResponse(byte[] bytes, int length) => response += Encoding.ASCII.GetString(bytes, 0, length);
        TuiModeChanged += OnMode;
        Bell += OnBell;
        TitleRawChanged += OnTitle;
        _terminal.WritePty += OnResponse;
        var visibility = Visibility;
        try
        {
            Visibility = hiddenVisibility;
            SelfTestFeed("\x1b[?1049h\x1b[2J\x1b[Hhidden-alt\x1b[?1003h\a\x1b]0;hidden-title\a\x1b[6n");
            var deferred = _rowHashes.SequenceEqual(hashes)
                && _terminal.FrameRows[0].Cells[0].Text == "b";
            var frame = new DispatcherFrame();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            var eventsLive = modes.SequenceEqual(new[] { true }) && bell && title == "hidden-title"
                && response.Contains("\x1b[1;11R", StringComparison.Ordinal);
            Visibility = visibility;
            var caughtUp = string.Concat(_terminal.FrameRows[0].Cells.Select(cell => cell.Text)).StartsWith("hidden-alt")
                && _mouseTracking;
            SelfTestFeed("\x1b[?1003l\x1b[?1049l");
            return deferred && eventsLive && caughtUp && modes.SequenceEqual(new[] { true, false });
        }
        finally
        {
            Visibility = visibility;
            TuiModeChanged -= OnMode;
            Bell -= OnBell;
            TitleRawChanged -= OnTitle;
            _terminal.WritePty -= OnResponse;
        }
    }
    /// <summary>Checks deferred output after a real detach/reattach with unchanged geometry.</summary>
    internal bool SelfTestDetachedOutputCatchUp()
    {
        if (VisualTreeHelper.GetParent(this) is not ContentPresenter presenter
            || !ReferenceEquals(presenter.Content, this))
            return false;
        SelfTestFeed("\x1b[?1049l\x1b[2J\x1b[Hbefore-detach");
        // Live tabs take the unchanged-grid shortcut; exercise it without
        // launching a shell in this deterministic renderer scenario.
        var sessionStarting = _sessionStarting;
        _sessionStarting = true;
        try
        {
            presenter.SetCurrentValue(ContentPresenter.ContentProperty, null);
            var detached = new DispatcherFrame();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => detached.Continue = false);
            Dispatcher.PushFrame(detached);
            if (IsVisible || IsLoaded)
                return false;
            SelfTestFeed("\x1b[2J\x1b[Hdetached-latest");
            var deferred = _terminal.FrameRows[0].Cells[0].Text == "b";
            presenter.SetCurrentValue(ContentPresenter.ContentProperty, this);
            var attached = new DispatcherFrame();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => attached.Continue = false);
            Dispatcher.PushFrame(attached);
            return deferred && IsLoaded && IsVisible
                && string.Concat(_terminal.FrameRows[0].Cells.Select(cell => cell.Text)).StartsWith("detached-latest");
        }
        finally
        {
            _sessionStarting = sessionStarting;
            presenter.SetCurrentValue(ContentPresenter.ContentProperty, this);
        }
    }
    internal void SelfTestFeed(string text)
    {
        _terminal.Feed(text);
        FlushRedraw();
    }

    /// <summary>Feed several chunks then flush once, mirroring the output
    /// pump's drain-then-repaint pattern.</summary>
    internal void SelfTestFeedBatch(params string[] chunks)
    {
        foreach (var chunk in chunks)
            _terminal.Feed(chunk);
        FlushRedraw();
    }

    /// <summary>Starts a real ConPTY session even in selftest mode (where the
    /// normal startup path is disabled), sized to the current grid.</summary>
    internal void SelfTestStartSession()
    {
        if (_session is not null)
            return;
        StartSession();
        _session?.Resize((short)_cols, (short)_rows);
    }

    /// <summary>Writes bytes to the live session's PTY input, as typed keys
    /// would; output flows back through the normal async pump.</summary>
    internal void SelfTestType(string text)
    {
        if (_session is null)
            return;
        var bytes = Encoding.UTF8.GetBytes(text);
        Diag($"type '{text.Replace("\r", "<CR>").Replace("\x1b", "<ESC>")}'");
        _session.Write(bytes);
    }

    /// <summary>Feed raw bytes in fixed-size chunks (like ConPTY delivery),
    /// one flush at the end — the pump's exact drain pattern.</summary>
    internal void SelfTestFeedBytes(byte[] data, int chunkSize = 64)
    {
        for (var off = 0; off < data.Length; off += chunkSize)
        {
            var n = Math.Min(chunkSize, data.Length - off);
            _terminal.Feed(data, off, n);
        }
        FlushRedraw();
    }

    internal void SelfTestScroll(int lines)
    {
        _terminal.ScrollBy(lines);
        FlushRedraw();
    }

    internal void SelfTestScrollToBottom()
    {
        _terminal.ScrollToBottom();
        FlushRedraw();
    }

    /// <summary>Configures a terminal mouse mode, encodes one event through
    /// libghostty, and returns the wire bytes in an escaped readable form.</summary>
    internal string SelfTestMouseReport(string modes, MouseInputAction action,
        MouseInputButton? button, bool anyButtonPressed = false, int col = 5)
    {
        _terminal.Feed("\x1b[?9l\x1b[?1000l\x1b[?1002l\x1b[?1003l" +
                       "\x1b[?1005l\x1b[?1006l\x1b[?1015l\x1b[?1016l" + modes);
        var output = new byte[128];
        // Use the geometry supplied to the encoder, not truncated WPF DIPs.
        var len = _terminal.EncodeMouse(action, button, MouseInputModifiers.None,
            _nativeCellWidth * col + 1, _nativeCellHeight * 2 + 1, anyButtonPressed, output);
        return Encoding.Latin1.GetString(output, 0, len).Replace("\x1b", "<ESC>");
    }

    /// <summary>The live tracking-mode snapshot the WPF mouse handlers route
    /// on, refreshed the same way FlushRedraw does.</summary>
    internal bool SelfTestMouseTracking
    {
        get { _mouseTracking = _terminal.MouseTracking; return _mouseTracking; }
    }

    /// <summary>Sends one mouse event through the exact SendMouse path the
    /// WPF handlers use (same encoder, same coordinate scaling, same PTY
    /// write), addressed in grid cells for live scenarios.</summary>
    internal void SelfTestMouse(MouseInputAction action, MouseInputButton? button, double col, double row)
    {
        SendMouse(action, button, new System.Windows.Point(
            col * _cellWidth + _cellWidth / 2,
            row * _cellHeight + _cellHeight / 2));
    }

    internal int[] SelfTestWheelSteps(params int[] deltas)
    {
        _wheelDeltaRemainder = 0;
        var steps = new int[deltas.Length];
        for (var i = 0; i < deltas.Length; i++)
            steps[i] = ConsumeWheelSteps(deltas[i]);
        return steps;
    }

    internal string SelfTestBenchMouseEncoding(int iterations)
    {
        _terminal.Feed("\x1b[?1003h\x1b[?1006h");
        var output = new byte[128];
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bytes = 0;
        for (var i = 0; i < iterations; i++)
        {
            bytes += _terminal.EncodeMouse(MouseInputAction.Motion, null, MouseInputModifiers.None,
                _nativeCellWidth * (i % Math.Max(1, _cols)) + 1, _nativeCellHeight * 2 + 1,
                anyButtonPressed: false, output);
        }
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _terminal.Feed("\x1b[?1003l\x1b[?1006l");
        return $"mouse-encode x{iterations} ms={watch.Elapsed.TotalMilliseconds:F2} allocKB={allocated / 1024.0:F1} bytes={bytes}";
    }

    /// <summary>Runs the link scan over every visible row repeatedly and
    /// reports wall time plus allocations — the scan's own cost, without
    /// the render pass the flood scenario includes.</summary>
    internal string SelfTestBenchLinkScan(int iterations)
    {
        var rows = _terminal.FrameRows;
        var rowCount = Math.Min(_rows, rows.Length);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            for (var r = 0; r < rowCount; r++)
                ComputeRowLinks(rows[r], r);
        }
        watch.Stop();
        var alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        return $"link-scan x{iterations} rows={rowCount} ms={watch.Elapsed.TotalMilliseconds:F2} allocKB={alloc / 1024.0:F0}";
    }

    /// <summary>Runs the exact press-drag-release sequence the mouse handlers
    /// use, so the selection gesture (and its grid_ref calls) is testable
    /// without real pointer input. Ghostty includes a cell only when the
    /// pointer passes its 60%-width threshold, so the press lands left of it
    /// and the drag right of it (like a real left-to-right drag).</summary>
    internal void SelfTestSelect(int pressCol, int pressRow, int dragCol, int dragRow)
    {
        _kbSelectionActive = false;
        _selectionActive = true;
        _selectionDragged = true;
        var pressX = pressCol * _cellWidth + _cellWidth * 0.2;
        var pressY = pressRow * _cellHeight + _cellHeight * 0.5;
        var dragX = dragCol * _cellWidth + _cellWidth * 0.8;
        var dragY = dragRow * _cellHeight + _cellHeight * 0.5;
        var nativePress = NativePoint(new Point(pressX, pressY));
        var nativeDrag = NativePoint(new Point(dragX, dragY));
        _terminal.SelectionPress(pressCol, pressRow, nativePress.X, nativePress.Y);
        _terminal.SelectionDrag(dragCol, dragRow, nativeDrag.X, nativeDrag.Y);
        _terminal.SelectionRelease(dragCol, dragRow);
        FlushRedraw();
    }

    /// <summary>Completes a mouse selection through the real release and clipboard path.</summary>
    internal void SelfTestSelectionMouseUp()
    {
        _mouseSelectionOverride = _mouseTracking;
        OnMouseLeftButtonUp(new MouseButtonEventArgs(
            Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left));
    }

    /// <summary>Copies through the same path used by the clipboard shortcut.</summary>
    internal void SelfTestCopySelection() => CopySelection();

    /// <summary>Cuts through the same path used by the clipboard shortcut.</summary>
    internal void SelfTestCutSelection() => CutSelection();

    /// <summary>Plain text of the active selection, or null when there is none.</summary>
    internal string? SelfTestSelectedText() => _terminal.HasSelection ? _terminal.GetSelectedText() : null;

    /// <summary>Clears selection before comparing terminal feedback pixels.</summary>
    internal void SelfTestClearSelection() => ClearSelection();

    /// <summary>Samples copy feedback without taking ownership of the system clipboard.</summary>
    internal void SelfTestCopyFeedback(double progress)
    {
        if (progress == 0)
        {
            StartCopyAnimation();
            ClearSelection();
        }
        else if (progress >= 1)
            StopCopyAnimation();
        else
            DrawCopyAnimation(progress);
    }

    /// <summary>Exercises application copy feedback without a Vex selection.</summary>
    internal void SelfTestApplicationCopy(string text) => ShowApplicationCopyFeedback(text);

    /// <summary>Drives link hover without moving the user's desktop pointer.</summary>
    internal void SelfTestLinkHover(int col, int row)
    {
        // A pending redraw refreshes hover from the real desktop pointer;
        // let it finish before applying this fixture's synthetic pointer.
        SelfTestWaitForOutputRedraw();
        UpdateLinkHover(col, row);
    }

    /// <summary>Checks delayed link preview through WPF layout and captures its popup.</summary>
    internal bool SelfTestLinkPreview(string path)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        if (_linkToolTip is not { IsOpen: true, ActualWidth: > 0, ActualHeight: > 0 } preview)
            return false;
        var dpi = VisualTreeHelper.GetDpi(preview).PixelsPerDip;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(preview.ActualWidth * dpi),
            (int)Math.Ceiling(preview.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(preview);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return true;
    }

    /// <summary>Captures bytes from the real Backspace key handler without writing to a shell.</summary>
    internal byte[] SelfTestSelectionBackspace()
    {
        var starting = _sessionStarting;
        var pending = _pendingSessionInput;
        try
        {
            _sessionStarting = true;
            _pendingSessionInput = new List<byte[]>();
            OnKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this),
                Environment.TickCount, Key.Back) { RoutedEvent = Keyboard.KeyDownEvent });
            return _pendingSessionInput.SelectMany(static bytes => bytes).ToArray();
        }
        finally
        {
            _sessionStarting = starting;
            _pendingSessionInput = pending;
        }
    }

    /// <summary>Exercises the real Shift+Arrow caret movement and selection path.</summary>
    internal void SelfTestKeyboardSelectionKey(Key key, bool byWord = false)
        => ExtendKeyboardSelection(key, byWord);

    /// <summary>The detected URL under a viewport cell, or null. Recomputes
    /// the row's spans directly so the harness can query rows that were not
    /// repainted since their content changed.</summary>
    internal string? SelfTestLinkAt(int row, int col)
    {
        if (row < 0 || row >= _terminal.FrameRows.Length)
            return null;
        _rowLinks[row] = ComputeRowLinks(_terminal.FrameRows[row], row);
        return LinkUriAt(col, row);
    }

    internal string SelfTestScrollInfo()
    {
        var sb = _terminal.Scrollbar;
        return $"scrollbar total={sb.Total} offset={sb.Offset} len={sb.Len}";
    }

    internal string SelfTestCursorInfo()
    {
        var cursor = _terminal.Cursor;
        return $"cursor x={cursor.X} y={cursor.Y} visible={cursor.Visible} blink={cursor.Blinking} shape={cursor.Shape}";
    }

    internal string SelfTestRowText(int row)
    {
        if (row < 0 || row >= _terminal.FrameRows.Length)
            return "no-row";
        var sb = new StringBuilder();
        foreach (var cell in _terminal.FrameRows[row].Cells)
            sb.Append(cell.Text.Length > 0 ? cell.Text : " ");
        return sb.ToString().TrimEnd();
    }

    internal void SelfTestStabilizeCaret()
    {
        _selfTestCaret = true;
        _cursorBlinkSetting = false;
        UpdateBlinkTimer();
        DrawCaret();
    }

    /// <summary>Redraw passes that threw since the run started.</summary>
    internal int SelfTestRenderFailures => _renderFailures;

    /// <summary>Repaints the whole surface from the emulator buffer, the way a
    /// palette/font change does. The pixel-parity scenarios compare this
    /// against the incremental paint: any difference is a pixel the
    /// incremental path left stale.</summary>
    internal void SelfTestFullRedraw()
    {
        _needsFullRedraw = true;
        FlushRedraw();
    }

    /// <summary>Rebuilds the grid at a new size exactly like
    /// <see cref="RecalculateGridSize"/> does on a pane resize: same cell
    /// geometry, new row/column counts, ConPTY and emulator told, paint
    /// caches dropped.</summary>
    internal void SelfTestResizeGrid(int cols, int rows)
    {
        var gridChanged = cols != _cols || rows != _rows;
        _cols = cols;
        _rows = rows;
        EnsureRowVisuals();
        if (gridChanged)
            InvalidateRowPaintCaches();
        _terminal.Resize(cols, rows, _nativeCellWidth, _nativeCellHeight);
        _needsFullRedraw = true;
        FlushRedraw();
    }
}
#else
namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    internal static readonly string? SelfTestShell = null;
    private const bool _selfTestCaret = false;
}
#endif
