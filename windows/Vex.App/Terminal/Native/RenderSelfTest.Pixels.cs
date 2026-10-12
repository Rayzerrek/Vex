#if DEBUG || VEX_SELFTEST
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

internal static partial class RenderSelfTest
{
    private static byte[] Capture(NativeTerminalControl control)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = Math.Max(1, (int)Math.Round(control.ActualHeight * dpi));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>Hard precondition inside the deterministic suite: reports and
    /// aborts so downstream checks do not read garbage.</summary>
    private static void Assert(NativeTerminalControl control, bool condition, string what)
    {
        if (condition)
            return;
        Report(control, $"FAIL assert: {what}");
        throw new InvalidOperationException($"selftest precondition failed: {what}");
    }

    /// <summary>Finds the horizontal shift in [-maxShift,maxShift] pixels
    /// that best aligns the band spanning columns [colFrom,colTo) of two
    /// viewport rows of one capture. Asserts grid alignment rather than
    /// pixel identity: the same glyphs at the same grid column rasterize
    /// with a slightly different AA phase depending on which run segment
    /// carries them, so raw diffs never reach zero even when aligned —
    /// but a real drift (whole-run font fallback moves the text by the
    /// emoji's natural-vs-grid advance difference) shifts the argmin away
    /// from 0 decisively.</summary>
    /// <returns>(bestShift, diffAtBest, diffAtZero).</returns>
    private static (int BestShift, int DiffAtBest, int DiffAtZero) BandAlignment(
        NativeTerminalControl control, byte[] pixels, int rowA, int rowB, int colFrom, int colTo, int maxShift)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var x0 = (int)Math.Round(colFrom * control.SelfTestCellWidth * dpi);
        var x1 = Math.Min(width - maxShift, (int)Math.Round(colTo * control.SelfTestCellWidth * dpi));
        var yA0 = (int)Math.Round(rowA * control.SelfTestCellHeight * dpi);
        var yA1 = Math.Min(height, (int)Math.Round((rowA + 1) * control.SelfTestCellHeight * dpi));
        var yB0 = (int)Math.Round(rowB * control.SelfTestCellHeight * dpi);
        var yB1 = Math.Min(height, (int)Math.Round((rowB + 1) * control.SelfTestCellHeight * dpi));
        var rows = Math.Min(yA1 - yA0, yB1 - yB0);

