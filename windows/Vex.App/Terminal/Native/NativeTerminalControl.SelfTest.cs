using System.Text;
using System.Windows;
using System.Windows.Media;
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
        var nativeCellWidth = Math.Max(1, (int)_cellWidth);
        var nativeCellHeight = Math.Max(1, (int)_cellHeight);
        var len = _terminal.EncodeMouse(action, button, MouseInputModifiers.None,
            nativeCellWidth * col + 1, nativeCellHeight * 2 + 1, anyButtonPressed, output);
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
        var cellWidth = Math.Max(1, (int)_cellWidth);
        var cellHeight = Math.Max(1, (int)_cellHeight);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bytes = 0;
        for (var i = 0; i < iterations; i++)
        {
            bytes += _terminal.EncodeMouse(MouseInputAction.Motion, null, MouseInputModifiers.None,
                cellWidth * (i % Math.Max(1, _cols)) + 1, cellHeight * 2 + 1,
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

    /// <summary>Plain text of the active selection, or null when there is none.</summary>
    internal string? SelfTestSelectedText() => _terminal.HasSelection ? _terminal.GetSelectedText() : null;

    /// <summary>Full press-drag-release sequence the keyboard-selection
    /// handler replays on every Shift+Arrow: ghostty commits the selection
    /// only on release, so the gesture must complete despite there being no
    /// real pointer button.</summary>
    internal void SelfTestKeyboardSelect(int pressCol, int pressRow, int dragCol, int dragRow)
    {
        _selectionActive = true;
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
}
