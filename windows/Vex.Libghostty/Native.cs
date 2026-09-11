using System.Runtime.InteropServices;

namespace Vex.Libghostty;

// libghostty-vt bindings, pin ghostty@f64f4aca. All exports are cdecl.
// Enum values and struct layouts mirror include/ghostty/vt/*.h.
internal static class Native
{
    internal const string Dll = "ghostty-vt";

    // --- Value types ----------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyString
    {
        public IntPtr ptr;
        public nuint len;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyBuffer
    {
        public IntPtr ptr;
        public nuint cap;
        public nuint len;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct GhosttyStyleColorValue
    {
        [FieldOffset(0)] public GhosttyColorRgb rgb;
        [FieldOffset(0)] public uint palette;
        [FieldOffset(0)] public ulong padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyStyleColor
    {
        public int tag;
        public GhosttyStyleColorValue value;
    }

    // Sized struct. The writer emits the full 72-byte struct regardless of
    // the size field, so the caller buffer must match (see spike README).
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyStyle
    {
        public nuint size;
        public GhosttyStyleColor fgColor;
        public GhosttyStyleColor bgColor;
        public GhosttyStyleColor underlineColor;
        public byte bold;
        public byte italic;
        public byte faint;
        public byte blink;
        public byte inverse;
        public byte invisible;
        public byte strikethrough;
        public byte overline;
        public int underline;

        public static GhosttyStyle New() => new() { size = (nuint)Marshal.SizeOf<GhosttyStyle>() };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyTerminalScrollbar
    {
        public ulong total;
        public ulong offset;
        public ulong len;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyTerminalScrollViewport
    {
        public int tag;
        public GhosttyTerminalScrollViewportValue value;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct GhosttyTerminalScrollViewportValue
    {
        [FieldOffset(0)] public nint delta;
        [FieldOffset(0)] public nuint row;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyMousePosition
    {
        public float x;
        public float y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyMouseEncoderSize
    {
        public nuint size;
        public uint screenWidth;
        public uint screenHeight;
        public uint cellWidth;
        public uint cellHeight;
        public uint paddingTop;
        public uint paddingBottom;
        public uint paddingRight;
        public uint paddingLeft;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyPointCoordinate
    {
        public ushort x;
        public uint y;
    }

    // C union GhosttyPointValue is 16 bytes: coordinate plus
    // uint64_t _padding[2] for ABI stability.
    [StructLayout(LayoutKind.Explicit)]
    internal struct GhosttyPointValue
    {
        [FieldOffset(0)] public GhosttyPointCoordinate coordinate;
        [FieldOffset(0)] public ulong padding0;
        [FieldOffset(8)] public ulong padding1;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyPoint
    {
        public int tag;
        public GhosttyPointValue value;
    }

    // Sized struct: size + node + x + y (24 bytes total).
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyGridRef
    {
        public nuint size;
        public IntPtr node;
        public ushort x;
        public ushort y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttySurfacePosition
    {
        public double x;
        public double y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttySelectionGestureGeometry
    {
        public uint columns;
        public uint cellWidth;
        public uint paddingLeft;
        public uint screenHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttySelectionGestureBehaviors
    {
        public int singleClick;
        public int doubleClick;
        public int tripleClick;
    }

    // Sized struct.
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyTerminalSelectionFormatOptions
    {
        public nuint size;
        public int emit;
        [MarshalAs(UnmanagedType.I1)] public bool unwrap;
        [MarshalAs(UnmanagedType.I1)] public bool trim;
        public IntPtr selection;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyRenderStateRowSelection
    {
        public nuint size;
        public ushort startX;
        public ushort endX;
    }

    // --- Enums ----------------------------------------------------------

    internal enum Result : int
    {
        Success = 0,
        OutOfMemory = -1,
        InvalidValue = -2,
        OutOfSpace = -3,
        NoValue = -4,
        IoError = -5,
        LimitExceeded = -6,
    }

    internal enum TerminalOption : int
    {
        Userdata = 0,
        WritePty = 1,
        Bell = 2,
        Enquiry = 3,
        Xtversion = 4,
        TitleChanged = 5,
        Size = 6,
        ColorScheme = 7,
        DeviceAttributes = 8,
        Title = 9,
        Pwd = 10,
        ColorForeground = 11,
        ColorBackground = 12,
        ColorCursor = 13,
        ColorPalette = 14,
        Selection = 21,
        DefaultCursorStyle = 22,
        DefaultCursorBlink = 23,
    }

    internal enum TerminalData : int
    {
        Cols = 1,
        Rows = 2,
        CursorX = 3,
        CursorY = 4,
        CursorPendingWrap = 5,
        ActiveScreen = 6,
        CursorVisible = 7,
        KittyKeyboardFlags = 8,
        Scrollbar = 9,
        CursorStyle = 10,
        MouseTracking = 11,
        Title = 12,
        Pwd = 13,
        TotalRows = 14,
        ScrollbackRows = 15,
        WidthPx = 16,
        HeightPx = 17,
        Selection = 31,
        Mode = 37,
    }

    internal enum TerminalScreen : int
    {
        Primary = 0,
        Alternate = 1,
    }

    internal enum TerminalCursorStyle : int
    {
        Bar = 0,
        Block = 1,
        Underline = 2,
        BlockHollow = 3,
    }

    internal enum ScrollViewportTag : int
    {
        Top = 0,
        Bottom = 1,
        Delta = 2,
        Row = 3,
    }

    internal enum PointTag : int
    {
        Active = 0,
        Viewport = 1,
        Screen = 2,
        History = 3,
    }

    internal enum RenderStateDirty : int
    {
        False = 0,
        Partial = 1,
        Full = 2,
    }

    internal enum RenderStateData : int
    {
        Cols = 1,
        Rows = 2,
        Dirty = 3,
        RowIterator = 4,
        ColorBackground = 5,
        ColorForeground = 6,
        ColorCursor = 7,
        ColorCursorHasValue = 8,
        ColorPalette = 9,
        CursorVisualStyle = 10,
        CursorVisible = 11,
        CursorBlinking = 12,
        CursorPasswordInput = 13,
        CursorViewportHasValue = 14,
        CursorViewportX = 15,
        CursorViewportY = 16,
        CursorViewportWideTail = 17,
    }

    internal enum RenderStateOption : int
    {
        Dirty = 0,
    }

    internal enum RenderStateRowData : int
    {
        Dirty = 1,
        Raw = 2,
        Cells = 3,
        Selection = 4,
    }

    internal enum RenderStateRowOption : int
    {
        Dirty = 0,
    }

    internal enum RenderStateRowCellsData : int
    {
        Raw = 1,
        Style = 2,
        GraphemesLen = 3,
        GraphemesBuf = 4,
        BgColor = 5,
        FgColor = 6,
        Selected = 7,
        HasStyling = 8,
        GraphemesUtf8 = 9,
    }

    internal enum CellData : int
    {
        Codepoint = 1,
        ContentTag = 2,
        Wide = 3,
        HasText = 4,
        HasStyling = 5,
        StyleId = 6,
        HasHyperlink = 7,
        Protected = 8,
        SemanticContent = 9,
        ColorPalette = 10,
        ColorRgb = 11,
    }

    internal enum CellWide : int
    {
        Narrow = 0,
        Wide = 1,
        WideSpacerTail = 2,
    }

    internal enum StyleColorTag : int
    {
        None = 0,
        Palette = 1,
        Rgb = 2,
    }

    internal enum SgrUnderline : int
    {
        None = 0,
        Single = 1,
        Double = 2,
        Curly = 3,
        Dotted = 4,
        Dashed = 5,
    }

    internal enum FormatterFormat : int
    {
        Plain = 0,
        Vt = 1,
        Html = 2,
    }

    internal enum SelectionGestureEventType : int
    {
        Press = 0,
        Release = 1,
        Drag = 2,
        AutoscrollTick = 3,
        DeepPress = 4,
    }

    internal enum SelectionGestureEventOption : int
    {
        Ref = 0,
        Position = 1,
        RepeatDistance = 2,
        TimeNs = 3,
        RepeatIntervalNs = 4,
        WordBoundaryCodepoints = 5,
        Behaviors = 6,
        Rectangle = 7,
        Geometry = 8,
        Viewport = 9,
    }

    internal enum SelectionGestureData : int
    {
        ClickCount = 0,
        Dragged = 1,
        Autoscroll = 2,
        Behavior = 3,
        Anchor = 4,
    }

    internal enum MouseEncoderOption : int
    {
        Event = 0,
        Format = 1,
        Size = 2,
        AnyButtonPressed = 3,
        TrackLastCell = 4,
    }

    // --- Callbacks ------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void TitleChangedFn(IntPtr terminal, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void WritePtyFn(IntPtr terminal, IntPtr userdata, IntPtr data, nuint len);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void BellFn(IntPtr terminal, IntPtr userdata);

    // --- Terminal -------------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_terminal_new(IntPtr allocator, out IntPtr terminal, ushort cols, ushort rows);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_terminal_free(IntPtr terminal);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_terminal_resize(IntPtr terminal, ushort cols, ushort rows, uint cellWidthPx, uint cellHeightPx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_terminal_set(IntPtr terminal, TerminalOption option, IntPtr value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_terminal_get(IntPtr terminal, TerminalData data, IntPtr outValue);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_terminal_vt_write(IntPtr terminal, byte[] data, nuint len);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_terminal_vt_write(IntPtr terminal, IntPtr data, nuint len);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_terminal_reset(IntPtr terminal);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_free(IntPtr allocator, IntPtr ptr, nuint len);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_terminal_scroll_viewport(IntPtr terminal, GhosttyTerminalScrollViewport behavior);

    // --- Mouse input ----------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_mouse_encoder_new(IntPtr allocator, out IntPtr encoder);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_mouse_encoder_free(IntPtr encoder);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_mouse_encoder_setopt(IntPtr encoder, MouseEncoderOption option, IntPtr value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_mouse_encoder_setopt_from_terminal(IntPtr encoder, IntPtr terminal);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_mouse_encoder_encode(IntPtr encoder, IntPtr mouseEvent, byte[] output, nuint outputSize, out nuint outputLength);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_mouse_event_new(IntPtr allocator, out IntPtr mouseEvent);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_mouse_event_free(IntPtr mouseEvent);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_mouse_event_set_action(IntPtr mouseEvent, MouseInputAction action);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_mouse_event_set_button(IntPtr mouseEvent, MouseInputButton button);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_mouse_event_clear_button(IntPtr mouseEvent);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_mouse_event_set_mods(IntPtr mouseEvent, MouseInputModifiers modifiers);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_mouse_event_set_position(IntPtr mouseEvent, GhosttyMousePosition position);

    // --- Render state ---------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_render_state_new(IntPtr allocator, out IntPtr state);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_render_state_free(IntPtr state);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_render_state_update(IntPtr state, IntPtr terminal);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_render_state_get(IntPtr state, RenderStateData data, IntPtr outValue);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_render_state_set(IntPtr state, RenderStateOption option, IntPtr value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_render_state_row_iterator_new(IntPtr allocator, out IntPtr iterator);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_render_state_row_iterator_free(IntPtr iterator);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ghostty_render_state_row_iterator_next(IntPtr iterator);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_render_state_row_get(IntPtr iterator, RenderStateRowData data, IntPtr outValue);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_render_state_row_set(IntPtr iterator, RenderStateRowOption option, IntPtr value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_render_state_row_cells_new(IntPtr allocator, out IntPtr cells);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_render_state_row_cells_free(IntPtr cells);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ghostty_render_state_row_cells_next(IntPtr cells);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_render_state_row_cells_get(IntPtr cells, RenderStateRowCellsData data, IntPtr outValue);

    // --- Cell helpers ---------------------------------------------------

    // NB: ghostty_cell_get takes the raw cell BY VALUE (an 8-byte packed
    // struct in the first register) despite the header's pointer spelling.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_cell_get(ulong cell, CellData data, IntPtr outValue);

    // --- Grid refs / selection -----------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_terminal_grid_ref(IntPtr terminal, GhosttyPoint point, out GhosttyGridRef gridRef);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_terminal_selection_format_alloc(IntPtr terminal, IntPtr allocator, in GhosttyTerminalSelectionFormatOptions options, out IntPtr outPtr, out nuint outLen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_selection_gesture_new(IntPtr allocator, out IntPtr gesture);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_selection_gesture_free(IntPtr gesture, IntPtr terminal);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_selection_gesture_reset(IntPtr gesture, IntPtr terminal);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_selection_gesture_event_new(IntPtr allocator, out IntPtr gestureEvent, SelectionGestureEventType type);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_selection_gesture_event_free(IntPtr gestureEvent);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_selection_gesture_event_set(IntPtr gestureEvent, SelectionGestureEventOption option, IntPtr value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_selection_gesture_event(IntPtr gesture, IntPtr terminal, IntPtr gestureEvent, IntPtr outSelection);
}
/// <summary>An sRGB color as consumed by libghostty-vt.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GhosttyColorRgb
{
    public byte r;
    public byte g;
    public byte b;

    public GhosttyColorRgb(byte r, byte g, byte b)
    {
        this.r = r;
        this.g = g;
        this.b = b;
    }
}