        int DiffAt(int shift)
        {
            var diff = 0;
            for (var dy = 0; dy < rows; dy++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var ia = ((yA0 + dy) * width + x) * 4;
                    var ib = ((yB0 + dy) * width + x + shift) * 4;
                    if (Math.Abs(pixels[ia] - pixels[ib]) > 8 ||
                        Math.Abs(pixels[ia + 1] - pixels[ib + 1]) > 8 ||
                        Math.Abs(pixels[ia + 2] - pixels[ib + 2]) > 8)
                        diff++;
                }
            }
            return diff;
        }

        var bestShift = 0;
        var diffAtBest = int.MaxValue;
        var diffAtZero = DiffAt(0);
        for (var s = -maxShift; s <= maxShift; s++)
        {
            var d = DiffAt(s);
            if (d < diffAtBest)
            {
                diffAtBest = d;
                bestShift = s;
            }
        }
        return (bestShift, diffAtBest, diffAtZero);
    }

    /// <summary>Writes a raw Pbgra32 capture to disk for visual inspection.</summary>
    internal static void DumpPng(NativeTerminalControl control, byte[] pixels, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var bitmap = BitmapSource.Create(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static bool PixelsEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    /// <summary>Number of row bands that carry ink (text or stale pixels). The
    /// scrollbar overlay occupies the right edge, so columns there are ignored.</summary>
    private static int InkBands(NativeTerminalControl control, byte[] pixels)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var rowPixels = Math.Max(1, (int)Math.Round(control.SelfTestCellHeight * dpi));
        var textWidth = width - (int)Math.Round(24 * dpi);

        // Background reference: the darkest pixels anywhere (text is brighter).
        var minLum = 765;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Pbgra32: premultiplied, but channel sums stay ordered for our
            // opaque-over-black base fill.
            var lum = pixels[i] + pixels[i + 1] + pixels[i + 2];
            if (lum < minLum)
                minLum = lum;
        }

        var bands = new StringBuilder();
        var y = 0;
        while (y < height)
        {
            var bandEnd = Math.Min(y + rowPixels, height);
            var hasInk = false;
            for (var py = y; py < bandEnd && !hasInk; py += 2)
            {
                var rowStart = py * width * 4;
                for (var px = 0; px < textWidth * 4; px += 8)
                {
                    var lum = pixels[rowStart + px] + pixels[rowStart + px + 1] + pixels[rowStart + px + 2];
                    if (lum > minLum + 90)
                    {
                        hasInk = true;
                        break;
                    }
                }
            }
            if (hasInk)
                bands.Append(y / rowPixels).Append(',');
            y += rowPixels;
        }
        Report(control, $"ink bands at: {bands}");
        return bands.Length == 0 ? 0 : bands.ToString().TrimEnd(',').Split(',').Length;
    }

    /// <summary>Tagged colors and flags of the first few cells of a row — the
    /// raw emulator state behind whatever pixels got rendered.</summary>
    private static string CellAttrDump(NativeTerminalControl control, int row)
    {
        return CellAttrDumpRange(control, row, 0, 8);
    }

    private static string CellAttrDumpRange(NativeTerminalControl control, int row, int start, int count)
    {
        var line = control.SelfTestLine(row);
        if (line is null)
            return "no-line";
        var sb = new StringBuilder();
        var cells = line.Cells;
        for (var c = start; c < start + count && c < cells.Length; c++)
        {
            var cell = cells[c];
            sb.Append($"{c}:'{(cell.Text.Length == 0 ? '·' : cell.Text)}'={(byte)cell.Flags:X2}/{cell.FgTag}{(cell.FgTag == ColorTag.Rgb ? cell.FgValue.ToString("X6") : cell.FgValue.ToString())} ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Replays a captured stream and asserts that every checked cell's
    /// painted background fill agrees with its emulator attribute: ghost
    /// pixels are cells whose rendered fill disagrees with the buffer.</summary>
    private static void ReplayCheck(NativeTerminalControl control, string label)
    {
        var shot = Capture(control);
        var caret = control.SelfTestCaretCell();
        var disagreements = 0;
        for (var row = 0; row < Math.Min(control.SelfTestRows, 12); row++)
        {
            var line = control.SelfTestLine(row);
            if (line is null)
                continue;
            var cells = line.Cells;
            for (var col = 0; col < Math.Min(24, cells.Length); col++)
            {
                // The block caret repaints its cell (cursor color); pixels
                // there are not cell content, so skip it.
                if (caret is { Row: var cr, Col: var cc } && row == cr && col == cc)
                    continue;
                ref readonly var cell = ref cells[col];
                // Cells with text legitimately carry glyph ink at any corner,
                // and the cell right after a text cell can carry the
                // overflow of a font-wide glyph (emoji are single-column in
                // ghostty's grid but their fallback glyphs are not);
                // background-fill verification (ghost pixel detection) only
                // makes sense on cells away from text.
                if (cell.Text.Length > 0)
                    continue;
                if (col > 0 && cells[col - 1].Text.Length > 0)
                    continue;
                var (bg, baseColor) = control.SelfTestResolveCell(cell);
                var expected = bg ?? baseColor;
                // Premultiplied over the opaque base fill: compare the corner
                // pixel channels to the resolved color's.
                var (r, g, b) = CellCornerRgb(control, shot, col, row);
                var tol = 60;
                if (Math.Abs(r - expected.R) > tol || Math.Abs(g - expected.G) > tol || Math.Abs(b - expected.B) > tol)
                {
                    disagreements++;
                    if (disagreements <= 10)
                        Report(control, $"{label} disagree row{row} col{col} flags={(byte)cell.Flags:X2} fg={cell.FgTag}:{cell.FgValue} bg={cell.BgTag}:{cell.BgValue} pixel=({r},{g},{b}) expected=({expected.R},{expected.G},{expected.B})");
                }
            }
        }
        Report(control, disagreements == 0 ? $"PASS {label}: pixels match buffer" : $"FAIL {label}: {disagreements} cells disagree");
    }

    private static string EscapeMark(string mark) =>
        string.Concat(mark.Select(c => c < 0x20 ? $"<{(int)c:x2}>" : c.ToString()));

    /// <summary>RGB at a cell's top-right corner — background fill only,
    /// away from glyph strokes.</summary>
    private static (byte R, byte G, byte B) CellCornerRgb(NativeTerminalControl control, byte[] pixels, int col, int row)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var x = Math.Min(width - 1, (int)Math.Round((col + 0.85) * control.SelfTestCellWidth * dpi));
        var y = Math.Min(height - 1, (int)Math.Round((row + 0.12) * control.SelfTestCellHeight * dpi));
        var i = (y * width + x) * 4;
        return (pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    /// <summary>Luminance at a cell's top-right corner — background fill only,
    /// away from glyph strokes.</summary>
    private static int CellCornerLum(NativeTerminalControl control, byte[] pixels, int col, int row)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var x = Math.Min(width - 1, (int)Math.Round((col + 0.85) * control.SelfTestCellWidth * dpi));
        var y = Math.Min(height - 1, (int)Math.Round((row + 0.12) * control.SelfTestCellHeight * dpi));
        var i = (y * width + x) * 4;
        return pixels[i] + pixels[i + 1] + pixels[i + 2];
    }

    /// <summary>Darkest channel sum in the frame: the base background fill.</summary>
    private static int MinLum(byte[] pixels)
    {
        var minLum = 765;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var lum = pixels[i] + pixels[i + 1] + pixels[i + 2];
            if (lum < minLum)
                minLum = lum;
        }
        return minLum;
    }

    /// <summary>Number of bright pixels inside the cell at (col,row): how much
    /// of the glyph there survives (a background-colored ghost glyph painted
    /// over it would eat into the ink).</summary>
    private static int CellInk(NativeTerminalControl control, byte[] pixels, int col, int row)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var minLum = 765;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var lum = pixels[i] + pixels[i + 1] + pixels[i + 2];
            if (lum < minLum)
                minLum = lum;
        }

        var x0 = (int)Math.Round(col * control.SelfTestCellWidth * dpi);
        var x1 = (int)Math.Round((col + 1) * control.SelfTestCellWidth * dpi);
        var y0 = (int)Math.Round(row * control.SelfTestCellHeight * dpi);
        var y1 = (int)Math.Round((row + 1) * control.SelfTestCellHeight * dpi);
        var ink = 0;
        for (var y = y0; y < y1 && y < height; y++)
        {
            for (var x = x0; x < x1 && x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > minLum + 90)
                    ink++;
            }
        }
        return ink;
    }

    /// <summary>Inked pixels along the bottom two pixel rows of a cell, where
    /// SGR and detected-link underlines land.</summary>
    private static int BottomLineInk(NativeTerminalControl control, byte[] pixels, int col, int row)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var minLum = 765;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var lum = pixels[i] + pixels[i + 1] + pixels[i + 2];
            if (lum < minLum)
                minLum = lum;
        }

        var x0 = (int)Math.Round(col * control.SelfTestCellWidth * dpi);
        var x1 = (int)Math.Round((col + 1) * control.SelfTestCellWidth * dpi);
        var y1 = (int)Math.Round((row + 1) * control.SelfTestCellHeight * dpi);
        var y0 = Math.Max(0, y1 - 2);
        var ink = 0;
        for (var y = y0; y < y1 && y < height; y++)
        {
            for (var x = x0; x < x1 && x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > minLum + 90)
                    ink++;
            }
        }
        return ink;
    }

    /// <summary>True when the cell at (col,row) contains any ink.</summary>
    private static bool HasInkAt(NativeTerminalControl control, byte[] pixels, int col, int row, int? backgroundLum = null)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var minLum = backgroundLum ?? MinLum(pixels);
        var cx = (int)Math.Round((col + 0.5) * control.SelfTestCellWidth * dpi);
        var cy = (int)Math.Round((row + 0.5) * control.SelfTestCellHeight * dpi);
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                if (x < 0 || y < 0 || x >= width || y >= height)
                    continue;
                var i = (y * width + x) * 4;
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > minLum + 90)
                    return true;
            }
        }
        return false;
    }
}
#endif
