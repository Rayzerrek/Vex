using System.Windows;
using System.Windows.Media;

namespace Vex.App.Terminal.Native;

/// <summary>
/// Renders Unicode Block Elements (U+2580..U+259F), Powerline symbols (U+E0B0..U+E0B7),
/// and Box Drawing characters directly with geometric primitives.
///
/// Standard monospace fonts (including Cascadia Mono) have font ascender/descender margins
/// and line-height gaps that cause 1-pixel seams or protruding pixels against cell backgrounds.
/// Procedural rendering guarantees 0-gap pixel-perfect alignment with cell boundaries.
/// </summary>
internal static class CustomGlyphRenderer
{
    public static bool CanDraw(int codepoint)
    {
        return codepoint switch
        {
            // Block Elements (U+2580..U+259F)
            >= 0x2580 and <= 0x259F => true,

            // Powerline symbols (U+E0B0..U+E0B7)
            >= 0xE0B0 and <= 0xE0B7 => true,

            // Box Drawing common characters (U+2500..U+257F)
            >= 0x2500 and <= 0x257F => true,

            _ => false,
        };
    }

    public static bool Draw(DrawingContext context, int codepoint, double x, double y,
        double width, double height, Brush brush, double pixelsPerDip)
    {
        if (DrawBlockElement(context, codepoint, x, y, width, height, brush))
            return true;

        if (DrawPowerline(context, codepoint, x, y, width, height, brush))
            return true;

        if (DrawBoxDrawing(context, codepoint, x, y, width, height, brush, pixelsPerDip))
            return true;

        return false;
    }

