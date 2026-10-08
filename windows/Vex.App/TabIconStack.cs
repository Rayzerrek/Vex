using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vex.App.Model;

namespace Vex.App;

/// <summary>Overlapping tab icons, with the first pane in front and one tile per application.</summary>
public sealed class TabIconStack : Control
{
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
        _ => new Size(22 + (Icons.Count - 1) * 7, 22),
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
        for (var index = Icons.Count - 1; index >= 0; index--)
        {
            var left = index * 7.0;
            drawingContext.DrawRoundedRectangle(Background, outline,
                new Rect(left + 0.5, 0.5, 21, 21), 5, 5);
            drawingContext.DrawImage(Icons[index].Image, new Rect(left + 3, 3, 16, 16));
        }
    }
}
