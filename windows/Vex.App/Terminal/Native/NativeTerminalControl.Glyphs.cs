using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private const int MaxFallbackCellDrawings = 512;
    private const int MaxCachedGlyphAdvances = 256;
    private readonly Dictionary<(int StartCol, int GlyphCount), double[]> _glyphAdvanceCache = new();
    private readonly Dictionary<FallbackCellKey, DrawingGroup> _fallbackCellDrawings = new();
    private readonly record struct FallbackCellKey(string Text, Typeface Typeface, Brush Foreground, bool SyntheticBold, int Columns);

    private double[] GetGlyphAdvances(ReadOnlySpan<double> advances, int startCol)
    {
        var key = (startCol, advances.Length);
        if (_glyphAdvanceCache.TryGetValue(key, out var cached))
            return cached;
        var snapshot = advances.ToArray();
        // WPF retains these arrays. Only immutable, single-column advances can be shared.
        if (_glyphAdvanceCache.Count >= MaxCachedGlyphAdvances)
            _glyphAdvanceCache.Clear();
        _glyphAdvanceCache[key] = snapshot;
        return snapshot;
    }

    private void DrawFallbackCell(DrawingContext context, string text, Typeface face, Brush foreground,
        bool syntheticBold, int columns, int col, double rowY)
    {
        var key = new FallbackCellKey(text, face, foreground, syntheticBold, columns);
        if (!_fallbackCellDrawings.TryGetValue(key, out var drawing))
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                face, _fontSize, foreground, _pixelsPerDip);
            var width = columns * _cellWidth;
            var scale = Math.Min(1, Math.Min(width / Math.Max(1, formatted.WidthIncludingTrailingWhitespace),
                _cellHeight / Math.Max(1, formatted.Height)));
            var x = Math.Max(0, (width - formatted.WidthIncludingTrailingWhitespace * scale) / 2);
            var y = Math.Clamp(_baselineY - formatted.Baseline * scale, 0, Math.Max(0, _cellHeight - formatted.Height * scale));
            drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawText(formatted, new Point(x / scale, y / scale));
                if (syntheticBold)
                    dc.DrawText(formatted, new Point((x + 1 / _pixelsPerDip) / scale, y / scale));
                dc.Pop();
            }
            drawing.Freeze();
            // Repeated emoji and combining clusters are expensive to shape.
            // Bound retained drawings so arbitrary output cannot grow this
            // cache for the lifetime of a long-running terminal session.
            if (_fallbackCellDrawings.Count >= MaxFallbackCellDrawings)
                _fallbackCellDrawings.Clear();
            _fallbackCellDrawings[key] = drawing;
        }
        var left = Math.Round(col * _cellWidth * _pixelsPerDip) / _pixelsPerDip;
        var right = Math.Round((col + columns) * _cellWidth * _pixelsPerDip) / _pixelsPerDip;
        context.PushClip(new RectangleGeometry(new Rect(left, rowY, right - left, _cellHeight)));
        context.PushTransform(new TranslateTransform(left, rowY));
        context.DrawDrawing(drawing);
        context.Pop();
        context.Pop();
    }
}