    private static bool DrawBlockElement(DrawingContext context, int codepoint, double x, double y,
        double width, double height, Brush brush)
    {
        switch (codepoint)
        {
            // Full block
            case 0x2588:
                context.DrawRectangle(brush, null, new Rect(x, y, width, height));
                return true;

            // Lower blocks U+2581..U+2587
            case 0x2581: // Lower 1/8
                context.DrawRectangle(brush, null, new Rect(x, y + height * 7.0 / 8.0, width, height / 8.0));
                return true;
            case 0x2582: // Lower 1/4
                context.DrawRectangle(brush, null, new Rect(x, y + height * 6.0 / 8.0, width, height * 2.0 / 8.0));
                return true;
            case 0x2583: // Lower 3/8
                context.DrawRectangle(brush, null, new Rect(x, y + height * 5.0 / 8.0, width, height * 3.0 / 8.0));
                return true;
            case 0x2584: // Lower 1/2
                context.DrawRectangle(brush, null, new Rect(x, y + height / 2.0, width, height - height / 2.0));
                return true;
            case 0x2585: // Lower 5/8
                context.DrawRectangle(brush, null, new Rect(x, y + height * 3.0 / 8.0, width, height * 5.0 / 8.0));
                return true;
            case 0x2586: // Lower 3/4
                context.DrawRectangle(brush, null, new Rect(x, y + height * 2.0 / 8.0, width, height * 6.0 / 8.0));
                return true;
            case 0x2587: // Lower 7/8
                context.DrawRectangle(brush, null, new Rect(x, y + height / 8.0, width, height * 7.0 / 8.0));
                return true;

            // Upper blocks
            case 0x2580: // Upper 1/2
                context.DrawRectangle(brush, null, new Rect(x, y, width, height / 2.0));
                return true;
            case 0x2594: // Upper 1/8
                context.DrawRectangle(brush, null, new Rect(x, y, width, height / 8.0));
                return true;

            // Left blocks U+2589..U+258F
            case 0x2589: // Left 7/8
                context.DrawRectangle(brush, null, new Rect(x, y, width * 7.0 / 8.0, height));
                return true;
            case 0x258A: // Left 3/4
                context.DrawRectangle(brush, null, new Rect(x, y, width * 6.0 / 8.0, height));
                return true;
            case 0x258B: // Left 5/8
                context.DrawRectangle(brush, null, new Rect(x, y, width * 5.0 / 8.0, height));
                return true;
            case 0x258C: // Left 1/2
                context.DrawRectangle(brush, null, new Rect(x, y, width / 2.0, height));
                return true;
            case 0x258D: // Left 3/8
                context.DrawRectangle(brush, null, new Rect(x, y, width * 3.0 / 8.0, height));
                return true;
            case 0x258E: // Left 1/4
                context.DrawRectangle(brush, null, new Rect(x, y, width * 2.0 / 8.0, height));
                return true;
            case 0x258F: // Left 1/8
                context.DrawRectangle(brush, null, new Rect(x, y, width / 8.0, height));
                return true;

            // Right blocks
            case 0x2590: // Right 1/2
                context.DrawRectangle(brush, null, new Rect(x + width / 2.0, y, width - width / 2.0, height));
                return true;
            case 0x2595: // Right 1/8
                context.DrawRectangle(brush, null, new Rect(x + width * 7.0 / 8.0, y, width / 8.0, height));
                return true;

            // Shades
            case 0x2591: // Light shade
                context.PushOpacity(0.25);
                context.DrawRectangle(brush, null, new Rect(x, y, width, height));
                context.Pop();
                return true;
            case 0x2592: // Medium shade
                context.PushOpacity(0.50);
                context.DrawRectangle(brush, null, new Rect(x, y, width, height));
                context.Pop();
                return true;
            case 0x2593: // Dark shade
                context.PushOpacity(0.75);
                context.DrawRectangle(brush, null, new Rect(x, y, width, height));
                context.Pop();
                return true;

            // Quadrants U+2596..U+259F
            case 0x2596: // Quadrant lower left
                context.DrawRectangle(brush, null, new Rect(x, y + height / 2.0, width / 2.0, height - height / 2.0));
                return true;
            case 0x2597: // Quadrant lower right
                context.DrawRectangle(brush, null, new Rect(x + width / 2.0, y + height / 2.0, width - width / 2.0, height - height / 2.0));
                return true;
            case 0x2598: // Quadrant upper left
                context.DrawRectangle(brush, null, new Rect(x, y, width / 2.0, height / 2.0));
                return true;
            case 0x2599: // Quadrant upper left, lower left, lower right
                context.DrawRectangle(brush, null, new Rect(x, y, width / 2.0, height));
                context.DrawRectangle(brush, null, new Rect(x + width / 2.0, y + height / 2.0, width - width / 2.0, height - height / 2.0));
                return true;
            case 0x259A: // Quadrant upper left, lower right
                context.DrawRectangle(brush, null, new Rect(x, y, width / 2.0, height / 2.0));
                context.DrawRectangle(brush, null, new Rect(x + width / 2.0, y + height / 2.0, width - width / 2.0, height - height / 2.0));
                return true;
            case 0x259B: // Quadrant upper left, upper right, lower left
                context.DrawRectangle(brush, null, new Rect(x, y, width, height / 2.0));
                context.DrawRectangle(brush, null, new Rect(x, y + height / 2.0, width / 2.0, height - height / 2.0));
                return true;
            case 0x259C: // Quadrant upper left, upper right, lower right
                context.DrawRectangle(brush, null, new Rect(x, y, width, height / 2.0));
                context.DrawRectangle(brush, null, new Rect(x + width / 2.0, y + height / 2.0, width - width / 2.0, height - height / 2.0));
                return true;
            case 0x259D: // Quadrant upper right
                context.DrawRectangle(brush, null, new Rect(x + width / 2.0, y, width - width / 2.0, height / 2.0));
                return true;
            case 0x259E: // Quadrant upper right, lower left
                context.DrawRectangle(brush, null, new Rect(x + width / 2.0, y, width - width / 2.0, height / 2.0));
                context.DrawRectangle(brush, null, new Rect(x, y + height / 2.0, width / 2.0, height - height / 2.0));
                return true;
            case 0x259F: // Quadrant upper right, lower left, lower right
                context.DrawRectangle(brush, null, new Rect(x + width / 2.0, y, width - width / 2.0, height));
                context.DrawRectangle(brush, null, new Rect(x, y + height / 2.0, width / 2.0, height - height / 2.0));
                return true;

            default:
                return false;
        }
    }

