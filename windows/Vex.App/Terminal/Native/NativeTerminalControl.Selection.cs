using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Vex.App.Model;
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
    private const double CopyAnimationDurationMs = 560;
    private TimeSpan? _copyAnimStartTime;
    private DrawingGroup? _copyAnimDrawing;
    private Geometry? _copyAnimGeometry;
    private Geometry? _copyAnimEdgeGeometry;
    private Brush? _copyAnimSweepBrush;

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

    private void FinishMouseSelection()
    {
        if (AppSettings.Instance.CopyOnSelect)
            CopySelection();
        else
            FlushRedraw();
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
        if (!SystemParameters.ClientAreaAnimation)
            return;

        var geometry = new GeometryGroup();
        var edges = new GeometryGroup();
        var edgeHeight = 1 / VisualTreeHelper.GetDpi(this).DpiScaleY;
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
                edges.Children.Add(new RectangleGeometry(new Rect(rect.Left, rect.Bottom - edgeHeight,
                    rect.Width, edgeHeight)));
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
        edges.Freeze();
        drawing.Freeze();
        var color = ((SolidColorBrush)_palette.Foreground).Color;
        var transparent = Color.FromArgb(0, color.R, color.G, color.B);
        var sweep = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(transparent, 0),
                new GradientStop(Color.FromArgb(70, color.R, color.G, color.B), 0.3),
                new GradientStop(color, 0.7),
                new GradientStop(transparent, 1),
            },
        };
        sweep.Freeze();
        _copyAnimGeometry = geometry;
        _copyAnimEdgeGeometry = edges;
        _copyAnimSweepBrush = sweep;
        _copyAnimDrawing = drawing;
        DrawCopyAnimation(0);
        CompositionTarget.Rendering += OnCopyAnimFrame;
    }

    private void OnCopyAnimFrame(object? sender, EventArgs e)
    {
        var renderingTime = ((RenderingEventArgs)e).RenderingTime;
        _copyAnimStartTime ??= renderingTime;
        var elapsed = (renderingTime - _copyAnimStartTime.Value).TotalMilliseconds;
        if (_disposed || !IsVisible || elapsed >= CopyAnimationDurationMs)
        {
            StopCopyAnimation();
            return;
        }
        DrawCopyAnimation(elapsed / CopyAnimationDurationMs);
    }

    private void DrawCopyAnimation(double progress)
    {
        if (_copyAnimDrawing is null || _copyAnimGeometry is null ||
            _copyAnimEdgeGeometry is null || _copyAnimSweepBrush is null)
            return;
        progress = Math.Clamp(progress, 0, 1);
        // Let the confirmation register before dissolving the snapshot;
        // the sweep follows the selected cells, including multiline gaps.
        var fade = Math.Clamp((progress - 0.3) / 0.7, 0, 1);
        var opacity = 1 - fade * fade * (3 - 2 * fade);
        var sweepProgress = Math.Min(1, progress / 0.78);
        var sweepEase = sweepProgress * sweepProgress * (3 - 2 * sweepProgress);
        var bounds = _copyAnimGeometry.Bounds;
        var sweepWidth = Math.Max(3 * _cellWidth, bounds.Width * 0.28);
        var sweepRect = new Rect(bounds.Left - sweepWidth + (bounds.Width + sweepWidth) * sweepEase,
            bounds.Top, sweepWidth, bounds.Height);

        using var dc = _copyAnimVisual.RenderOpen();
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        dc.PushOpacity(opacity);
        dc.PushTransform(new TranslateTransform(0, -2 * fade * fade));
        dc.DrawDrawing(_copyAnimDrawing);

        dc.PushClip(_copyAnimGeometry);
        dc.PushOpacity(0.14);
        dc.DrawRectangle(_copyAnimSweepBrush, null, sweepRect);
        dc.Pop();
        dc.PushClip(_copyAnimEdgeGeometry);
        dc.PushOpacity(0.65);
        dc.DrawRectangle(_copyAnimSweepBrush, null, sweepRect);
        dc.Pop();
        dc.Pop();
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
        _copyAnimEdgeGeometry = null;
        _copyAnimSweepBrush = null;
        using var dc = _copyAnimVisual.RenderOpen();
    }

    /// <summary>
    /// Moves the keyboard-selection caret between cells, keeping the anchor
    /// fixed while the focus extends or shrinks the selection. Keyboard ranges
    /// are independent of mouse clicks and stay inside the viewport.
    /// </summary>
    private void ExtendKeyboardSelection(Key key, bool byWord)
    {
        if (!_kbSelectionActive)
        {
            var cursor = _terminal.Cursor;
            _kbAnchorCol = Math.Clamp(cursor.CaretColumn, 0, _cols);
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
                else if (_kbFocusCol < _cols) _kbFocusCol++;
                else if (_kbFocusRow < _rows - 1) { _kbFocusRow++; _kbFocusCol = 1; }
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
                _kbFocusCol = _cols;
                break;
        }

        _terminal.SetKeyboardSelection(_kbAnchorCol, _kbAnchorRow, _kbFocusCol, _kbFocusRow);
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
        var end = cells.Length;

        static bool IsSeparator(in CellInfo cell)
        {
            var text = cell.Text.TrimEnd('\0');
            return text.Length == 0 || char.IsWhiteSpace(text[0]);
        }

        col = Math.Clamp(col, 0, end);
        if (forward)
        {
            while (col < end && IsSeparator(cells[col]))
                col++;
            while (col < end && !IsSeparator(cells[col]))
                col++;
            return col;
        }
        while (col > 0 && IsSeparator(cells[col - 1]))
            col--;
        while (col > 0 && !IsSeparator(cells[col - 1]))
            col--;
        return col;
    }

    private void PasteClipboard()
    {
        if (_disposed || !TryGetClipboardText(out var text) || text.Length == 0)
            return;
        if (_pathCompletionOpen)
        {
            TypePathCompletionQuery(text);
            return;
        }
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
    /// Copies the selection and deletes it only when it ends at the shell
    /// cursor within one logical input line (which may soft-wrap).
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

        if (!TryDeleteKeyboardSelection())
            ClearSelection();
    }

    private bool TryDeleteKeyboardSelection()
    {
        if (!_kbSelectionActive || !_terminal.HasSelection || _terminal.IsAlternateScreen)
            return false;

        // Cursor coordinates refer to the live screen, not a scrolled viewport.
        var scrollbar = _terminal.Scrollbar;
        if (scrollbar.Offset + scrollbar.Len < scrollbar.Total)
            return false;
        var cursor = _terminal.Cursor;
        var anchor = _kbAnchorRow * _cols + _kbAnchorCol;
        var focus = _kbFocusRow * _cols + _kbFocusCol;
        if (Math.Max(anchor, focus) != cursor.Y * _cols + cursor.CaretColumn)
            return false;

        var text = _terminal.GetSelectedText(trim: false);
        // Soft wraps are unwrapped by Ghostty; a hard newline crosses into
        // another logical line and cannot safely be deleted at this cursor.
        if (string.IsNullOrEmpty(text) || text.IndexOfAny(['\r', '\n']) >= 0)
            return false;
        var backspaces = new byte[StringInfo.ParseCombiningCharacters(text).Length];
        Array.Fill(backspaces, (byte)0x7f);
        ClearSelection();
        WriteUserInput(backspaces);
        return true;
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
