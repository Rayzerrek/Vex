using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static Vex.Libghostty.Native;

namespace Vex.Libghostty;

[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Underline = 1 << 2,
    Strikethrough = 1 << 3,
    Inverse = 1 << 4,
    Blink = 1 << 5,
    Invisible = 1 << 6,
    Faint = 1 << 7,
}

public enum FrameDirty
{
    Clean = 0,
    Partial = 1,
    Full = 2,
}

public enum CursorShape
{
    Bar = 0,
    Block = 1,
    Underline = 2,
    BlockHollow = 3,
}

public readonly record struct CursorState(
    int X, int Y, bool Visible, bool Blinking, CursorShape Shape);

public enum ColorTag : int
{
    None = 0,
    Palette = 1,
    Rgb = 2,
}

/// <summary>
/// One rendered cell: the grapheme text ("" for empty cells and wide-char
/// spacer tails), the grid width, style flags, and the cell's fg/bg colors
/// as tagged values (palette index or packed RGB, or None for the default).
/// </summary>
public struct CellInfo
{
    public string Text;
    public bool Wide;
    public bool Tail;
    public CellFlags Flags;
    public ColorTag FgTag;
    public int FgValue;
    public ColorTag BgTag;
    public int BgValue;
}

public sealed class FrameRow
{
    public bool Dirty;
    public bool HasSelection;
    public int SelectionStart;
    public int SelectionEnd;
    public CellInfo[] Cells = Array.Empty<CellInfo>();
}

/// <summary>
/// Managed wrapper over one libghostty-vt terminal plus its render state.
/// All calls must be serialized on a single thread (the WPF UI thread).
/// </summary>
public sealed class GhosttyTerminal : IDisposable
{
    private const int MaxGraphemes = 32;

    private static readonly TitleChangedFn s_titleChanged = OnTitleChanged;
    private static readonly WritePtyFn s_writePty = OnWritePty;
    private static readonly BellFn s_bell = OnBell;

    private IntPtr _terminal;
    private IntPtr _renderState;
    private IntPtr _rowIterator;
    private IntPtr _rowCells;
    private IntPtr _gesture;
    private IntPtr _pressEvent;
    private IntPtr _dragEvent;
    private IntPtr _releaseEvent;
    private GCHandle _selfHandle;
    private uint[] _codepoints = new uint[MaxGraphemes];
    private bool _disposed;

    public event Action<string>? TitleChanged;
    public event Action<byte[]>? WritePty;
    public event Action? Bell;

    public FrameDirty FrameDirty { get; private set; }
    public CursorState Cursor { get; private set; }
    public FrameRow[] FrameRows { get; private set; } = Array.Empty<FrameRow>();
    public int Cols { get; private set; }
    public int Rows { get; private set; }

    private int _cellWidthPx = 8;
    private int _cellHeightPx = 16;

