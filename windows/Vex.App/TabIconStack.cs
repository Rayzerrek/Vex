using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vex.App.Model;

namespace Vex.App;

/// <summary>Stable application icon stack, capped at three icons plus a count so busy tabs retain room for their title.</summary>
public sealed class TabIconStack : Control
{
    private const int MaximumVisibleIcons = 3;
    private static readonly Typeface OverflowTypeface = new("Segoe UI");

    /// <summary>Pane icons; changes resize and redraw the tab icon stack.</summary>
    public static readonly DependencyProperty IconsProperty = DependencyProperty.Register(
        nameof(Icons), typeof(IReadOnlyList<AppIcon>), typeof(TabIconStack),
        new FrameworkPropertyMetadata(Array.Empty<AppIcon>(),
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Icons in stable split-tree order, including repeated applications.</summary>
    public IReadOnlyList<AppIcon> Icons
    {
        get => (IReadOnlyList<AppIcon>)GetValue(IconsProperty);
        set => SetValue(IconsProperty, value);
    }

    protected override Size MeasureOverride(Size constraint) => Icons.Count switch
    {
        0 => new Size(0, 0),
        1 => new Size(16, 16),
        _ => new Size(22 + (Math.Min(Icons.Count, MaximumVisibleIcons) - 1) * 7
            + (Icons.Count > MaximumVisibleIcons ? 20 : 0), 22),
    };

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Icons.Count == 1)
        {
            drawingContext.DrawImage(Icons[0].Image, new Rect(0, 0, 16, 16));
            return;
        }

        var outline = new Pen(BorderBrush, 1);
        if (BorderBrush is { IsFrozen: true })
            outline.Freeze();
        var visible = Math.Min(Icons.Count, MaximumVisibleIcons);
        if (Icons.Count > visible)
        {
            // Overflow has its own unobscured tile, rather than hiding its
            // number behind the preceding icon's overlapping surface.
            var left = 22 + (visible - 1) * 7.0;
            drawingContext.DrawRoundedRectangle(Background, outline, new Rect(left + 0.5, 0.5, 19, 21), 4, 4);
            var count = new FormattedText("+" + (Icons.Count - visible), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, OverflowTypeface, 9, Foreground ?? Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(count, new Point(left + (20 - count.Width) / 2, (22 - count.Height) / 2));
        }
        for (var index = visible - 1; index >= 0; index--)
        {
            var left = index * 7.0;
            drawingContext.DrawRoundedRectangle(Background, outline,
                new Rect(left + 0.5, 0.5, 21, 21), 4, 4);
            drawingContext.DrawImage(Icons[index].Image, new Rect(left + 3, 3, 16, 16));
        }
    }
}
