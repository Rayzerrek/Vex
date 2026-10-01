using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private bool _selectionActive;
    private bool _selectionDragged;
    private bool _selectionGestureActive;
    private int _selectionClickCount;
    private bool _mouseSelectionOverride;
    private bool _kbSelectionActive;
    private int _kbAnchorCol, _kbAnchorRow;
    private int _kbFocusCol, _kbFocusRow;
    private bool _selectionVisualDrawn;

    private readonly DrawingVisual _copyAnimVisual = new();
    private TimeSpan? _copyAnimStartTime;
    private DrawingGroup? _copyAnimDrawing;
    private Geometry? _copyAnimGeometry;

    private void DrawSelection()
    {
        var anyRowSelected = false;
        foreach (var frameRow in _terminal.FrameRows)
        {
            if (frameRow.HasSelection)
            {
                anyRowSelected = true;
                break;
            }
        }
        if (!_selectionActive && !anyRowSelected)
        {
            // Clear the overlay once when the selection deactivates; after
            // that, pumps with no selection skip the RenderOpen entirely.
            if (_selectionVisualDrawn)
            {
                using var clearDc = _selectionVisual.RenderOpen();
                _selectionVisualDrawn = false;
            }
            return;
        }

        _selectionVisualDrawn = true;
        using var dc = _selectionVisual.RenderOpen();

        for (var row = 0; row < _rows && row < _terminal.FrameRows.Length; row++)
        {
            var frameRow = _terminal.FrameRows[row];
            if (!frameRow.HasSelection)
                continue;

            var fromCol = frameRow.SelectionStart;
            var toCol = Math.Min(frameRow.SelectionEnd, _cols - 1);
            if (toCol < fromCol)
                continue;

            var rect = new Rect(fromCol * _cellWidth, row * _cellHeight, (toCol - fromCol + 1) * _cellWidth, _cellHeight);
            dc.DrawRoundedRectangle(_palette.Selection, null, rect, 2, 2);
        }
    }

    private void CopySelection()
    {
        if (!_selectionActive && !_terminal.HasSelection)
            return;

        var text = _terminal.GetSelectedText();
        if (!string.IsNullOrEmpty(text))
        {
            if (!TrySetClipboardText(text))
                return;
            StartCopyAnimation();
        }
        // Copied text is deselected, matching the copy-then-clear convention.
        ClearSelection();
    }

    private void ClearSelection()
    {
        _kbSelectionActive = false;
        if (_selectionActive || _terminal.HasSelection)
        {
            _selectionActive = false;
            _terminal.ClearSelection();
            FlushRedraw();
        }
    }

    private void StartCopyAnimation()
    {
        StopCopyAnimation();
        var geometry = new GeometryGroup();
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            for (var row = 0; row < Math.Min(_rows, _terminal.FrameRows.Length); row++)
            {
                var frameRow = _terminal.FrameRows[row];
                if (!frameRow.HasSelection || row >= _rowVisuals.Count)
                    continue;
                var fromCol = Math.Max(0, frameRow.SelectionStart);
                var toCol = Math.Min(frameRow.SelectionEnd, _cols - 1);
                if (toCol < fromCol)
                    continue;
                var rect = new Rect(fromCol * _cellWidth, row * _cellHeight,
                    (toCol - fromCol + 1) * _cellWidth, _cellHeight);
                var clip = new RectangleGeometry(rect, 2, 2);
                geometry.Children.Add(clip);
                dc.PushClip(clip);
                dc.DrawRectangle(_palette.Background, null, rect);
                dc.DrawRectangle(_palette.Selection, null, rect);
                // Retain the painted glyphs, not a live VisualBrush: output
                // arriving during the fade must not change the copied text.
                dc.DrawDrawing(_rowVisuals[row].Drawing);
                dc.Pop();
            }
        }
        if (geometry.Children.Count == 0)
            return;
        geometry.Freeze();
        drawing.Freeze();
        _copyAnimGeometry = geometry;
        _copyAnimDrawing = drawing;
        DrawCopyAnimation(0);
        if (!SystemParameters.ClientAreaAnimation)
        {
            StopCopyAnimation();
            return;
        }
        CompositionTarget.Rendering += OnCopyAnimFrame;
    }

    private void OnCopyAnimFrame(object? sender, EventArgs e)
    {
        var renderingTime = ((RenderingEventArgs)e).RenderingTime;
        _copyAnimStartTime ??= renderingTime;
        var elapsed = (renderingTime - _copyAnimStartTime.Value).TotalMilliseconds;
        if (_disposed || !IsVisible || elapsed >= 420)
        {
            StopCopyAnimation();
            return;
        }
        DrawCopyAnimation(elapsed / 420);
    }

    private void DrawCopyAnimation(double progress)
    {
        if (_copyAnimDrawing is null || _copyAnimGeometry is null)
            return;
        var ease = 1 - Math.Pow(1 - progress, 3);
        using var dc = _copyAnimVisual.RenderOpen();
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        dc.PushOpacity(Math.Pow(1 - progress, 2));
        dc.PushTransform(new TranslateTransform(0, -4 * ease));
        dc.DrawDrawing(_copyAnimDrawing);
        dc.PushOpacity(0.18 * Math.Max(0, 1 - progress * 3));
        dc.DrawGeometry(_palette.Foreground, null, _copyAnimGeometry);
        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void StopCopyAnimation()
    {
        CompositionTarget.Rendering -= OnCopyAnimFrame;
        _copyAnimStartTime = null;
        _copyAnimDrawing = null;
        _copyAnimGeometry = null;
        using var dc = _copyAnimVisual.RenderOpen();
    }

    /// <summary>
    /// Moves the keyboard-selection caret and re-drives the selection
    /// gesture. Every key replays the full press-drag-release sequence:
    /// ghostty only commits the selection snapshot on release, so a bare
    /// press+drag (no pointer button ever releases here) leaves nothing
    /// selectable. The caret wraps at line boundaries but stays inside the
    /// viewport; <paramref name="byWord"/> steps a whole word (left) or is
    /// reserved for the Home/End line jumps.
    /// </summary>
    private void ExtendKeyboardSelection(Key key, bool byWord)
    {
        if (!_kbSelectionActive)
        {
            var cursor = _terminal.Cursor;
            _kbAnchorCol = Math.Clamp(cursor.X, 0, _cols - 1);
            _kbAnchorRow = Math.Clamp(cursor.Y, 0, _rows - 1);
            _kbFocusCol = _kbAnchorCol;
            _kbFocusRow = _kbAnchorRow;
            _kbSelectionActive = true;
        }

        switch (key)
        {
            case Key.Left:
                if (byWord) _kbFocusCol = WordBoundaryCol(_kbFocusRow, _kbFocusCol, forward: false);
                else if (_kbFocusCol > 0) _kbFocusCol--;
                else if (_kbFocusRow > 0) { _kbFocusRow--; _kbFocusCol = _cols - 1; }
                break;
            case Key.Right:
                if (byWord) _kbFocusCol = WordBoundaryCol(_kbFocusRow, _kbFocusCol, forward: true);
                else if (_kbFocusCol < _cols - 1) _kbFocusCol++;
                else if (_kbFocusRow < _rows - 1) { _kbFocusRow++; _kbFocusCol = 0; }
                break;
            case Key.Up:
                if (_kbFocusRow > 0) _kbFocusRow--;
                break;
            case Key.Down:
                if (_kbFocusRow < _rows - 1) _kbFocusRow++;
                break;
            case Key.Home:
                _kbFocusCol = 0;
                break;
            case Key.End:
                _kbFocusCol = _cols - 1;
                break;
        }

        var pressX = _kbAnchorCol * _cellWidth + _cellWidth * 0.2;
        var rowY = _kbAnchorRow * _cellHeight + _cellHeight * 0.5;
        var focusX = _kbFocusCol * _cellWidth + _cellWidth * 0.8;
        var focusY = _kbFocusRow * _cellHeight + _cellHeight * 0.5;
        var nativePress = NativePoint(new Point(pressX, rowY));
        var nativeFocus = NativePoint(new Point(focusX, focusY));
        _terminal.SelectionPress(_kbAnchorCol, _kbAnchorRow, nativePress.X, nativePress.Y);
        _terminal.SelectionDrag(_kbFocusCol, _kbFocusRow, nativeFocus.X, nativeFocus.Y);
        _terminal.SelectionRelease(_kbFocusCol, _kbFocusRow);
        FlushRedraw();
    }

    /// <summary>
    /// Nearest word boundary at or next to <paramref name="col"/> in the
    /// viewport row. Backward skips separators then the word's characters, so
    /// a caret inside "foo|bar" lands on "foo" first and "bar" on the next
    /// press — the readline word-left behavior; forward is the mirror image.
    /// </summary>
    private int WordBoundaryCol(int row, int col, bool forward)
    {
        var frameRows = _terminal.FrameRows;
        if ((uint)row >= (uint)frameRows.Length)
            return col;
        var cells = frameRows[row].Cells;
        var last = Math.Max(0, cells.Length - 1);

        static bool IsSeparator(in CellInfo cell)
        {
            var text = cell.Text.TrimEnd('\0');
            return text.Length == 0 || char.IsWhiteSpace(text[0]);
        }

        if ((uint)col >= (uint)cells.Length)
            return last;
        if (forward)
        {
            while (col < last && IsSeparator(cells[col]))
                col++;
            while (col < last && !IsSeparator(cells[col + 1]))
                col++;
            return col;
        }
        while (col > 0 && IsSeparator(cells[col]))
            col--;
        while (col > 0 && !IsSeparator(cells[col - 1]))
            col--;
        return col;
    }

    private void PasteClipboard()
    {
        if (_disposed || !TryGetClipboardText(out var text) || text.Length == 0)
            return;
        text = text.Replace("\r\n", "\r").Replace("\n", "\r");
        if (_terminal.BracketedPaste)
            text = "\x1b[200~" + text + "\x1b[201~";
        WriteUserInput(Encoding.UTF8.GetBytes(text));
    }

    internal static bool TryGetClipboardText(out string text)
    {
        try
        {
            text = Clipboard.GetText();
            return true;
        }
        catch (ExternalException)
        {
            text = "";
            return false;
        }
    }

    internal static bool TrySetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the selection and, when it is single-line input ending exactly
    /// at the shell cursor (a leftward keyboard selection from the prompt),
    /// deletes it with backspaces — the only text a terminal can truly "cut"
    /// is unsubmitted line input.
    /// </summary>
    private void CutSelection()
    {
        if (!_selectionActive && !_terminal.HasSelection)
            return;
        var text = _terminal.GetSelectedText();
        if (string.IsNullOrEmpty(text))
            return;
        if (!TrySetClipboardText(text))
            return;

        var cursor = _terminal.Cursor;
        var nearestCursorCol = _kbFocusRow == _kbAnchorRow
            ? Math.Max(_kbAnchorCol, _kbFocusCol)
            : -1;
        var deletable = _kbSelectionActive
            && _kbFocusRow == _kbAnchorRow
            && _kbFocusRow == cursor.Y
            && nearestCursorCol == cursor.X;
        ClearSelection();

        if (deletable && _session is not null)
        {
            var backspaces = new byte[text.Length];
            Array.Fill(backspaces, (byte)0x7f);
            _session.Write(backspaces);
        }
    }

    private void FinishClickSelection()
    {
        if (_selectionClickCount > 1)
        {
            FlushRedraw();
            return;
        }

        _selectionActive = false;
        // Keep Ghostty's click history so the next nearby press can become a
        // word or line selection, while a lone click leaves no selected cell.
        _terminal.ClearSelection(resetGesture: false);
        FlushRedraw();
    }
}
