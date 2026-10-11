using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vex.App;

/// <summary>A floating surface with shared rounded corners and vector clipping of its content.</summary>
public sealed class RoundedSurface : Border
{
    static RoundedSurface()
    {
        CornerRadiusProperty.OverrideMetadata(typeof(RoundedSurface),
            new FrameworkPropertyMetadata(new CornerRadius(12),
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (Child is UIElement child && finalSize.Width > 0 && finalSize.Height > 0)
        {
            var offset = VisualTreeHelper.GetOffset(child);
            var bounds = new Rect(-offset.X, -offset.Y, finalSize.Width, finalSize.Height);
            var geometry = CreateCornerGeometry(bounds, CornerRadius);
            var mask = new DrawingBrush(new GeometryDrawing(Brushes.White, null, geometry))
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = bounds,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = bounds,
                Stretch = Stretch.None,
            };
            mask.Freeze();
            // Mask only the content: masking Border's already-antialiased paint
            // multiplies edge coverage twice and darkens the outline. Absolute
            // bounds prevent the corner radius stretching when results resize.
            child.SetCurrentValue(OpacityMaskProperty, mask);
        }
        return arranged;
    }

    private static StreamGeometry CreateCornerGeometry(Rect bounds, CornerRadius corners)
    {
        var limit = Math.Min(bounds.Width, bounds.Height) / 2;
        var topLeft = Math.Min(corners.TopLeft, limit);
        var topRight = Math.Min(corners.TopRight, limit);
        var bottomRight = Math.Min(corners.BottomRight, limit);
        var bottomLeft = Math.Min(corners.BottomLeft, limit);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(bounds.Left + topLeft, bounds.Top), isFilled: true, isClosed: true);
            context.LineTo(new Point(bounds.Right - topRight, bounds.Top), isStroked: true, isSmoothJoin: false);
            context.ArcTo(new Point(bounds.Right, bounds.Top + topRight), new Size(topRight, topRight), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(bounds.Right, bounds.Bottom - bottomRight), true, false);
            context.ArcTo(new Point(bounds.Right - bottomRight, bounds.Bottom), new Size(bottomRight, bottomRight), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(bounds.Left + bottomLeft, bounds.Bottom), true, false);
            context.ArcTo(new Point(bounds.Left, bounds.Bottom - bottomLeft), new Size(bottomLeft, bottomLeft), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(bounds.Left, bounds.Top + topLeft), true, false);
            context.ArcTo(new Point(bounds.Left + topLeft, bounds.Top), new Size(topLeft, topLeft), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
