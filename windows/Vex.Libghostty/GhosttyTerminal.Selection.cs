using System.Text;
using static Vex.Libghostty.Native;

namespace Vex.Libghostty;

public sealed partial class GhosttyTerminal
{
    public bool HasSelection
    {
        get
        {
            lock (_vtLock)
                return TryGetBuffer(TerminalData.Selection, s_selectionScratch);
        }
    }

    /// <summary>
    /// Sets keyboard selection between viewport caret boundaries (columns 0 through
    /// the grid width). Mouse multi-click history must not affect keyboard selection.
    /// </summary>
    public void SetKeyboardSelection(int anchorCol, int anchorRow, int focusCol, int focusRow)
    {
        lock (_vtLock)
        {
            ClearSelection();
            anchorCol = Math.Clamp(anchorCol, 0, _cols);
            focusCol = Math.Clamp(focusCol, 0, _cols);
            anchorRow = Math.Clamp(anchorRow, 0, _rows - 1);
            focusRow = Math.Clamp(focusRow, 0, _rows - 1);
            var start = anchorRow * _cols + anchorCol;
            var end = focusRow * _cols + focusCol;
            if (start == end)
                return;
            if (start > end)
                (start, end) = (end, start);

            // Ghostty selects inclusive cells; keyboard carets delimit a half-open range.
            end--;
            var startCol = start % _cols;
            var startRow = start / _cols;
            var endCol = end % _cols;
            var endRow = end / _cols;
            SelectionPress(startCol, startRow, (startCol + 0.2) * _cellWidthPx,
                (startRow + 0.5) * _cellHeightPx);
            SelectionDrag(endCol, endRow, (endCol + 0.8) * _cellWidthPx,
                (endRow + 0.5) * _cellHeightPx);
            SelectionRelease(endCol, endRow);
        }
    }

    public unsafe int SelectionPress(int viewportCol, int viewportRow, double xPx, double yPx)
    {
        lock (_vtLock)
        {
            var point = new Native.GhosttyPoint
            {
                tag = (int)Native.PointTag.Viewport,
                value = { coordinate = new Native.GhosttyPointCoordinate { x = (ushort)viewportCol, y = (uint)viewportRow } },
            };
            // A point that no longer maps to the grid (resize, alt-screen switch)
            // must skip the press, not crash the app.
            if (Native.ghostty_terminal_grid_ref(_terminal, point, out var gridRef) != Result.Success)
                return 0;

            SetEventOption(_pressEvent, SelectionGestureEventOption.Ref, gridRef);
            SetEventOption(_pressEvent, SelectionGestureEventOption.TimeNs, NowNs());
            var position = new Native.GhosttySurfacePosition { x = xPx, y = yPx };
            SetEventOption(_pressEvent, SelectionGestureEventOption.Position, position);

            var snapshot = GhosttySelectionScratch();
            var result = Native.ghostty_selection_gesture_event(_gesture, _terminal, _pressEvent, (IntPtr)(&snapshot));
            if (result == Result.Success)
                Check(Native.ghostty_terminal_set(_terminal, TerminalOption.Selection, (IntPtr)(&snapshot)), "set selection");

            byte clickCount = 0;
            Check(Native.ghostty_selection_gesture_get(_gesture, _terminal,
                SelectionGestureData.ClickCount, (IntPtr)(&clickCount)), "get selection click count");
            return clickCount;
        }
    }
    public unsafe void SelectionDrag(int viewportCol, int viewportRow, double xPx, double yPx)
    {
        lock (_vtLock)
        {
            var point = new Native.GhosttyPoint
            {
                tag = (int)Native.PointTag.Viewport,
                value = { coordinate = new Native.GhosttyPointCoordinate { x = (ushort)viewportCol, y = (uint)viewportRow } },
            };
            if (Native.ghostty_terminal_grid_ref(_terminal, point, out var gridRef) != Result.Success)
                return;

            SetEventOption(_dragEvent, SelectionGestureEventOption.Ref, gridRef);
            var geometry = new Native.GhosttySelectionGestureGeometry
            {
                columns = (uint)_cols,
                cellWidth = (uint)Math.Max(1, _cellWidthPx),
                paddingLeft = 0,
                screenHeight = (uint)Math.Max(1, _rows * _cellHeightPx),
            };
            SetEventOption(_dragEvent, SelectionGestureEventOption.Geometry, geometry);
            var position = new Native.GhosttySurfacePosition { x = xPx, y = yPx };
            SetEventOption(_dragEvent, SelectionGestureEventOption.Position, position);

            var snapshot = GhosttySelectionScratch();
            var result = Native.ghostty_selection_gesture_event(_gesture, _terminal, _dragEvent, (IntPtr)(&snapshot));
            if (result == Result.Success)
                Check(Native.ghostty_terminal_set(_terminal, TerminalOption.Selection, (IntPtr)(&snapshot)), "set selection");
        }
    }

    public unsafe void SelectionRelease(int viewportCol, int viewportRow)
    {
        lock (_vtLock)
        {
            var point = new Native.GhosttyPoint
            {
                tag = (int)Native.PointTag.Viewport,
                value = { coordinate = new Native.GhosttyPointCoordinate { x = (ushort)viewportCol, y = (uint)viewportRow } },
            };
            if (Native.ghostty_terminal_grid_ref(_terminal, point, out var gridRef) == Result.Success)
                SetEventOption(_releaseEvent, SelectionGestureEventOption.Ref, gridRef);
            else
                SetEventOption(_releaseEvent, SelectionGestureEventOption.Ref, IntPtr.Zero);

            var snapshot = GhosttySelectionScratch();
            Native.ghostty_selection_gesture_event(_gesture, _terminal, _releaseEvent, (IntPtr)(&snapshot));
        }
    }

    public unsafe void ClearSelection(bool resetGesture = true)
    {
        lock (_vtLock)
        {
            Native.ghostty_terminal_set(_terminal, TerminalOption.Selection, IntPtr.Zero);
            if (resetGesture)
                Native.ghostty_selection_gesture_reset(_gesture, _terminal);
        }
    }

    /// <summary>Formats selection text, unwrapping soft wraps; disabling trim preserves spaces for deletion.</summary>
    public unsafe string? GetSelectedText(bool trim = true)
    {
        lock (_vtLock)
        {
            var options = new Native.GhosttyTerminalSelectionFormatOptions
            {
                size = (nuint)sizeof(Native.GhosttyTerminalSelectionFormatOptions),
                emit = (int)Native.FormatterFormat.Plain,
                unwrap = true,
                trim = trim,
                selection = IntPtr.Zero,
            };
            var result = Native.ghostty_terminal_selection_format_alloc(_terminal, IntPtr.Zero, options, out var ptr, out var len);
            if (result != Result.Success || ptr == IntPtr.Zero)
                return null;
            try
            {
                return Encoding.UTF8.GetString((byte*)ptr, (int)len);
            }
            finally
            {
                Native.ghostty_free(IntPtr.Zero, ptr, len);
            }
        }
    }

    private static unsafe GhosttySelection GhosttySelectionScratch() => new()
    {
        size = (nuint)sizeof(GhosttySelection),
        start = new Native.GhosttyGridRef { size = (nuint)sizeof(Native.GhosttyGridRef) },
        end = new Native.GhosttyGridRef { size = (nuint)sizeof(Native.GhosttyGridRef) },
    };
}
