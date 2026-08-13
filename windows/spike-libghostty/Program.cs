using System.Runtime.InteropServices;
using System.Text;
using static Spike.Native;
using Vex.Terminal;

namespace Spike;

internal static class Program
{
    // Callback delegate roots: kept alive for the process lifetime so the
    // unmanaged side never sees a collected delegate.
    private static readonly TitleChangedFn s_titleChanged = OnTitleChanged;
    private static readonly WritePtyFn s_writePty = OnWritePty;
    private static readonly EnquiryFn s_enquiry = OnEnquiry;
    private static readonly XtversionFn s_xtversion = OnXtversion;

    private static readonly byte[] s_enquiryBytes = Encoding.UTF8.GetBytes("VEX-SPIKE\r\n");
    private static readonly byte[] s_xtversionBytes = Encoding.UTF8.GetBytes("vex 0.1.0");
    private static readonly StringBuilder s_ptyWrites = new();

    private static unsafe int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            AssertLayouts();
            return InMemorySmoke() && ConPtySmoke() ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.WriteLine($"FAILED: {e}");
            return 1;
        }
    }

    private static unsafe void AssertLayouts()
    {
        Require(Marshal.SizeOf<GhosttyString>() == 16, "GhosttyString layout");
        Require(Marshal.SizeOf<GhosttyStyleColor>() == 16, "GhosttyStyleColor layout");
        Require(Marshal.SizeOf<GhosttyStyle>() == 72, "GhosttyStyle layout");
        Require(Marshal.SizeOf<GhosttyRenderStateColors>() == 792, "GhosttyRenderStateColors layout");
        Console.WriteLine("layouts OK");
    }

    private static unsafe bool InMemorySmoke()
    {
        Console.WriteLine("=== phase 1: in-memory VT ===");

        var r = Native.ghostty_terminal_new(IntPtr.Zero, out var term, 80, 24);
        Require(r == Result.Success, $"terminal_new: {r}");
        try
        {
            long marker = 0x1234;
            Require(Native.ghostty_terminal_set(term, TerminalOption.Userdata, (IntPtr)(&marker)) == Result.Success, "set userdata");
            Require(Native.ghostty_terminal_set(term, TerminalOption.TitleChanged, Marshal.GetFunctionPointerForDelegate(s_titleChanged)) == Result.Success, "set title_changed");
            Require(Native.ghostty_terminal_set(term, TerminalOption.WritePty, Marshal.GetFunctionPointerForDelegate(s_writePty)) == Result.Success, "set write_pty");
            Require(Native.ghostty_terminal_set(term, TerminalOption.Enquiry, Marshal.GetFunctionPointerForDelegate(s_enquiry)) == Result.Success, "set enquiry");
            Require(Native.ghostty_terminal_set(term, TerminalOption.Xtversion, Marshal.GetFunctionPointerForDelegate(s_xtversion)) == Result.Success, "set xtversion");

            var fg = new GhosttyColorRgb { r = 220, g = 220, b = 220 };
            var bg = new GhosttyColorRgb { r = 16, g = 18, b = 24 };
            var cursor = new GhosttyColorRgb { r = 255, g = 120, b = 80 };
            Require(Native.ghostty_terminal_set(term, TerminalOption.ColorForeground, (IntPtr)(&fg)) == Result.Success, "set fg");
            Require(Native.ghostty_terminal_set(term, TerminalOption.ColorBackground, (IntPtr)(&bg)) == Result.Success, "set bg");
            Require(Native.ghostty_terminal_set(term, TerminalOption.ColorCursor, (IntPtr)(&cursor)) == Result.Success, "set cursor color");

            var sample = new StringBuilder();
            sample.Append("\u001b[2J\u001b[H");
            sample.Append("Hello from libghostty-vt!\r\n");
            sample.Append("\u001b[31mred \u001b[32mgreen \u001b[1mbold \u001b[3mitalic \u001b[4munderline \u001b[9mstrike \u001b[7minverse\u001b[0m\r\n");
            sample.Append("\u001b[38;5;208m256-color \u001b[38;2;255;128;64mtruecolor\u001b[0m\r\n");
            sample.Append("wide: 中 文 emoji: \U0001F600  family ZWJ: \U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466\r\n");
            sample.Append("\u001b]0;vex-spike-title\u0007");
            sample.Append("\u001b]7;file:///C:/Users/ziut\u0007");
            sample.Append("\x05\u001b[>q"); // ENQ then XTVERSION: both write back through write_pty
            Feed(term, sample.ToString());

            Require(s_ptyWrites.Length > 0, "write_pty received responses");
            Console.WriteLine($"write_pty got: {Escape(s_ptyWrites.ToString())}");

            var title = GetBorrowedString(term, TerminalData.Title);
            var pwd = GetBorrowedString(term, TerminalData.Pwd);
            Require(title == "vex-spike-title", $"title = '{title}'");
            Require(pwd.StartsWith("file:///C:/Users/ziut"), $"pwd = '{pwd}'");

            Console.WriteLine("-- viewport after initial feed --");
            DumpViewport(term, expectDirty: RenderStateDirty.Full);

            // Scrolling: push 100 lines, verify scrollback and viewport scroll.
            for (var i = 0; i < 100; i++)
                Feed(term, $"line {i:D3} of scroll pressure\r\n");
            Console.WriteLine("-- after 100 scroll lines --");
            DumpViewport(term, expectDirty: null);

            // Reflow: shrink the grid.
            Require(Native.ghostty_terminal_resize(term, 40, 24, 8, 16) == Result.Success, "resize");
            Console.WriteLine("-- after resize to 40x24 (reflow) --");
            DumpViewport(term, expectDirty: RenderStateDirty.Full);

            return true;
        }
        finally
        {
            Native.ghostty_terminal_free(term);
        }
    }

    private static bool ConPtySmoke()
    {
        Console.WriteLine("=== phase 2: live ConPTY (cmd.exe) ===");

        Native.ghostty_terminal_new(IntPtr.Zero, out var term, 80, 24);
        Native.ghostty_terminal_set(term, TerminalOption.WritePty, Marshal.GetFunctionPointerForDelegate(s_writePty));

        using var session = new TerminalSession();
        var buffer = new List<byte>();
        var exit = new ManualResetEventSlim();
        session.OutputReceived += chunk =>
        {
            lock (buffer)
            {
                var bytes = chunk.ToArray();
                buffer.AddRange(bytes);
                Native.ghostty_terminal_vt_write(term, bytes, (nuint)bytes.Length);
            }
        };
        session.Exited += _ => exit.Set();

        session.Start(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 80, 24);
        session.Write(Encoding.UTF8.GetBytes("echo HELLO-FROM-CONPTY\r\nexit\r\n"));

        Require(exit.Wait(TimeSpan.FromSeconds(20)), "conpty session exited");
        Thread.Sleep(200); // drain trailing reader-loop bytes

        Native.ghostty_terminal_vt_write(term, Array.Empty<byte>(), 0);
        Console.WriteLine($"conpty total bytes: {buffer.Count}");
        Require(buffer.Count > 0, "received bytes from conpty");
        DumpViewport(term, expectDirty: null);

        Native.ghostty_terminal_free(term);
        return true;
    }

    // --- helpers --------------------------------------------------------

    private static void Feed(IntPtr term, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Native.ghostty_terminal_vt_write(term, bytes, (nuint)bytes.Length);
    }

    private static unsafe string GetBorrowedString(IntPtr term, TerminalData data)
    {
        var s = new GhosttyString();
        Require(Native.ghostty_terminal_get(term, data, (IntPtr)(&s)) == Result.Success, $"get {data}");
        return s.ptr == IntPtr.Zero || s.len == 0 ? "" : Marshal.PtrToStringUTF8(s.ptr, (int)s.len) ?? "";
    }

    private static unsafe void DumpViewport(IntPtr term, RenderStateDirty? expectDirty)
    {
        Require(Native.ghostty_render_state_new(IntPtr.Zero, out var state) == Result.Success, "render_state_new");
        try
        {
            Require(Native.ghostty_render_state_update(state, term) == Result.Success, "render_state_update");

            ushort cols = 0, rows = 0;
            var dirty = RenderStateDirty.False;
            Require(Native.ghostty_render_state_get(state, RenderStateData.Cols, (IntPtr)(&cols)) == Result.Success, "get cols");
            Require(Native.ghostty_render_state_get(state, RenderStateData.Rows, (IntPtr)(&rows)) == Result.Success, "get rows");
            Require(Native.ghostty_render_state_get(state, RenderStateData.Dirty, (IntPtr)(&dirty)) == Result.Success, "get dirty");
            if (expectDirty is { } expected)
                Require(dirty == expected, $"dirty {dirty} != {expected}");

            ushort cx = 0, cy = 0;
            byte cursorVisible = 0, cursorBlink = 0, cursorInViewport = 0;
            var cursorStyle = CursorVisualStyle.Block;
            Require(Native.ghostty_render_state_get(state, RenderStateData.CursorVisible, (IntPtr)(&cursorVisible)) == Result.Success, "cursor visible");
            Require(Native.ghostty_render_state_get(state, RenderStateData.CursorBlinking, (IntPtr)(&cursorBlink)) == Result.Success, "cursor blink");
            Require(Native.ghostty_render_state_get(state, RenderStateData.CursorVisualStyle, (IntPtr)(&cursorStyle)) == Result.Success, "cursor style");
            Require(Native.ghostty_render_state_get(state, RenderStateData.CursorViewportHasValue, (IntPtr)(&cursorInViewport)) == Result.Success, "cursor vp has value");
            if (cursorInViewport != 0)
            {
                Require(Native.ghostty_render_state_get(state, RenderStateData.CursorViewportX, (IntPtr)(&cx)) == Result.Success, "cursor x");
                Require(Native.ghostty_render_state_get(state, RenderStateData.CursorViewportY, (IntPtr)(&cy)) == Result.Success, "cursor y");
            }

            var colors = GhosttyRenderStateColors.New();
            Require(Native.ghostty_render_state_colors_get(state, ref colors) == Result.Success, "colors_get");

            Console.WriteLine(
                $"  {cols}x{rows} dirty={dirty} cursor=({cx},{cy}) style={cursorStyle} " +
                $"visible={cursorVisible != 0} blink={cursorBlink != 0} " +
                $"bg=#{colors.background.r:X2}{colors.background.g:X2}{colors.background.b:X2} " +
                $"fg=#{colors.foreground.r:X2}{colors.foreground.g:X2}{colors.foreground.b:X2}");

            Require(Native.ghostty_render_state_row_iterator_new(IntPtr.Zero, out var rowIt) == Result.Success, "row_iterator_new");
            Require(Native.ghostty_render_state_row_cells_new(IntPtr.Zero, out var cells) == Result.Success, "row_cells_new");
            try
            {
                Require(Native.ghostty_render_state_get(state, RenderStateData.RowIterator, (IntPtr)(&rowIt)) == Result.Success, "get row iterator");

                var y = 0;
                while (Native.ghostty_render_state_row_iterator_next(rowIt))
                {
                    Require(Native.ghostty_render_state_row_get(rowIt, RenderStateRowData.Cells, (IntPtr)(&cells)) == Result.Success, "row get cells");

                    if (y == 1)
                    {
                        DumpRowCellsDetail(cells, cols);
                        y++;
                        continue;
                    }
                    var text = new StringBuilder(cols);
                    var styles = new StringBuilder(cols);
                    var col = 0;
                    while (Native.ghostty_render_state_row_cells_next(cells))
                    {
                        var grapheme = CellUtf8(cells);
                        text.Append(grapheme.Length > 0 ? grapheme : " ");
                        styles.Append(StyleFlags(cells));
                        col++;
                        if (col >= cols) break;
                    }
                    if (y < 24 || y % 10 == 0)
                        Console.WriteLine($"  r{y:D2} {Escape(text.ToString())}");
                    var markers = styles.ToString();
                    if (markers.Trim().Length > 0 && y < 24)
                        Console.WriteLine($"     {Escape(markers)}");
                    y++;
                }
                Console.WriteLine($"  total viewport rows: {y}");
            }
            finally
            {
                Native.ghostty_render_state_row_cells_free(cells);
                Native.ghostty_render_state_row_iterator_free(rowIt);
            }
        }
        finally
        {
            Native.ghostty_render_state_free(state);
        }
    }

    private static unsafe string CellUtf8(IntPtr cells)
    {
        uint graphemesLen = 0;
        if (Native.ghostty_render_state_row_cells_get(cells, RenderStateRowCellsData.GraphemesLen, (IntPtr)(&graphemesLen)) != Result.Success || graphemesLen == 0)
            return "";
        var codepoints = new uint[Math.Min(graphemesLen, 32u)];
        fixed (uint* p = codepoints)
        {
            if (Native.ghostty_render_state_row_cells_get(cells, RenderStateRowCellsData.GraphemesBuf, (IntPtr)p) != Result.Success)
                return "";
        }
        var sb = new StringBuilder(codepoints.Length);
        foreach (var cp in codepoints)
            sb.Append(char.ConvertFromUtf32((int)cp));
        return sb.ToString();
    }

    private static unsafe string StyleFlags(IntPtr cells)
    {
        var style = new GhosttyStyle { size = (nuint)Marshal.SizeOf<GhosttyStyle>() };
        var r = Native.ghostty_render_state_row_cells_get(cells, RenderStateRowCellsData.Style, (IntPtr)(&style));
        if (r != Result.Success || style.bold == 0 && style.italic == 0 && style.underline == 0 &&
            style.strikethrough == 0 && style.inverse == 0)
            return " ";

        var flags = new StringBuilder();
        if (style.bold != 0) flags.Append('B');
        if (style.italic != 0) flags.Append('I');
        if (style.underline != 0) flags.Append('U');
        if (style.strikethrough != 0) flags.Append('S');
        if (style.inverse != 0) flags.Append('R');
        if (style.blink != 0) flags.Append('*');

        var fg = new GhosttyColorRgb();
        if (Native.ghostty_render_state_row_cells_get(cells, RenderStateRowCellsData.FgColor, (IntPtr)(&fg)) == Result.Success)
            flags.Append($"[{fg.r:X2}{fg.g:X2}{fg.b:X2}]");
        return flags.ToString();
    }

    private static unsafe void DumpRowCellsDetail(IntPtr cells, ushort cols)
    {
        Native.ghostty_render_state_row_cells_select(cells, 0);
        for (var x = 0; x < 16; x++)
        {
            var style = new GhosttyStyle { size = (nuint)Marshal.SizeOf<GhosttyStyle>() };
            Native.ghostty_render_state_row_cells_get(cells, RenderStateRowCellsData.Style, (IntPtr)(&style));
            var fg = new GhosttyColorRgb { r = 0xEE, g = 0xEE, b = 0xEE };
            var fgR = Native.ghostty_render_state_row_cells_get(cells, RenderStateRowCellsData.FgColor, (IntPtr)(&fg));
            var grapheme = CellUtf8(cells);
            Console.WriteLine(
                $"  [detail] x={x} '{grapheme}' bold={style.bold} it={style.italic} u={style.underline} inv={style.inverse} " +
                $"fgTag={style.fgColor.tag} fg={fg.r:X2}{fg.g:X2}{fg.b:X2}({fgR}) bgTag={style.bgColor.tag}");
            Native.ghostty_render_state_row_cells_next(cells);
        }
    }

    private static void OnTitleChanged(IntPtr terminal, IntPtr userdata)
    {
        Console.WriteLine($"  [effect] title_changed userdata=0x{userdata:X}");
    }

    private static void OnWritePty(IntPtr terminal, IntPtr userdata, IntPtr data, nuint len)
    {
        lock (s_ptyWrites)
        {
            var bytes = new byte[(int)len];
            Marshal.Copy(data, bytes, 0, (int)len);
            s_ptyWrites.Append(Encoding.ASCII.GetString(bytes));
        }
    }

    private static unsafe GhosttyString OnEnquiry(IntPtr terminal, IntPtr userdata)
    {
        fixed (byte* p = s_enquiryBytes)
            return new GhosttyString { ptr = (IntPtr)p, len = (nuint)s_enquiryBytes.Length };
    }

    private static unsafe GhosttyString OnXtversion(IntPtr terminal, IntPtr userdata)
    {
        fixed (byte* p = s_xtversionBytes)
            return new GhosttyString { ptr = (IntPtr)p, len = (nuint)s_xtversionBytes.Length };
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
            throw new InvalidOperationException($"assert failed: {what}");
    }

    private static string Escape(string s) => s.Replace("\u001b", "^[");
}
