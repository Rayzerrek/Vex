using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private void RedrawAll(bool force = false)
    {
        // A full redraw repaints every cell, including the one under the
        // block cursor, so the caret overlay must redraw with it (also covers
        // font metrics/DPI/resize changes that move the caret geometry).
        InvalidateCaretCache();
        for (var row = 0; row < _rowVisuals.Count; row++)
            RedrawRow(row, force);
        DrawScrollbar();
    }

    private void RedrawRow(int row, bool force = false)
    {
        if (row < 0 || row >= _rowVisuals.Count)
            return;

        var frameRows = _terminal.FrameRows;
        if (row >= frameRows.Length || frameRows[row].Cells.Length == 0)
        {
            _previewRevision++;
            _rowVersions[row] = 0;
            _rowLinks[row] = Array.Empty<LinkSpan>();
            _rowLinkTexts[row] = null;
            using var clearDc = _rowVisuals[row].RenderOpen();
            return;
        }

        var frameRow = frameRows[row];

        // Skip the re-render when this row's cells are unchanged since it was
        // last drawn — the common case after a redundant full redraw.
        var hash = RowHash(frameRow, _cols);
        if (!force && _rowVersions[row] == _renderVersion && _rowHashes[row] == hash)
            return;
        _previewRevision++;
#if DEBUG || VEX_SELFTEST
        if (_performanceSample is { } sample) sample.RowsDrawn++;
#endif

        // The link scan is keyed by the same hash: equal hash means the row
        // content is identical, so the spans stay valid between redraws.
        var links = _rowLinkHashes[row] == hash ? _rowLinks[row] : (_rowLinks[row] = ComputeRowLinks(frameRow, row));

        using var dc = _rowVisuals[row].RenderOpen();

        var y = Math.Round(row * _cellHeight * _pixelsPerDip) / _pixelsPerDip;
        // A rounded top plus an unrounded height leaves gaps at fractional DPI.
        // Share the next row's snapped boundary for both backgrounds and block glyphs.
        var rowHeight = Math.Round((row + 1) * _cellHeight * _pixelsPerDip) / _pixelsPerDip - y;
        var cells = frameRow.Cells;
        ref readonly var first = ref cells[0];
        var runFgTag = first.FgTag;
        var runFg = first.FgValue;
        var runBgTag = first.BgTag;
        var runBg = first.BgValue;
        var runFlags = first.Flags;
        var runIsBlockElement = IsBlockElement(first.Text);
        var runStart = 0;
        _runBuilder.Clear();
        EnsureTextRunCapacity(Math.Max(1, _cols));
        // One width entry per UTF-16 unit the run loop copies, so a row of
        // multi-codepoint clusters needs more than the old 2*cols guess. An
        // undersized buffer threw IndexOutOfRange mid-row, and because every
        // retry aborted at the same row, everything below it stopped
        // repainting — stale/duplicated text until the row's content changed.
        EnsureRunWidthCapacity(Math.Max(1, RowTextUnits(cells, _cols)));
        var textRuns = 0;
        var runWidthCount = 0;

        static bool IsBlockElement(ReadOnlySpan<char> text) =>
            text.Length == 1 && text[0] is >= '\u2580' and <= '\u259F';

        var colCount = Math.Min(_cols, cells.Length);
        for (var col = 0; col < colCount; col++)
        {
            ref readonly var cell = ref cells[col];
            var isBlockElement = cell.Tail ? runIsBlockElement : IsBlockElement(cell.Text);

            // Text and block graphics need different contrast policies even with identical SGR.
            var cellChanged = cell.FgTag != runFgTag || cell.FgValue != runFg ||
                              cell.BgTag != runBgTag || cell.BgValue != runBg ||
                              cell.Flags != runFlags || isBlockElement != runIsBlockElement;
            if (cellChanged && _runBuilder.Length > 0)
            {
                textRuns = FlushRun(dc, _runBuilder, _runWidths, runWidthCount, runStart, y, runFgTag, runFg, runBgTag, runBg, runFlags, runIsBlockElement, textRuns);
                runStart = col;
                runFgTag = cell.FgTag;
                runFg = cell.FgValue;
                runBgTag = cell.BgTag;
                runBg = cell.BgValue;
                runFlags = cell.Flags;
                runIsBlockElement = isBlockElement;
                _runBuilder.Clear();
                runWidthCount = 0;
            }
            else if (cellChanged)
            {
                runStart = col;
                runFgTag = cell.FgTag;
                runFg = cell.FgValue;
                runBgTag = cell.BgTag;
                runBg = cell.BgValue;
                runFlags = cell.Flags;
                runIsBlockElement = isBlockElement;
            }

            if (cell.Tail)
                continue; // wide glyph stub: the base cell owns the advance
            if (cell.Text.Length == 0)
            {
                _runBuilder.Append(' ');
                _runWidths[runWidthCount++] = 1;
            }
            else
            {
                // One width entry per UTF-16 unit: the first char of a cell
                // carries the cell advance, continuation chars carry zero so
                // the glyph cluster advances once (astral and ZWJ clusters).
                _runBuilder.Append(cell.Text);
                _runWidths[runWidthCount++] = cell.Wide ? 2 : 1;
                for (var i = 1; i < cell.Text.Length; i++)
                    _runWidths[runWidthCount++] = 0;
            }
        }

        if (_runBuilder.Length > 0)
            textRuns = FlushRun(dc, _runBuilder, _runWidths, runWidthCount, runStart, y, runFgTag, runFg, runBgTag, runBg, runFlags, runIsBlockElement, textRuns);

        // Draw underlines for runs that have text; the pooled glyph arrays
        // index into the textRuns we recorded.
        for (var i = 0; i < textRuns; i++)
        {
            ref readonly var run = ref _textRuns[i];
            if (!run.HasDecoration)
                continue;
            var runX1 = Math.Round(run.StartCol * _cellWidth * _pixelsPerDip) / _pixelsPerDip;
            var runX2 = Math.Round((run.StartCol + run.Length) * _cellWidth * _pixelsPerDip) / _pixelsPerDip;
            var runWidth = runX2 - runX1;
            var runX = runX1;
            var pen = _palette.GetPen(run.Foreground ?? _palette.Foreground);
            var rowY = y;
            if (run.Underline)
                dc.DrawLine(pen, new Point(runX, rowY + _cellHeight - pen.Thickness), new Point(runX + runWidth, rowY + _cellHeight - pen.Thickness));
            if (run.CrossedOut)
                dc.DrawLine(pen, new Point(runX, rowY + _cellHeight / 2), new Point(runX + runWidth, rowY + _cellHeight / 2));
        }

        // Detected URLs get their own underline, independent of SGR styles.
        foreach (var link in links)
        {
            var linkX1 = Math.Round(link.StartCol * _cellWidth * _pixelsPerDip) / _pixelsPerDip;
            var linkX2 = Math.Round((link.EndCol + 1) * _cellWidth * _pixelsPerDip) / _pixelsPerDip;
            var linkWidth = linkX2 - linkX1;
            var linkX = linkX1;
            dc.DrawLine(_linkPen, new Point(linkX, y + _cellHeight - _linkPen.Thickness), new Point(linkX + linkWidth, y + _cellHeight - _linkPen.Thickness));
        }

        // Only now is the row painted in full, so only now may it be cached:
        // marking it earlier remembered a half-drawn row as up to date, which
        // left the missing pixels on screen until something else changed.
        _rowVersions[row] = _renderVersion;
        _rowHashes[row] = hash;
        _rowLinkHashes[row] = hash;

        int FlushRun(DrawingContext context, StringBuilder runText, int[] widths, int cellCount, int startCol, double rowY,
            ColorTag fgTag, int fgValue, ColorTag bgTag, int bgValue, CellFlags flags, bool isBlockElement, int textRunIndex)
        {
            _palette.Resolve(fgTag, fgValue, bgTag, bgValue, flags, out var fg, out var bg, correctContrast: !isBlockElement);
            var x = Math.Round(startCol * _cellWidth * _pixelsPerDip) / _pixelsPerDip;

            // One width entry per advance-consuming char (wide glyph stubs
            // are already skipped), so the column extent is the width sum.
            var cellsSpanned = 0;
            for (var i = 0; i < cellCount; i++)
                cellsSpanned += widths[i];

            var x2 = Math.Round((startCol + cellsSpanned) * _cellWidth * _pixelsPerDip) / _pixelsPerDip;

            // Only paint cells that carry their own background color. The
            // opaque base layer already covers default cells; filling every
            // run again adds work and can expose seams at fractional widths.
            if (bg is not null)
                context.DrawRectangle(bg, null, new Rect(x, rowY, x2 - x, rowHeight));

            // Trailing whitespace carries no ink; skip the text pass for it.
            var contentEnd = runText.Length;
            while (contentEnd > 0 && runText[contentEnd - 1] == ' ')
                contentEnd--;
            if (contentEnd == 0 || fg is null)
            {
                RecordRun(textRunIndex, startCol, cellsSpanned, fg, flags);
                return textRunIndex + 1;
            }

            var face = ResolveTypeface(flags);
            var glyphFace = ResolveGlyphTypeface(flags);
            var asciiTable = ResolveAsciiGlyphs(flags);
            // resolution fell back to the regular face), emulate bold by
            // double-drawing the run with a 1px horizontal offset. Note: a
            // null glyphFace (FormattedText fallback) also needs this, so the
            // check is "did we NOT get a real bold face".
            var syntheticBold = (flags & CellFlags.Bold) != 0 && !ReferenceEquals(glyphFace, _boldGlyph);

            var contentLength = contentEnd;
            // Read the trimmed run directly from the run builder; no per-run
            // copy, so the segment loop and FormattedText below index runText
            // up to contentEnd.
            var trimmedText = runText;

            // Walk the trimmed run cell by cell. A cell whose every codepoint
            // has a glyph in the face joins a shared GlyphRun segment; any
            // other cell (emoji, symbols outside the family) is drawn alone
            // through FormattedText — whose shaping and font fallback can
            // find a glyph in another family — pinned to its exact grid
            // column. Falling back for the WHOLE run used natural font
            // advances, drifting every later character off the grid: text
            // slid under the next run's background and read as doubled or
            // overlapping letters whenever an emoji sat mid-line.
            var map = glyphFace?.CharacterToGlyphMap;
            var colCursor = startCol;

            if (glyphFace is not null && map is not null)
            {
                // WPF retains the final arrays until its render thread consumes
                // the visual. Glyph indices need copies; immutable advances for
                // single-column glyphs can be shared. Staging stays private.
                EnsureSegScratch(contentLength);
                var segIndices = _segIndicesScratch;
                var segAdvances = _segAdvancesScratch;
                var segUnits = 0;
                var segStartCol = startCol;
                var segSingleWidth = true;

                void FlushSegment()
                {
                    if (segUnits == 0)
                        return;
                    var indices = new ushort[segUnits];
                    // Cache lookups outweigh allocation savings for short style/fallback fragments.
                    var advances = segSingleWidth && segUnits >= 16
                        ? GetGlyphAdvances(segAdvances.AsSpan(0, segUnits), segStartCol)
                        : segAdvances.AsSpan(0, segUnits).ToArray();
                    Array.Copy(segIndices, indices, segUnits);
                    var sx = Math.Round(segStartCol * _cellWidth * _pixelsPerDip) / _pixelsPerDip;
                    DrawGlyph(indices, advances, sx);
                    segUnits = 0;
                    segSingleWidth = true;
                }

                void DrawGlyph(ushort[] indices, double[] advances, double originX)
                {
                    var run = new GlyphRun(
                        glyphFace,
                        0,
                        false,
                        _fontSize,
                        (float)_pixelsPerDip,
                        indices,
                        new Point(originX, rowY + _baselineY),
                        advances,
                        null, null, null, null, null, null);
                    context.DrawGlyphRun(fg, run);
                    if (!syntheticBold)
                        return;
                    // Second pass offset by 1 physical pixel (fractional in DIPs
                    // for subpixel crispness), the classic cheap fake-bold.
                    var boldRun = new GlyphRun(
                        glyphFace,
                        0,
                        false,
                        _fontSize,
                        (float)_pixelsPerDip,
                        indices,
                        new Point(originX + (1.0 / _pixelsPerDip), rowY + _baselineY),
                        advances,
                        null, null, null, null, null, null);
                    context.DrawGlyphRun(fg, boldRun);
                }

                var i = 0;
                while (i < contentLength)
                {
                    // A cell spans one width-consuming unit plus its
                    // zero-width continuation units.
                    var unitStart = i;
                    i++;
                    while (i < contentLength && widths[i] == 0)
                        i++;

                    var firstGlyph = segUnits;
                    if (segUnits == 0)
                        segStartCol = colCursor;

                    // Procedural rendering for block elements, powerline, and box drawing:
                    // guarantees 0-gap pixel-perfect alignment with cell boundaries.
                    var cellLen = i - unitStart;
                    int singleCp = 0;
                    if (cellLen == 1)
                    {
                        singleCp = trimmedText[unitStart];
                    }
                    else if (cellLen == 2 && char.IsHighSurrogate(trimmedText[unitStart]) && char.IsLowSurrogate(trimmedText[unitStart + 1]))
                    {
                        singleCp = char.ConvertToUtf32(trimmedText[unitStart], trimmedText[unitStart + 1]);
                    }

                    if (singleCp != 0 && CustomGlyphRenderer.CanDraw(singleCp))
                    {
                        FlushSegment();
                        var cx1 = Math.Round(colCursor * _cellWidth * _pixelsPerDip) / _pixelsPerDip;
                        var cx2 = Math.Round((colCursor + widths[unitStart]) * _cellWidth * _pixelsPerDip) / _pixelsPerDip;
                        if (CustomGlyphRenderer.Draw(context, singleCp, cx1, rowY, cx2 - cx1, rowHeight, fg, _pixelsPerDip))
                        {
                            colCursor += widths[unitStart];
                            continue;
                        }
                    }

                    // A multi-codepoint cell needs shaping (combining marks,
                    // variation selectors and joiners), even when the face
                    // has every glyph. Raw advances detach accents from the
                    // base character and bypass ligature composition.
                    var cellOk = singleCp != 0;
                    var u = unitStart;
                    while (cellOk && u < i)
                    {
                        var ch = trimmedText[u];
                        int cp;
                        var span = 1;
                        if (char.IsHighSurrogate(ch) && u + 1 < i && char.IsLowSurrogate(trimmedText[u + 1]))
                        {
                            cp = char.ConvertToUtf32(ch, trimmedText[u + 1]);
                            span = 2;
                        }
                        else
                        {
                            cp = ch;
                        }
                        ushort glyphIndex;
                        if ((uint)cp < 128u && asciiTable is not null)
                        {
                            glyphIndex = asciiTable[cp];
                            if (glyphIndex == 0)
                            {
                                cellOk = false;
                                break;
                            }
                        }
                        else if (!map.TryGetValue(cp, out glyphIndex))
                        {
                            cellOk = false;
                            break;
                        }
                        segIndices[segUnits] = glyphIndex;
                        segAdvances[segUnits] = 0;
                        segUnits++;
                        u += span;
                    }
                    if (cellOk)
                    {
                        segSingleWidth &= widths[unitStart] == 1;
                        // Snap both endpoints to the same physical grid used
                        // by segment origins and cell backgrounds. Fractional
                        // advances otherwise accumulate a different phase
                        // after emoji or an ANSI style splits the GlyphRun.
                        segAdvances[firstGlyph] = (Math.Round((colCursor + widths[unitStart]) * _cellWidth * _pixelsPerDip)
                            - Math.Round(colCursor * _cellWidth * _pixelsPerDip)) / _pixelsPerDip;
                    }
                    else
                    {
                        segUnits = firstGlyph;
                        FlushSegment();
                        DrawFallbackCell(context, trimmedText.ToString(unitStart, i - unitStart), face, fg,
                            syntheticBold, widths[unitStart], colCursor, rowY);
                    }
                    colCursor += widths[unitStart];
                }
                FlushSegment();
            }
            else
            {
                // Composite font families can have no single GlyphTypeface.
                // They still need the terminal grid, rather than the natural
                // advances of a whole-run FormattedText fallback.
                var i = 0;
                while (i < contentLength)
                {
                    var unitStart = i++;
                    while (i < contentLength && widths[i] == 0)
                        i++;
                    DrawFallbackCell(context, trimmedText.ToString(unitStart, i - unitStart), face, fg,
                        syntheticBold, widths[unitStart], colCursor, rowY);
                    colCursor += widths[unitStart];
                }
            }
            RecordRun(textRunIndex, startCol, cellsSpanned, fg, flags);
            return textRunIndex + 1;
        }

        void RecordRun(int index, int startCol, int cellsSpanned, Brush? fg, CellFlags flags)
        {
            ref var run = ref _textRuns[index];
            run.StartCol = startCol;
            run.Length = cellsSpanned;
            run.Foreground = fg;
            run.HasDecoration = (flags & (CellFlags.Underline | CellFlags.Strikethrough)) != 0;
            run.Underline = (flags & CellFlags.Underline) != 0;
            run.CrossedOut = (flags & CellFlags.Strikethrough) != 0;
        }
    }

    private void EnsureTextRunCapacity(int required)
    {
        if (_textRuns.Length >= required)
            return;
        Array.Resize(ref _textRuns, required);
    }

    private void EnsureRunWidthCapacity(int required)
    {
        if (_runWidths.Length >= required)
            return;
        Array.Resize(ref _runWidths, required);
    }

    private struct TextRun
    {
        public int StartCol;
        public int Length;
        public bool HasDecoration;
        public bool Underline;
        public bool CrossedOut;
        public Brush? Foreground;
    }

    private TextRun[] _textRuns = Array.Empty<TextRun>();

    // Staging buffers for the pending GlyphRun segment inside FlushRun. Only
    // the flushed per-segment copies are handed to the DrawingContext (which
    // retains them); the staging area itself is reused across runs to avoid
    // two array allocations per text run per row per frame.
    private ushort[] _segIndicesScratch = Array.Empty<ushort>();
    private double[] _segAdvancesScratch = Array.Empty<double>();

    private void EnsureSegScratch(int required)
    {
        if (_segIndicesScratch.Length < required)
        {
            _segIndicesScratch = new ushort[required];
            _segAdvancesScratch = new double[required];
        }
    }
    /// <summary>
    /// FNV-1a over the cells RedrawRow renders: (text, width, flags, fg, bg)
    /// per column, mirroring the render loop's cell selection so equal hashes
    /// guarantee equal pixels for the current metrics. Every character of a
    /// cell's text is hashed so two different clusters of the same length
    /// cannot collide.
    /// </summary>
    internal static ulong RowHash(FrameRow row, int cols)
    {
        const ulong prime = 1099511628211;
        ulong hash = 14695981039346656037;
        var cells = row.Cells;
        var maxCol = Math.Min(cols, cells.Length);
        for (var col = 0; col < maxCol; col++)
        {
            ref readonly var cell = ref cells[col];
            var text = cell.Text;
            if (text is not null)
            {
                for (var i = 0; i < text.Length; i++)
                    hash = (hash ^ text[i]) * prime;
                hash = (hash ^ (ulong)(uint)text.Length) * prime;
            }
            ulong packedMeta = ((ulong)(uint)cell.FgValue << 32) | (uint)cell.BgValue;
            ulong packedFlags = ((ulong)(uint)cell.FgTag << 24)
                              | ((ulong)(uint)cell.BgTag << 16)
                              | ((ulong)(byte)cell.Flags << 8)
                              | (cell.Wide ? 2u : 0u)
                              | (cell.Tail ? 1u : 0u);
            hash = (hash ^ packedMeta) * prime;
            hash = (hash ^ packedFlags) * prime;
        }
        return hash;
    }

    private ushort[]? ResolveAsciiGlyphs(CellFlags flags)
    {
        var bold = (flags & CellFlags.Bold) != 0;
        var italic = (flags & CellFlags.Italic) != 0;
        if (bold && _boldGlyph is not null)
        {
            if (!italic)
                return _boldAscii;
            return _boldItalicAscii ?? _boldAscii;
        }
        if (italic)
            return _italicAscii;
        return _normalAscii;
    }

    private Typeface ResolveTypeface(CellFlags flags)
    {
        var bold = (flags & CellFlags.Bold) != 0;
        var italic = (flags & CellFlags.Italic) != 0;
        return (bold, italic) switch
        {
            (true, true) => _boldItalicTypeface,
            (true, false) => _boldTypeface,
            (false, true) => _italicTypeface,
            _ => _normalTypeface,
        };
    }

    private GlyphTypeface? ResolveGlyphTypeface(CellFlags flags)
    {
        var bold = (flags & CellFlags.Bold) != 0;
        var italic = (flags & CellFlags.Italic) != 0;
        if (bold && _boldGlyph is { } boldFace)
        {
            // Bold falls back to the regular face when the family lacks a
            // bold face (Cascadia Mono has one, but user-chosen families may
            // not); the caller then emulates bold via a thicker pen.
            if (!italic)
                return boldFace;
            return _boldItalicGlyph ?? boldFace;
        }
        if (italic)
            return _italicGlyph;
        return _normalGlyph;
    }
}
