using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Vex.App.Model;

/// <summary>
/// A tab icon: either a brand glyph tinted with its color or a rounded letter
/// badge for tools without a brand mark (aider, lazygit, ...). Immutable and
/// cached per key so every pane shares one frozen image.
/// </summary>
public sealed class AppIcon
{
    private static readonly Dictionary<string, AppIcon> Cache = new();
    private static readonly Typeface BadgeTypeface =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private AppIcon(DrawingImage image)
    {
        Image = image;
    }

    /// <summary>Vector image, 16x16 logical units, safe to bind to Image.Source.</summary>
    public DrawingImage Image { get; }

    internal static AppIcon Glyph(string slug)
    {
        var key = $"glyph:{slug}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        if (AppIconCatalog.GeometryFor(slug) is not { } resolved)
            throw new ArgumentException($"Unknown simple-icons slug '{slug}'.", nameof(slug));
        var icon = new AppIcon(BuildGlyphImage(resolved.Geometry, resolved.Color));
        Cache[key] = icon;
        return icon;
    }

    internal static AppIcon Badge(string text, Color color)
    {
        var key = $"badge:{text}:{color}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        var icon = new AppIcon(BuildBadgeImage(text, color));
        Cache[key] = icon;
        return icon;
    }

    private static DrawingImage BuildGlyphImage(Geometry glyph, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var drawing = new GeometryDrawing(brush, null, glyph);
        drawing.Freeze();

        var group = new DrawingGroup
        {
            // simple-icons ship a 24x24 viewBox; the tab renders 16x16.
            Transform = new ScaleTransform(16.0 / 24.0, 16.0 / 24.0),
        };
        group.Children.Add(drawing);
        group.Freeze();

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static DrawingImage BuildBadgeImage(string text, Color color)
    {
        var group = new DrawingGroup();

        // Rounded tile in a darkened brand gradient: deep at the bottom,
        // slightly lifted at the top, ringed by the brand color so the badge
        // still reads as "belonging to that tool" on the dark tab strip.
        var bottom = Darken(color, 0.32);
        var top = Darken(color, 0.55);
        var gradient = new LinearGradientBrush(top, bottom, 90);
        gradient.Freeze();
        var tile = new RectangleGeometry(new Rect(0.5, 0.5, 15, 15), 4.5, 4.5);
        tile.Freeze();
        var fill = new GeometryDrawing(gradient, null, tile);
        fill.Freeze();
        group.Children.Add(fill);

        var ring = new SolidColorBrush(Color.FromArgb(0x9E, color.R, color.G, color.B));
        ring.Freeze();
        var outline = new GeometryDrawing(null, new Pen(ring, 1), tile);
        outline.Freeze();
        group.Children.Add(outline);

        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            BadgeTypeface, text.Length > 1 ? 8 : 9.5, Brushes.White, 1.0);
        var labelGeometry = label.BuildGeometry(new Point((16 - label.Width) / 2, (16 - label.Height) / 2));
        labelGeometry.Freeze();
        var labelDrawing = new GeometryDrawing(Brushes.White, null, labelGeometry);
        labelDrawing.Freeze();
        group.Children.Add(labelDrawing);

        group.Freeze();

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static Color Darken(Color color, double factor) => Color.FromRgb(
        (byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));
}