    public GhosttyTerminal(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
        Check(Native.ghostty_terminal_new(IntPtr.Zero, out _terminal, (ushort)cols, (ushort)rows), "terminal_new");

        _selfHandle = GCHandle.Alloc(this);
        SetOption(TerminalOption.Userdata, GCHandle.ToIntPtr(_selfHandle));
        SetOption(TerminalOption.TitleChanged, Marshal.GetFunctionPointerForDelegate(s_titleChanged));
        SetOption(TerminalOption.WritePty, Marshal.GetFunctionPointerForDelegate(s_writePty));
        SetOption(TerminalOption.Bell, Marshal.GetFunctionPointerForDelegate(s_bell));
        // xterm's default cursor (DECSCUSR 0) blinks; Vex's caret follows
        // the terminal unless an app forces a steady cursor.
        var defaultCursorBlink = true;
        SetOption(TerminalOption.DefaultCursorBlink, defaultCursorBlink);

        Check(Native.ghostty_render_state_new(IntPtr.Zero, out _renderState), "render_state_new");
        Check(Native.ghostty_render_state_row_iterator_new(IntPtr.Zero, out _rowIterator), "row_iterator_new");
        Check(Native.ghostty_render_state_row_cells_new(IntPtr.Zero, out _rowCells), "row_cells_new");
        Check(Native.ghostty_selection_gesture_new(IntPtr.Zero, out _gesture), "gesture_new");
        Check(Native.ghostty_selection_gesture_event_new(IntPtr.Zero, out _pressEvent, SelectionGestureEventType.Press), "event_new press");
        Check(Native.ghostty_selection_gesture_event_new(IntPtr.Zero, out _dragEvent, SelectionGestureEventType.Drag), "event_new drag");
        Check(Native.ghostty_selection_gesture_event_new(IntPtr.Zero, out _releaseEvent, SelectionGestureEventType.Release), "event_new release");

        var grid = new GhosttySelectionGestureBehaviors
        {
            singleClick = 0, // cell
            doubleClick = 1, // word
            tripleClick = 2, // line
        };
        var nowNs = NowNs();
        var repeatIntervalNs = 500_000_000UL;
        const double repeatDistance = 8.0;
        SetEventOption(_pressEvent, SelectionGestureEventOption.Behaviors, grid);
        SetEventOption(_pressEvent, SelectionGestureEventOption.TimeNs, nowNs);
        SetEventOption(_pressEvent, SelectionGestureEventOption.RepeatIntervalNs, repeatIntervalNs);
        SetEventOption(_pressEvent, SelectionGestureEventOption.RepeatDistance, repeatDistance);
    }

    // ---- Feed ------------------------------------------------------------

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    public void Feed(byte[] data) => Feed(data, 0, data.Length);

    public void Feed(byte[] data, int count) => Feed(data, 0, count);

