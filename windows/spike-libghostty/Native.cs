using System.Runtime.InteropServices;

namespace Spike;

// Minimal libghostty-vt bindings for the spike. Enum values mirror
// include/ghostty/vt/*.h at pin f64f4aca. All exports are cdecl.
internal static class Native
{
    private const string Dll = "ghostty-vt";

    // --- Types ----------------------------------------------------------

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

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyColorRgb
    {
        public byte r;
        public byte g;
        public byte b;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct GhosttyStyleColorValue
    {
        [FieldOffset(0)] public GhosttyColorRgb rgb;
        [FieldOffset(0)] public ulong padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyStyleColor
    {
        public int tag;
        public GhosttyStyleColorValue value;
    }

    // Sized struct; size field must be set by the caller.
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyStyle
    {
        public nuint size;
        public GhosttyStyleColor fgColor;
        public GhosttyStyleColor bgColor;
        public byte bold;
        public byte italic;
        public byte faint;
        public byte blink;
        public byte inverse;
        public byte invisible;
        public byte strikethrough;
        public byte overline;
        public int underline;
    }

    // Sized struct; size field must be set by the caller.
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyRenderStateColors
    {
        public nuint size;
        public GhosttyColorRgb background;
        public GhosttyColorRgb foreground;
        public GhosttyColorRgb cursor;
        [MarshalAs(UnmanagedType.I1)] public bool cursorHasValue;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public GhosttyColorRgb[] palette;

        public static GhosttyRenderStateColors New() => new()
        {
            size = (nuint)Marshal.SizeOf<GhosttyRenderStateColors>(),
            palette = new GhosttyColorRgb[256],
        };
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

    internal enum RenderStateRowData : int
    {
        Dirty = 1,
        Raw = 2,
        Cells = 3,
        Selection = 4,
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

    internal enum CursorVisualStyle : int
    {
        Bar = 0,
        Block = 1,
        Underline = 2,
        BlockHollow = 3,
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

    // --- Callbacks ------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void TitleChangedFn(IntPtr terminal, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void WritePtyFn(IntPtr terminal, IntPtr userdata, IntPtr data, nuint len);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate GhosttyString EnquiryFn(IntPtr terminal, IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate GhosttyString XtversionFn(IntPtr terminal, IntPtr userdata);

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
    internal static extern void ghostty_terminal_reset(IntPtr terminal);

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
    internal static extern Result ghostty_render_state_colors_get(IntPtr state, ref GhosttyRenderStateColors outColors);

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
    internal static extern Result ghostty_render_state_row_cells_new(IntPtr allocator, out IntPtr cells);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ghostty_render_state_row_cells_free(IntPtr cells);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool ghostty_render_state_row_cells_next(IntPtr cells);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result ghostty_render_state_row_cells_get(IntPtr cells, RenderStateRowCellsData data, IntPtr outValue);
}
