using System.Windows;
using System.Windows.Media;

namespace Vex.App;

/// <summary>
/// Attached CornerRadius clip: Border.CornerRadius only rounds the border's
/// own background, children still paint into the corners. This property
/// applies a rounded RectangleGeometry as the element's Clip, tracking size
/// changes, so pane contents (the terminal surface) follow the pane chrome.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip),
        new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(UIElement element) => (double)element.GetValue(RadiusProperty);

    public static void SetRadius(UIElement element, double value) => element.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;
        Apply(element);
        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element)
            Apply(element);
    }

    private static void Apply(FrameworkElement element)
    {
        var radius = GetRadius(element);
        element.Clip = radius <= 0 || element.ActualWidth <= 0 || element.ActualHeight <= 0
            ? null
            : new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
    }
}
