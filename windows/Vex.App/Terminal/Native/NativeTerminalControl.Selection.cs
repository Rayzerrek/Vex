using System.Globalization;
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

    private DrawingVisual? _copyAnimVisual;
    private TimeSpan? _copyAnimStartTime;
    private Rect _copyAnimRect;

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

            dc.DrawRectangle(_palette.Selection, null,
                new Rect(fromCol * _cellWidth, row * _cellHeight, (toCol - fromCol + 1) * _cellWidth, _cellHeight));
        }
    }

    private void CopySelection()
    {
        if (!_selectionActive && !_terminal.HasSelection)
            return;

        var unionRect = Rect.Empty;
        for (var row = 0; row < _rows && row < _terminal.FrameRows.Length; row++)
        {
            var frameRow = _terminal.FrameRows[row];
            if (!frameRow.HasSelection) continue;

            var fromCol = frameRow.SelectionStart;
            var toCol = Math.Min(frameRow.SelectionEnd, _cols - 1);
            if (toCol < fromCol) continue;

            var rect = new Rect(fromCol * _cellWidth, row * _cellHeight, (toCol - fromCol + 1) * _cellWidth, _cellHeight);
            unionRect.Union(rect);
        }

        var text = _terminal.GetSelectedText();
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            if (!unionRect.IsEmpty)
                StartCopyAnimation(unionRect);
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

    private void StartCopyAnimation(Rect rect)
    {
        if (_copyAnimVisual == null)
        {
            _copyAnimVisual = new DrawingVisual();
            _children.Add(_copyAnimVisual);
        }

        _copyAnimRect = rect;
        _copyAnimStartTime = TimeSpan.Zero;
        CompositionTarget.Rendering -= OnCopyAnimFrame;
        CompositionTarget.Rendering += OnCopyAnimFrame;
    }

    private void OnCopyAnimFrame(object? sender, EventArgs e)
    {
        if (_copyAnimVisual == null) return;

        var renderingEventArgs = (RenderingEventArgs)e;
        if (_copyAnimStartTime == TimeSpan.Zero)
        {
            _copyAnimStartTime = renderingEventArgs.RenderingTime;
        }

        var elapsed = _copyAnimStartTime.HasValue
            ? (renderingEventArgs.RenderingTime - _copyAnimStartTime.Value).TotalMilliseconds
            : 0;

        var duration = 1200.0;

        if (elapsed >= duration)
        {
            CompositionTarget.Rendering -= OnCopyAnimFrame;
            using (var dc = _copyAnimVisual.RenderOpen()) { } // clear
            return;
        }

        var t = elapsed / duration;
        var opacity = t < 0.6 ? 1.0 : 1.0 - (t - 0.6) / 0.4;

        var popEaseOut = 1 - Math.Pow(1 - Math.Min(1.0, t * 5), 4);
        var yOffset = 16 * (1 - popEaseOut);

        var flashOpacity = 1.0 - Math.Min(1.0, t * 4); // Fades out in first 25% of animation

        using (var dc = _copyAnimVisual.RenderOpen())
        {
            dc.PushOpacity(opacity);

            var fgColor = ((SolidColorBrush)_palette.Foreground).Color;
            var glowAlpha = (byte)(0x40 + 0x80 * flashOpacity);
            var glowBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(glowAlpha, fgColor.R, fgColor.G, fgColor.B));
            var framePen = new Pen(_palette.Foreground, 1.5 + 1.5 * flashOpacity);
            var glowPen = new Pen(glowBrush, 5.0 + 8.0 * flashOpacity);

            dc.DrawRectangle(null, glowPen, _copyAnimRect);
            dc.DrawRectangle(null, framePen, _copyAnimRect);

            var textBrush = _palette.Background;
            var bgBrush = _palette.Foreground;

            var text = new FormattedText("Copied", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12, textBrush, _pixelsPerDip);

            var toastWidth = text.Width + 24;
            var toastHeight = text.Height + 12;

            var paddingX = 16.0;
            if (_scrollbarOpacity > 0.2)
                paddingX += _scrollbarWidth;

            var x = ActualWidth - paddingX - toastWidth;
            var y = 16.0 - yOffset;

            var rectToast = new Rect(x, y, toastWidth, toastHeight);

            dc.DrawRoundedRectangle(bgBrush, null, rectToast, 6, 6);
            dc.DrawText(text, new Point(rectToast.X + 12, rectToast.Y + 6));

            dc.Pop();
        }
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
        if (_session is null || !Clipboard.ContainsText())
            return;
        var text = Clipboard.GetText().Replace("\r\n", "\r").Replace("\n", "\r");
        if (_terminal.BracketedPaste)
            text = "\x1b[200~" + text + "\x1b[201~";
        _session.Write(Encoding.UTF8.GetBytes(text));
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
        Clipboard.SetText(text);

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