    public void Feed(byte[] data, int offset, int count)
    {
        if (count <= 0 || _disposed)
            return;
        if (offset == 0)
        {
            Native.ghostty_terminal_vt_write(_terminal, data, (nuint)count);
        }
        else
        {
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(count);
            Array.Copy(data, offset, buffer, 0, count);
            try
            {
                Native.ghostty_terminal_vt_write(_terminal, buffer, (nuint)count);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public void Reset() => Native.ghostty_terminal_reset(_terminal);

    // ---- Geometry / colors ------------------------------------------------

    public void Resize(int cols, int rows, int cellWidthPx, int cellHeightPx)
    {
        Cols = cols;
        Rows = rows;
        _cellWidthPx = cellWidthPx;
        _cellHeightPx = cellHeightPx;
        Check(Native.ghostty_terminal_resize(_terminal, (ushort)cols, (ushort)rows, (uint)cellWidthPx, (uint)cellHeightPx), "resize");
    }

    public unsafe void SetDefaultColors(GhosttyColorRgb foreground, GhosttyColorRgb background,
        GhosttyColorRgb cursor, GhosttyColorRgb[] palette256)
    {
        SetOption(TerminalOption.ColorForeground, foreground);
        SetOption(TerminalOption.ColorBackground, background);
        SetOption(TerminalOption.ColorCursor, cursor);
        fixed (GhosttyColorRgb* palette = palette256)
            Check(Native.ghostty_terminal_set(_terminal, TerminalOption.ColorPalette, (IntPtr)palette), "set palette");
    }

    // ---- Modes ------------------------------------------------------------

    public bool IsAlternateScreen =>
        TryGet(TerminalData.ActiveScreen, out TerminalScreen screen) && screen == TerminalScreen.Alternate;

    public bool MouseTracking => TryGet(TerminalData.MouseTracking, out bool tracking) && tracking;

    public bool ApplicationCursor => GetMode(1);

    public bool BracketedPaste => GetMode(2004);

    private unsafe bool GetMode(ushort mode)
    {
        var config = new GhosttyTerminalModeConfig { mode = mode, value = false };
        return Native.ghostty_terminal_get(_terminal, TerminalData.Mode, (IntPtr)(&config)) == Result.Success && config.value;
    }

    // ---- Scroll -------------------------------------------------------------

    public (ulong Total, ulong Offset, ulong Len) Scrollbar =>
        TryGet(TerminalData.Scrollbar, out GhosttyTerminalScrollbar sb) ? (sb.total, sb.offset, sb.len) : (0, 0, 0);

    public void ScrollToTop() => Scroll(Native.ScrollViewportTag.Top, 0);

    public void ScrollToBottom() => Scroll(Native.ScrollViewportTag.Bottom, 0);

    public void ScrollToRow(ulong row) => Scroll(Native.ScrollViewportTag.Row, (nint)row);

    public void ScrollBy(int delta) => Scroll(Native.ScrollViewportTag.Delta, delta);

    private void Scroll(Native.ScrollViewportTag tag, nint value)
    {
        var behavior = new Native.GhosttyTerminalScrollViewport { tag = (int)tag, value = default };
        if (tag == Native.ScrollViewportTag.Delta)
            behavior.value.delta = value;
        else if (tag == Native.ScrollViewportTag.Row)
            behavior.value.row = (nuint)value;
        Native.ghostty_terminal_scroll_viewport(_terminal, behavior);
    }

    // ---- Frame --------------------------------------------------------------

    /// <summary>
    /// Pulls the latest terminal state into the render state and copies the
    /// viewport into managed <see cref="FrameRows"/>. Row/cell arrays are
    /// reused between calls; data is only valid until the next call.
    /// </summary>
    public unsafe void UpdateFrame()
    {
        Check(Native.ghostty_render_state_update(_renderState, _terminal), "render_state_update");

        FrameDirty dirty = FrameDirty.Clean;
        ushort cols = 0, rows = 0;
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.Dirty, (IntPtr)(&dirty)), "get dirty");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.Cols, (IntPtr)(&cols)), "get cols");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.Rows, (IntPtr)(&rows)), "get rows");
        FrameDirty = dirty;
        Cols = cols;
        Rows = rows;

        byte cursorVisible = 0, cursorBlink = 0, cursorInViewport = 0;
        var cursorStyle = CursorShape.Block;
        ushort cursorX = 0, cursorY = 0;
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorVisible, (IntPtr)(&cursorVisible)), "cursor visible");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorBlinking, (IntPtr)(&cursorBlink)), "cursor blink");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorVisualStyle, (IntPtr)(&cursorStyle)), "cursor style");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorViewportHasValue, (IntPtr)(&cursorInViewport)), "cursor vp");
        if (cursorInViewport != 0)
        {
            Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorViewportX, (IntPtr)(&cursorX)), "cursor x");
            Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorViewportY, (IntPtr)(&cursorY)), "cursor y");
        }
        Cursor = new CursorState(cursorX, cursorY, cursorVisible != 0, cursorBlink != 0, cursorStyle);

        EnsureFrameRows(rows);
        var rowIterator = _rowIterator;
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.RowIterator, (IntPtr)(&rowIterator)), "row iterator");

        var row = 0;
        while (Native.ghostty_render_state_row_iterator_next(_rowIterator))
        {
            var frameRow = FrameRows[row];
            byte rowDirty = 0;
            Check(Native.ghostty_render_state_row_get(_rowIterator, RenderStateRowData.Dirty, (IntPtr)(&rowDirty)), "row dirty");
            frameRow.Dirty = rowDirty != 0;

            var selection = new Native.GhosttyRenderStateRowSelection { size = (nuint)Marshal.SizeOf<Native.GhosttyRenderStateRowSelection>() };
            var selResult = Native.ghostty_render_state_row_get(_rowIterator, RenderStateRowData.Selection, (IntPtr)(&selection));
            frameRow.HasSelection = selResult == Result.Success;
            if (frameRow.HasSelection)
            {
                frameRow.SelectionStart = selection.startX;
                frameRow.SelectionEnd = selection.endX;
            }

            var rowCellsHandle = _rowCells;
            Check(Native.ghostty_render_state_row_get(_rowIterator, RenderStateRowData.Cells, (IntPtr)(&rowCellsHandle)), "row cells");
            var cells = frameRow.Cells;
            var col = 0;
            while (col < cols && Native.ghostty_render_state_row_cells_next(_rowCells))
            {
                ref var cell = ref cells[col];
                ReadCell(ref cell);
                col++;
            }
            for (; col < cols; col++)
                cells[col] = default;

            row++;
            if (row >= rows)
                break;
        }
    }

    private unsafe void ReadCell(ref CellInfo cell)
    {
        var style = Native.GhosttyStyle.New();
        Check(Native.ghostty_render_state_row_cells_get(_rowCells, RenderStateRowCellsData.Style, (IntPtr)(&style)), "cell style");

        cell.Flags = CellFlags.None;
        if (style.bold != 0) cell.Flags |= CellFlags.Bold;
        if (style.italic != 0) cell.Flags |= CellFlags.Italic;
        if (style.underline != 0) cell.Flags |= CellFlags.Underline;
        if (style.strikethrough != 0) cell.Flags |= CellFlags.Strikethrough;
        if (style.inverse != 0) cell.Flags |= CellFlags.Inverse;
        if (style.blink != 0) cell.Flags |= CellFlags.Blink;
        if (style.invisible != 0) cell.Flags |= CellFlags.Invisible;
        if (style.faint != 0) cell.Flags |= CellFlags.Faint;

        cell.FgTag = (ColorTag)style.fgColor.tag;
        cell.FgValue = style.fgColor.tag == (int)ColorTag.Rgb
            ? (style.fgColor.value.rgb.r << 16) | (style.fgColor.value.rgb.g << 8) | style.fgColor.value.rgb.b
            : (int)style.fgColor.value.palette;
        cell.BgTag = (ColorTag)style.bgColor.tag;
        cell.BgValue = style.bgColor.tag == (int)ColorTag.Rgb
            ? (style.bgColor.value.rgb.r << 16) | (style.bgColor.value.rgb.g << 8) | style.bgColor.value.rgb.b
            : (int)style.bgColor.value.palette;

        ulong raw = 0;
        Check(Native.ghostty_render_state_row_cells_get(_rowCells, RenderStateRowCellsData.Raw, (IntPtr)(&raw)), "cell raw");
        var wide = Native.CellWide.Narrow;
        Check(Native.ghostty_cell_get(raw, CellData.Wide, (IntPtr)(&wide)), "cell wide");
        cell.Wide = wide == Native.CellWide.Wide;
        cell.Tail = wide == Native.CellWide.WideSpacerTail;

        uint graphemesLen = 0;
        Check(Native.ghostty_render_state_row_cells_get(_rowCells, RenderStateRowCellsData.GraphemesLen, (IntPtr)(&graphemesLen)), "cell graphemes len");
        if (graphemesLen == 0 || cell.Tail)
        {
            cell.Text = "";
            return;
        }

        if (_codepoints.Length < graphemesLen)
            _codepoints = new uint[Math.Max(graphemesLen, MaxGraphemes)];
        var codepoints = _codepoints;
        fixed (uint* p = codepoints)
            Check(Native.ghostty_render_state_row_cells_get(_rowCells, RenderStateRowCellsData.GraphemesBuf, (IntPtr)p), "cell graphemes buf");

        if (graphemesLen == 1)
        {
            cell.Text = char.ConvertFromUtf32((int)_codepoints[0]);
        }
        else
        {
            var sb = new StringBuilder((int)graphemesLen * 2);
            for (var i = 0; i < graphemesLen; i++)
                sb.Append(char.ConvertFromUtf32((int)_codepoints[i]));
            cell.Text = sb.ToString();
        }
    }

    private void EnsureFrameRows(int rows)
    {
        if (FrameRows.Length == rows)
            return;
        var previous = FrameRows;
        FrameRows = new FrameRow[rows];
        for (var i = 0; i < rows; i++)
            FrameRows[i] = i < previous.Length ? previous[i] : new FrameRow();
        for (var i = 0; i < rows; i++)
        {
            if (FrameRows[i].Cells.Length != Cols)
                FrameRows[i].Cells = new CellInfo[Cols];
        }
    }

    // ---- Selection -----------------------------------------------------------

    public bool HasSelection => TryGetBuffer(TerminalData.Selection, s_selectionScratch);

    public unsafe void SelectionPress(int viewportCol, int viewportRow, double xPx, double yPx)
    {
        var point = new Native.GhosttyPoint
        {
            tag = (int)Native.PointTag.Viewport,
            value = { coordinate = new Native.GhosttyPointCoordinate { x = (ushort)viewportCol, y = (uint)viewportRow } },
        };
        Check(Native.ghostty_terminal_grid_ref(_terminal, point, out var gridRef), "grid_ref");

        SetEventOption(_pressEvent, SelectionGestureEventOption.Ref, gridRef);
        SetEventOption(_pressEvent, SelectionGestureEventOption.TimeNs, NowNs());
        var position = new Native.GhosttySurfacePosition { x = xPx, y = yPx };
        SetEventOption(_pressEvent, SelectionGestureEventOption.Position, position);

        var snapshot = GhosttySelectionScratch();
        var result = Native.ghostty_selection_gesture_event(_gesture, _terminal, _pressEvent, (IntPtr)(&snapshot));
        if (result == Result.Success)
            Check(Native.ghostty_terminal_set(_terminal, TerminalOption.Selection, (IntPtr)(&snapshot)), "set selection");
    }

    public unsafe void SelectionDrag(int viewportCol, int viewportRow, double xPx, double yPx)
    {
        var point = new Native.GhosttyPoint
        {
            tag = (int)Native.PointTag.Viewport,
            value = { coordinate = new Native.GhosttyPointCoordinate { x = (ushort)viewportCol, y = (uint)viewportRow } },
        };
        Check(Native.ghostty_terminal_grid_ref(_terminal, point, out var gridRef), "grid_ref");

        SetEventOption(_dragEvent, SelectionGestureEventOption.Ref, gridRef);
        var geometry = new Native.GhosttySelectionGestureGeometry
        {
            columns = (uint)Cols,
            cellWidth = (uint)Math.Max(1, _cellWidthPx),
            paddingLeft = 0,
            screenHeight = (uint)Math.Max(1, Rows * _cellHeightPx),
        };
        SetEventOption(_dragEvent, SelectionGestureEventOption.Geometry, geometry);
        var position = new Native.GhosttySurfacePosition { x = xPx, y = yPx };
        SetEventOption(_dragEvent, SelectionGestureEventOption.Position, position);

        var snapshot = GhosttySelectionScratch();
        var result = Native.ghostty_selection_gesture_event(_gesture, _terminal, _dragEvent, (IntPtr)(&snapshot));
        if (result == Result.Success)
            Check(Native.ghostty_terminal_set(_terminal, TerminalOption.Selection, (IntPtr)(&snapshot)), "set selection");
    }

    public unsafe void SelectionRelease(int viewportCol, int viewportRow)
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

    public unsafe void ClearSelection()
    {
        Native.ghostty_terminal_set(_terminal, TerminalOption.Selection, IntPtr.Zero);
        Native.ghostty_selection_gesture_reset(_gesture, _terminal);
    }

    public unsafe string? GetSelectedText()
    {
        var options = new Native.GhosttyTerminalSelectionFormatOptions
        {
            size = (nuint)Marshal.SizeOf<Native.GhosttyTerminalSelectionFormatOptions>(),
            emit = (int)Native.FormatterFormat.Plain,
            unwrap = true,
            trim = true,
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

    private static GhosttySelection GhosttySelectionScratch() => new()
    {
        size = (nuint)Marshal.SizeOf<GhosttySelection>(),
        start = new Native.GhosttyGridRef { size = (nuint)Marshal.SizeOf<Native.GhosttyGridRef>() },
        end = new Native.GhosttyGridRef { size = (nuint)Marshal.SizeOf<Native.GhosttyGridRef>() },
    };

    // ---- Plumbing ------------------------------------------------------------

    private static readonly byte[] s_selectionScratch = new byte[64];

    private unsafe bool TryGetBuffer(TerminalData data, byte[] buffer)
    {
        var scratch = buffer;
        fixed (byte* p = scratch)
            return Native.ghostty_terminal_get(_terminal, data, (IntPtr)p) == Result.Success;
    }

    private unsafe bool TryGet<T>(TerminalData data, out T value) where T : unmanaged
    {
        value = default;
        var buffer = value;
        var ok = Native.ghostty_terminal_get(_terminal, data, (IntPtr)(&buffer)) == Result.Success;
        value = buffer;
        return ok;
    }

    private void SetOption(TerminalOption option, IntPtr value) =>
        Check(Native.ghostty_terminal_set(_terminal, option, value), $"set {(int)option}");

    private void SetOption<T>(TerminalOption option, T value) where T : unmanaged
    {
        unsafe
        {
            Check(Native.ghostty_terminal_set(_terminal, option, (IntPtr)(&value)), $"set {(int)option}");
        }
    }

    private void SetEventOption<T>(IntPtr eventHandle, SelectionGestureEventOption option, T value) where T : unmanaged
    {
        unsafe
        {
            Check(Native.ghostty_selection_gesture_event_set(eventHandle, option, (IntPtr)(&value)), $"event set {(int)option}");
        }
    }

    private void SetEventOption(IntPtr eventHandle, SelectionGestureEventOption option, IntPtr value) =>
        Native.ghostty_selection_gesture_event_set(eventHandle, option, value);

    private static ulong NowNs()
    {
        var ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        return (ulong)(ticks * 1_000_000_000.0 / frequency);
    }

    private static void Check(Result result, string what)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"libghostty: {what} failed: {result}");
    }

    private static void OnTitleChanged(IntPtr terminal, IntPtr userdata)
    {
        if (GetTarget(userdata) is not { } self)
            return;
        var title = GetBorrowed(terminal, TerminalData.Title);
        self.TitleChanged?.Invoke(title);
    }

    private static void OnWritePty(IntPtr terminal, IntPtr userdata, IntPtr data, nuint len)
    {
        if (GetTarget(userdata) is not { } self)
            return;
        var bytes = new byte[(int)len];
        Marshal.Copy(data, bytes, 0, (int)len);
        self.WritePty?.Invoke(bytes);
    }

    private static void OnBell(IntPtr terminal, IntPtr userdata)
    {
        GetTarget(userdata)?.Bell?.Invoke();
    }

    private static GhosttyTerminal? GetTarget(IntPtr userdata) =>
        GCHandle.FromIntPtr(userdata).Target as GhosttyTerminal;

    private static unsafe string GetBorrowed(IntPtr terminal, TerminalData data)
    {
        var s = new Native.GhosttyString();
        if (Native.ghostty_terminal_get(terminal, data, (IntPtr)(&s)) != Result.Success)
            return "";
        return s.ptr == IntPtr.Zero || s.len == 0 ? "" : Marshal.PtrToStringUTF8(s.ptr, (int)s.len) ?? "";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Native.ghostty_selection_gesture_event_free(_releaseEvent);
        Native.ghostty_selection_gesture_event_free(_dragEvent);
        Native.ghostty_selection_gesture_event_free(_pressEvent);
        Native.ghostty_selection_gesture_free(_gesture, _terminal);
        Native.ghostty_render_state_row_cells_free(_rowCells);
        Native.ghostty_render_state_row_iterator_free(_rowIterator);
        Native.ghostty_render_state_free(_renderState);
        Native.ghostty_terminal_free(_terminal);
        _terminal = IntPtr.Zero;
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyTerminalModeConfig
{
    public ushort mode;
    [MarshalAs(UnmanagedType.I1)] public bool value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttySelection
{
    public nuint size;
    public Native.GhosttyGridRef start;
    public Native.GhosttyGridRef end;
    [MarshalAs(UnmanagedType.I1)] public bool rectangle;
}