    private static bool DrawPowerline(DrawingContext context, int codepoint, double x, double y,
        double width, double height, Brush brush)
    {
        switch (codepoint)
        {
            case 0xE0B0: // Powerline right arrow (solid triangle)
            {
                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    ctx.BeginFigure(new Point(x, y), isFilled: true, isClosed: true);
                    ctx.LineTo(new Point(x + width, y + height / 2.0), isStroked: false, isSmoothJoin: false);
                    ctx.LineTo(new Point(x, y + height), isStroked: false, isSmoothJoin: false);
                }
                geom.Freeze();
                context.DrawGeometry(brush, null, geom);
                return true;
            }
            case 0xE0B2: // Powerline left arrow (solid triangle)
            {
                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    ctx.BeginFigure(new Point(x + width, y), isFilled: true, isClosed: true);
                    ctx.LineTo(new Point(x, y + height / 2.0), isStroked: false, isSmoothJoin: false);
                    ctx.LineTo(new Point(x + width, y + height), isStroked: false, isSmoothJoin: false);
                }
                geom.Freeze();
                context.DrawGeometry(brush, null, geom);
                return true;
            }
            case 0xE0B4: // Powerline right rounded (half circle)
            {
                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    ctx.BeginFigure(new Point(x, y), isFilled: true, isClosed: true);
                    ctx.ArcTo(new Point(x, y + height), new Size(width, height / 2.0), 0, false, SweepDirection.Clockwise, isStroked: false, isSmoothJoin: false);
                }
                geom.Freeze();
                context.DrawGeometry(brush, null, geom);
                return true;
            }
            case 0xE0B6: // Powerline left rounded (half circle)
            {
                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    ctx.BeginFigure(new Point(x + width, y), isFilled: true, isClosed: true);
                    ctx.ArcTo(new Point(x + width, y + height), new Size(width, height / 2.0), 0, false, SweepDirection.Counterclockwise, isStroked: false, isSmoothJoin: false);
                }
                geom.Freeze();
                context.DrawGeometry(brush, null, geom);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool DrawBoxDrawing(DrawingContext context, int codepoint, double x, double y,
        double width, double height, Brush brush, double pixelsPerDip)
    {
        // 1 physical pixel thickness in DIPs
        var thickness = Math.Max(1.0, Math.Round(1.0 * pixelsPerDip)) / pixelsPerDip;
        var heavyThickness = thickness * 2.0;

        var midX = Math.Round((x + width / 2.0) * pixelsPerDip) / pixelsPerDip;
        var midY = Math.Round((y + height / 2.0) * pixelsPerDip) / pixelsPerDip;

        var halfT = thickness / 2.0;
        var halfHT = heavyThickness / 2.0;

        void DrawHLine(double startX, double endX, double cy, double th)
        {
            context.DrawRectangle(brush, null, new Rect(startX, cy - th / 2.0, endX - startX, th));
        }

        void DrawVLine(double startY, double endY, double cx, double th)
        {
            context.DrawRectangle(brush, null, new Rect(cx - th / 2.0, startY, th, endY - startY));
        }

        switch (codepoint)
        {
            // Light horizontal line
            case 0x2500:
                DrawHLine(x, x + width, midY, thickness);
                return true;

            // Heavy horizontal line
            case 0x2501:
                DrawHLine(x, x + width, midY, heavyThickness);
                return true;

            // Light vertical line
            case 0x2502:
                DrawVLine(y, y + height, midX, thickness);
                return true;

            // Heavy vertical line
            case 0x2503:
                DrawVLine(y, y + height, midX, heavyThickness);
                return true;

            // Light corners
            case 0x250C: // ┌ down and right
                DrawHLine(midX - halfT, x + width, midY, thickness);
                DrawVLine(midY - halfT, y + height, midX, thickness);
                return true;
            case 0x2510: // ┐ down and left
                DrawHLine(x, midX + halfT, midY, thickness);
                DrawVLine(midY - halfT, y + height, midX, thickness);
                return true;
            case 0x2514: // └ up and right
                DrawHLine(midX - halfT, x + width, midY, thickness);
                DrawVLine(y, midY + halfT, midX, thickness);
                return true;
            case 0x2518: // ┘ up and left
                DrawHLine(x, midX + halfT, midY, thickness);
                DrawVLine(y, midY + halfT, midX, thickness);
                return true;

            // Rounded corners
            case 0x256D: // ╭
            case 0x256E: // ╮
            case 0x256F: // ╯
            case 0x2570: // ╰
            {
                // Clean rounded quarter arc
                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    if (codepoint == 0x256D) // ╭ down and right
                    {
                        ctx.BeginFigure(new Point(midX, y + height), isFilled: false, isClosed: false);
                        ctx.ArcTo(new Point(x + width, midY), new Size(width / 2.0, height / 2.0), 0, false, SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: true);
                    }
                    else if (codepoint == 0x256E) // ╮ down and left
                    {
                        ctx.BeginFigure(new Point(x, midY), isFilled: false, isClosed: false);
                        ctx.ArcTo(new Point(midX, y + height), new Size(width / 2.0, height / 2.0), 0, false, SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: true);
                    }
                    else if (codepoint == 0x256F) // ╯ up and left
                    {
                        ctx.BeginFigure(new Point(x, midY), isFilled: false, isClosed: false);
                        ctx.ArcTo(new Point(midX, y), new Size(width / 2.0, height / 2.0), 0, false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
                    }
                    else // 0x2570: ╰ up and right
                    {
                        ctx.BeginFigure(new Point(midX, y), isFilled: false, isClosed: false);
                        ctx.ArcTo(new Point(x + width, midY), new Size(width / 2.0, height / 2.0), 0, false, SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: true);
                    }
                }
                geom.Freeze();
                var pen = new Pen(brush, thickness);
                pen.Freeze();
                context.DrawGeometry(null, pen, geom);
                return true;
            }

            // Light T-junctions
            case 0x251C: // ├ right, up, down
                DrawHLine(midX - halfT, x + width, midY, thickness);
                DrawVLine(y, y + height, midX, thickness);
                return true;
            case 0x2524: // ┤ left, up, down
                DrawHLine(x, midX + halfT, midY, thickness);
                DrawVLine(y, y + height, midX, thickness);
                return true;
            case 0x252C: // ┬ left, right, down
                DrawHLine(x, x + width, midY, thickness);
                DrawVLine(midY - halfT, y + height, midX, thickness);
                return true;
            case 0x2534: // ┴ left, right, up
                DrawHLine(x, x + width, midY, thickness);
                DrawVLine(y, midY + halfT, midX, thickness);
                return true;

            // Light cross
            case 0x253C: // ┼
                DrawHLine(x, x + width, midY, thickness);
                DrawVLine(y, y + height, midX, thickness);
                return true;

            // Double horizontal and vertical lines
            case 0x2550: // ═
            {
                var offset = Math.Max(1.0, Math.Round(2.0 * pixelsPerDip)) / pixelsPerDip;
                DrawHLine(x, x + width, midY - offset, thickness);
                DrawHLine(x, x + width, midY + offset, thickness);
                return true;
            }
            case 0x2551: // ║
            {
                var offset = Math.Max(1.0, Math.Round(2.0 * pixelsPerDip)) / pixelsPerDip;
                DrawVLine(y, y + height, midX - offset, thickness);
                DrawVLine(y, y + height, midX + offset, thickness);
                return true;
            }

            // Heavy corners
            case 0x250F: // ┏
                DrawHLine(midX - halfHT, x + width, midY, heavyThickness);
                DrawVLine(midY - halfHT, y + height, midX, heavyThickness);
                return true;
            case 0x2513: // ┓
                DrawHLine(x, midX + halfHT, midY, heavyThickness);
                DrawVLine(midY - halfHT, y + height, midX, heavyThickness);
                return true;
            case 0x2517: // ┗
                DrawHLine(midX - halfHT, x + width, midY, heavyThickness);
                DrawVLine(y, midY + halfHT, midX, heavyThickness);
                return true;
            case 0x251B: // ┛
                DrawHLine(x, midX + halfHT, midY, heavyThickness);
                DrawVLine(y, midY + halfHT, midX, heavyThickness);
                return true;

            // Heavy T-junctions
            case 0x2523: // ┣
                DrawHLine(midX - halfHT, x + width, midY, heavyThickness);
                DrawVLine(y, y + height, midX, heavyThickness);
                return true;
            case 0x252B: // ┫
                DrawHLine(x, midX + halfHT, midY, heavyThickness);
                DrawVLine(y, y + height, midX, heavyThickness);
                return true;
            case 0x2533: // ┳
                DrawHLine(x, x + width, midY, heavyThickness);
                DrawVLine(midY - halfHT, y + height, midX, heavyThickness);
                return true;
            case 0x253B: // ┻
                DrawHLine(x, x + width, midY, heavyThickness);
                DrawVLine(y, midY + halfHT, midX, heavyThickness);
                return true;

            // Heavy cross
            case 0x254B: // ╋
                DrawHLine(x, x + width, midY, heavyThickness);
                DrawVLine(y, y + height, midX, heavyThickness);
                return true;

            // Double corners
            case 0x2554: // ╔
            {
                var off = Math.Max(1.0, Math.Round(2.0 * pixelsPerDip)) / pixelsPerDip;
                DrawHLine(midX - off, x + width, midY - off, thickness);
                DrawHLine(midX + off, x + width, midY + off, thickness);
                DrawVLine(midY - off, y + height, midX - off, thickness);
                DrawVLine(midY + off, y + height, midX + off, thickness);
                return true;
            }
            case 0x2557: // ╗
            {
                var off = Math.Max(1.0, Math.Round(2.0 * pixelsPerDip)) / pixelsPerDip;
                DrawHLine(x, midX + off, midY - off, thickness);
                DrawHLine(x, midX - off, midY + off, thickness);
                DrawVLine(midY - off, y + height, midX + off, thickness);
                DrawVLine(midY + off, y + height, midX - off, thickness);
                return true;
            }
            case 0x255A: // ╚
            {
                var off = Math.Max(1.0, Math.Round(2.0 * pixelsPerDip)) / pixelsPerDip;
                DrawHLine(midX - off, x + width, midY + off, thickness);
                DrawHLine(midX + off, x + width, midY - off, thickness);
                DrawVLine(y, midY + off, midX - off, thickness);
                DrawVLine(y, midY - off, midX + off, thickness);
                return true;
            }
            case 0x255D: // ╝
            {
                var off = Math.Max(1.0, Math.Round(2.0 * pixelsPerDip)) / pixelsPerDip;
                DrawHLine(x, midX + off, midY + off, thickness);
                DrawHLine(x, midX - off, midY - off, thickness);
                DrawVLine(y, midY + off, midX + off, thickness);
                DrawVLine(y, midY - off, midX - off, thickness);
                return true;
            }

            // Double T-junctions
            case 0x2560: // ╠
            {
                var off = Math.Max(1.0, Math.Round(2.0 * pixelsPerDip)) / pixelsPerDip;
                DrawVLine(y, y + height, midX - off, thickness);
                DrawVLine(y, y + height, midX + off, thickness);
                DrawHLine(midX + off, x + width, midY - off, thickness);
                DrawHLine(midX + off, x + width, midY + off, thickness);
                return true;
            }
            case 0x2563: // ╣
            {
                var off = Math.Max(1.0, Math.Round(2.0 * pixelsPerDip)) / pixelsPerDip;
                DrawVLine(y, y + height, midX - off, thickness);
                DrawVLine(y, y + height, midX + off, thickness);
                DrawHLine(x, midX - off, midY - off, thickness);
                DrawHLine(x, midX - off, midY + off, thickness);
                return true;
            }
            case 0x2566: // ╦
            {
                var off = Math.Max(1.0, Math.Round(2.0 * pixelsPerDip)) / pixelsPerDip;
                DrawHLine(x, x + width, midY - off, thickness);
                DrawHLine(x, x + width, midY + off, thickness);
                DrawVLine(midY + off, y + height, midX - off, thickness);
                DrawVLine(midY + off, y + height, midX + off, thickness);
                return true;
            }
            case 0x2569: // ╩
            {
                var off = Math.Max(1.0, Math.Round(2.0 * pixelsPerDip)) / pixelsPerDip;
                DrawHLine(x, x + width, midY - off, thickness);
                DrawHLine(x, x + width, midY + off, thickness);
                DrawVLine(y, midY - off, midX - off, thickness);
                DrawVLine(y, midY - off, midX + off, thickness);
                return true;
            }

            // Double cross
            case 0x256C: // ╬
            {
                var off = Math.Max(1.0, Math.Round(2.0 * pixelsPerDip)) / pixelsPerDip;
                DrawHLine(x, x + width, midY - off, thickness);
                DrawHLine(x, x + width, midY + off, thickness);
                DrawVLine(y, y + height, midX - off, thickness);
                DrawVLine(y, y + height, midX + off, thickness);
                return true;
            }

            // Light half lines
            case 0x2574: // ╴ left
                DrawHLine(x, midX, midY, thickness);
                return true;
            case 0x2575: // ╵ up
                DrawVLine(y, midY, midX, thickness);
                return true;
            case 0x2576: // ╶ right
                DrawHLine(midX, x + width, midY, thickness);
                return true;
            case 0x2577: // ╷ down
                DrawVLine(midY, y + height, midX, thickness);
                return true;

            // Diagonals
            case 0x2571: // ╱
            {
                var pen = new Pen(brush, thickness);
                pen.Freeze();
                context.DrawLine(pen, new Point(x, y + height), new Point(x + width, y));
                return true;
            }
            case 0x2572: // ╲
            {
                var pen = new Pen(brush, thickness);
                pen.Freeze();
                context.DrawLine(pen, new Point(x, y), new Point(x + width, y + height));
                return true;
            }
            case 0x2573: // ╳
            {
                var pen = new Pen(brush, thickness);
                pen.Freeze();
                context.DrawLine(pen, new Point(x, y), new Point(x + width, y + height));
                context.DrawLine(pen, new Point(x, y + height), new Point(x + width, y));
                return true;
            }

            default:
                return false;
        }
    }
}
