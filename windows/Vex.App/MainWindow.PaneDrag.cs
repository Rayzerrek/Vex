using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vex.App.Model;

namespace Vex.App;

public sealed partial class MainWindow
{
    private void PaneTitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only start a drag from the title bar itself, not from its buttons.
        if (FindAncestorButton(e.OriginalSource as DependencyObject) is not null)
            return;
        if (sender is FrameworkElement { DataContext: LeafPane leaf })
        {
            if (_workspace.SelectedProject?.SelectedTab is { } tab)
                tab.ActiveLeaf = leaf;

            _dragPane = leaf;
            _dragStart = e.GetPosition(this);
            _draggingPane = false;
            _dropZone = DropZone.None;
            // Capture now so the move/up are delivered even when the cursor
            // leaves the title bar during the drag.
            System.Windows.Input.Mouse.Capture(this);
        }
    }

    private void MainWindow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
            return;

        // A click on a non-interactive surface (tab strip, pane chrome) leaves
        // keyboard focus stranded in an overlay text box (e.g. search, settings),
        // so typing goes nowhere. Let the click settle, and if nothing took focus,
        // return it to the active pane.
        if (System.Windows.Input.Keyboard.FocusedElement is not TextBox focused)
            return;
        if (IsVisualDescendantOf(focused, e.OriginalSource as DependencyObject))
            return; // clicking the box itself repositions the caret

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, focused))
                _workspace.SelectedProject?.SelectedTab?.ActiveLeaf?.Focus();
        });
    }

    private static bool IsVisualDescendantOf(DependencyObject ancestor, DependencyObject? node)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor))
                return true;
        }
        return false;
    }

    private static Button? FindAncestorButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Button button)
                return button;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void MainWindow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HandleTabDragMove(e);

        if (_dragPane is null)
            return;

        if (!_draggingPane)
        {
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _dragStart.X) + Math.Abs(pos.Y - _dragStart.Y) < 8)
                return;
            _draggingPane = true;
            DropOverlay.Visibility = Visibility.Visible;
            HideSplitPreview();
        }

        var point = e.GetPosition(ContentArea);
        var (target, bounds) = PaneUnder(point);
        var zone = target is not null && ReferenceEquals(target, _dragPane)
            ? ZoneInBounds(point, bounds)
            : DropZone.None;
        _dropZone = zone;
        if (zone != DropZone.None)
            ShowSplitPreview(PreviewRect(bounds, zone));
        else
            HideSplitPreview();
        e.Handled = true;
    }

    private void MainWindow_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HandleTabDragDrop();

        var pane = _dragPane;
        var zone = _dropZone;
        var wasDragging = _draggingPane;
        _draggingPane = false;
        _dragPane = null;
        _dropZone = DropZone.None;

        // Release the drag capture taken on mouse-down (even for a plain click).
        if (System.Windows.Input.Mouse.Captured == this)
            System.Windows.Input.Mouse.Capture(null);

        if (!wasDragging)
            return;

        DropOverlay.Visibility = Visibility.Collapsed;
        HideSplitPreview();

        if (pane is not null && zone != DropZone.None)
        {
            pane.Split(zone == DropZone.Left || zone == DropZone.Right
                ? Orientation.Horizontal
                : Orientation.Vertical);
        }
        e.Handled = true;
    }

    private (LeafPane? Pane, Rect Bounds) PaneUnder(System.Windows.Point point)
    {
        var hosts = new List<(LeafPane Pane, FrameworkElement Element)>();
        CollectPaneHosts(ContentArea, hosts);
        foreach (var (pane, element) in hosts)
        {
            var bounds = element.TransformToAncestor(ContentArea)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (bounds.Contains(point))
                return (pane, bounds);
        }
        // Fallback: if the visual walk found nothing, treat the dragged pane as
        // filling the whole content area so the drop still works.
        if (hosts.Count == 0 && _dragPane is not null)
            return (_dragPane, new Rect(0, 0, ContentArea.ActualWidth, ContentArea.ActualHeight));
        return (null, default);
    }


    private static void CollectPaneHosts(DependencyObject node, List<(LeafPane Pane, FrameworkElement Element)> result)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is FrameworkElement fe && fe.DataContext is LeafPane pane)
            {
                result.Add((pane, fe));
                continue; // a pane never nests another pane; skip its content
            }
            CollectPaneHosts(child, result);
        }
    }

    private static DropZone ZoneInBounds(System.Windows.Point p, Rect bounds)
    {
        var x = (p.X - bounds.X) / bounds.Width;
        var y = (p.Y - bounds.Y) / bounds.Height;
        const double edge = 0.25;
        if (x < edge && y >= edge && y <= 1 - edge) return DropZone.Left;
        if (x > 1 - edge && y >= edge && y <= 1 - edge) return DropZone.Right;
        if (y < edge && x >= edge && x <= 1 - edge) return DropZone.Top;
        if (y > 1 - edge && x >= edge && x <= 1 - edge) return DropZone.Bottom;
        return DropZone.None;
    }

    private static Rect PreviewRect(Rect bounds, DropZone zone)
    {
        const double fraction = 0.42;
        const double inset = 4;
        return zone switch
        {
            DropZone.Left => new Rect(bounds.X + inset, bounds.Y + inset, bounds.Width * fraction - inset, bounds.Height - inset * 2),
            DropZone.Right => new Rect(bounds.X + bounds.Width * (1 - fraction), bounds.Y + inset, bounds.Width * fraction - inset, bounds.Height - inset * 2),
            DropZone.Top => new Rect(bounds.X + inset, bounds.Y + inset, bounds.Width - inset * 2, bounds.Height * fraction - inset),
            DropZone.Bottom => new Rect(bounds.X + inset, bounds.Y + bounds.Height * (1 - fraction), bounds.Width - inset * 2, bounds.Height * fraction - inset),
            _ => Rect.Empty,
        };
    }

    private void ShowSplitPreview(Rect rect)
    {
        Canvas.SetLeft(SplitPreview, rect.X);
        Canvas.SetTop(SplitPreview, rect.Y);
        SplitPreview.Width = rect.Width;
        SplitPreview.Height = rect.Height;
        if (SplitPreview.Visibility == Visibility.Visible)
            return;
        SplitPreview.Visibility = Visibility.Visible;
        SplitPreview.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, UiMotion.FeedbackDuration)
            {
                EasingFunction = UiMotion.FeedbackEasing,
            });
    }

    private void HideSplitPreview()
    {
        if (SplitPreview.Visibility == Visibility.Collapsed)
            return;
        SplitPreview.BeginAnimation(OpacityProperty, null);
        SplitPreview.Opacity = 0;
        SplitPreview.Visibility = Visibility.Collapsed;
    }
}
