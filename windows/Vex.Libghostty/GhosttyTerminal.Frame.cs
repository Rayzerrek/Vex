using System.Text;
using static Vex.Libghostty.Native;

namespace Vex.Libghostty;

public sealed partial class GhosttyTerminal
{
    /// <summary>
    /// Pulls the latest terminal state into the render state and copies the
    /// viewport into managed <see cref="FrameRows"/>. Row/cell arrays are
    /// reused between calls; data is only valid until the next call.
    /// <para>
    /// Dirty contract: this method consumes global and per-row dirty flags.
    /// Clean frames skip the cell walk entirely (cursor still updates).
    /// Full / resize / alt-screen / host scroll force a complete cell read.
    /// Partial frames re-read every row: emulator scroll shifts under-report
    /// per-row dirty, and stale managed cells are what produced visual
    /// duplication. Per-row dirty is still recorded for the paint layer.
    /// </para>
    /// </summary>
    public unsafe void UpdateFrame()
    {
        // The whole snapshot is one critical section: the reader thread may
        // feed concurrently, and render_state_update reads the terminal.
        lock (_vtLock)
        {
            UpdateFrameLocked();
        }
    }

    private unsafe void UpdateFrameLocked()
    {
        Check(Native.ghostty_render_state_update(_renderState, _terminal), "render_state_update");

        FrameDirty dirty = FrameDirty.Clean;
        ushort cols = 0, rows = 0;
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.Dirty, (IntPtr)(&dirty)), "get dirty");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.Cols, (IntPtr)(&cols)), "get cols");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.Rows, (IntPtr)(&rows)), "get rows");
        FrameDirty = dirty;

        // Alt-screen enter/exit rematerializes the whole viewport; treat it
        // like a full rebuild even when the dirty bit stays Partial.
        var screen = TryGet(TerminalData.ActiveScreen, out TerminalScreen active)
            ? active
            : _lastScreen;
        var screenChanged = screen != _lastScreen;
        _lastScreen = screen;

        var sizeChanged = cols != _cols || rows != _rows;
        var readAll = _needsReadAll || dirty == FrameDirty.Full || sizeChanged || screenChanged;
        _needsReadAll = false;
        _cols = cols;
        _rows = rows;

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
        var pendingWrap = cursorInViewport != 0
            && TryGet(TerminalData.CursorPendingWrap, out byte cursorPendingWrap) && cursorPendingWrap != 0;
        Cursor = new CursorState(cursorX, cursorY, cursorVisible != 0, cursorBlink != 0, cursorStyle, pendingWrap);

        // Nothing changed since the last consumed snapshot: keep managed
        // cells as-is. Avoids a full grid P/Invoke walk on caret/UI pumps.
        if (dirty == FrameDirty.Clean && !readAll)
            return;

        // Partial still walks every row (scroll shifts under-report dirty).
        // readAll covers Full / resize / alt-screen / host scroll.
        EnsureFrameRows(rows);
        var rowIterator = _rowIterator;
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.RowIterator, (IntPtr)(&rowIterator)), "row iterator");

        byte rowDirtyFalse = 0;
        var row = 0;
        while (Native.ghostty_render_state_row_iterator_next(_rowIterator))
        {
            var frameRow = FrameRows[row];
            byte rowDirty = 0;
            Check(Native.ghostty_render_state_row_get(_rowIterator, RenderStateRowData.Dirty, (IntPtr)(&rowDirty)), "row dirty");
            frameRow.Dirty = rowDirty != 0 || readAll;

            var selection = new Native.GhosttyRenderStateRowSelection { size = (nuint)sizeof(Native.GhosttyRenderStateRowSelection) };
            var selResult = Native.ghostty_render_state_row_get(_rowIterator, RenderStateRowData.Selection, (IntPtr)(&selection));
            frameRow.HasSelection = selResult == Result.Success;
            if (frameRow.HasSelection)
            {
                frameRow.SelectionStart = selection.startX;
                frameRow.SelectionEnd = selection.endX;
            }

            // Always materialize cells on dirty frames: selective skips were
            // the source of duplicated/stale rows after emulator scrolls.
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
                cells[col] = new CellInfo { Text = "" };

            // Consume per-row dirty so the next snapshot reports only rows
            // that change after this point.
            Check(Native.ghostty_render_state_row_set(_rowIterator, RenderStateRowOption.Dirty, (IntPtr)(&rowDirtyFalse)), "row dirty clear");

            row++;
            if (row >= rows)
                break;
        }

        // Clear the global dirty state too: it accumulates across updates and
        // would otherwise force a full read (and full repaint) on every frame.
        if (dirty != FrameDirty.Clean)
        {
            var clean = FrameDirty.Clean;
            Check(Native.ghostty_render_state_set(_renderState, RenderStateOption.Dirty, (IntPtr)(&clean)), "dirty clear");
        }
    }

    private unsafe void ReadCell(ref CellInfo cell)
    {
        Check(Native.ghostty_render_state_row_cells_get_raw(_rowCells, RenderStateRowCellsData.Raw, out var raw), "cell raw");
        var style = Native.GhosttyStyle.New();
        // The pinned ABI packs the u16 style ID at bits 26-41 and the two
        // width bits at 42-43. ID zero always means the default style; avoid
        // a native style conversion for the majority of prompt/scrollback cells.
        if (((raw >> 26) & 0xFFFF) != 0)
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

        var wide = (Native.CellWide)((raw >> 42) & 0b11);
        cell.Wide = wide == Native.CellWide.Wide;
        cell.Tail = wide == Native.CellWide.WideSpacerTail;

        // A cell's background can live in the cell CONTENT rather than the
        // style: erases with a color set ("clear" in TUI apps) write cells
        // whose content is a palette index or RGB and whose style is the
        // default. Paint those like any other background.
        // Packed cell layout for the pinned ghostty@f64f4aca ABI:
        // content_tag = bits 0-1, content = bits 2-25. Decoding this in
        // managed code avoids another P/Invoke for every blank grid cell.
        var contentTag = (int)(raw & 0b11);
        if (cell.BgTag == ColorTag.None && contentTag == 2)
        {
            // color_palette: packed struct(u24) { data: u8 at bits 2-9 }
            cell.BgTag = ColorTag.Palette;
            cell.BgValue = (int)((raw >> 2) & 0xFF);
        }
        else if (cell.BgTag == ColorTag.None && contentTag == 3)
        {
            // color_rgb: packed RGB at bits 2-25, LSB first: r 2-9, g 10-17,
            // b 18-25. Repack into (r<<16)|(g<<8)|b like style RGB values.
            var content = (int)((raw >> 2) & 0xFFFFFF);
            var cr = content & 0xFF;
            var cg = (content >> 8) & 0xFF;
            var cb = (content >> 16) & 0xFF;
            cell.BgTag = ColorTag.Rgb;
            cell.BgValue = (cr << 16) | (cg << 8) | cb;
        }

        // The pinned ghostty@f64f4aca cell stores a single Unicode scalar in
        // bits 2-22. Only grapheme clusters need the native buffer walk;
        // ordinary text and empty/color-only cells need no further calls.
        if (contentTag != 1 || cell.Tail)
        {
            var cp = contentTag == 0 && !cell.Tail ? (uint)((raw >> 2) & 0x1FFFFF) : 0;
            if (cp == 0)
                cell.Text = "";
            else if (cp < (uint)s_asciiStrings.Length)
                cell.Text = s_asciiStrings[(int)cp];
            else
            {
                Span<uint> scalar = stackalloc uint[1] { cp };
                if (!MatchesCodepoints(cell.Text, scalar))
                    cell.Text = char.ConvertFromUtf32((int)cp);
            }
            return;
        }

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
            var cp = codepoints[0];
            if (cp < (uint)s_asciiStrings.Length)
                cell.Text = s_asciiStrings[(int)cp];
            else if (!MatchesCodepoints(cell.Text, codepoints.AsSpan(0, 1)))
                cell.Text = char.ConvertFromUtf32((int)cp);
        }
        else if (!MatchesCodepoints(cell.Text, codepoints.AsSpan(0, (int)graphemesLen)))
        {
            // Partial frames still read the whole viewport for scroll coherence.
            // Retain unchanged Unicode text instead of allocating it every frame.
            var sb = _graphemeBuilder;
            sb.Clear();
            Span<char> utf16 = stackalloc char[2];
            for (var i = 0; i < graphemesLen; i++)
            {
                var rune = new Rune((int)codepoints[i]);
                var length = rune.EncodeToUtf16(utf16);
                sb.Append(utf16[..length]);
            }
            cell.Text = sb.ToString();
        }
    }

    private static bool MatchesCodepoints(string text, ReadOnlySpan<uint> codepoints)
    {
        var offset = 0;
        foreach (var codepoint in codepoints)
        {
            if (offset >= text.Length || !Rune.TryGetRuneAt(text, offset, out var rune) || rune.Value != codepoint)
                return false;
            offset += rune.Utf16SequenceLength;
        }
        return offset == text.Length;
    }

    private void EnsureFrameRows(int rows)
    {
        if (FrameRows.Length != rows)
        {
            var previous = FrameRows;
            FrameRows = new FrameRow[rows];
            for (var i = 0; i < rows; i++)
                FrameRows[i] = i < previous.Length ? previous[i] : new FrameRow();
        }
        // A width-only resize keeps the row count unchanged, so the early
        // return above cannot be the only guard: the per-row cell arrays must
        // still grow to the new width. Size every row unconditionally.
        for (var i = 0; i < rows; i++)
        {
            if (FrameRows[i].Cells.Length != _cols)
            {
                var cells = new CellInfo[_cols];
                for (var c = 0; c < _cols; c++)
                    cells[c].Text = "";
                FrameRows[i].Cells = cells;
            }
        }
    }
}
