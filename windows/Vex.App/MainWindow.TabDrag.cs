using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Vex.App.Model;

namespace Vex.App;

public partial class MainWindow
{
    private WorkspaceTab? _dragTab;
    private bool _dragTabActive;
    private Point _dragTabStart;
    private bool _markerVisible;
    // Per-tab animated horizontal offsets (TranslateTransform) that push tabs
    // aside during a reorder drag; keyed by container instance so offsets stay
    private readonly Dictionary<DependencyObject, TranslateTransform> _tabOffsets = new();
    private int _tabSlotIndex = -1;
    private double _ghostWidth;
    private bool _ghostVisible;

    /// <summary>Handles tab-bar drags: grows into a visible drag once past the
    /// threshold, and moves the insertion marker with the pointer.</summary>
    private void HandleTabDragMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_dragTab is null)
            return;

        // The tab may have been closed out from under the drag (its terminal
        // exited); drop the stale drag silently.
        if (_workspace.SelectedProject is not { } project || !project.Tabs.Contains(_dragTab))
        {
            CancelTabDrag();
            return;
        }

        if (!_dragTabActive)
        {
            var pos = e.GetPosition(TabDragCanvas);
            if (Math.Abs(pos.X - _dragTabStart.X) + Math.Abs(pos.Y - _dragTabStart.Y) < 6)
                return;
            _dragTabActive = true;
            System.Windows.Input.Mouse.Capture(this);
            ShowTabGhost();
        }

        UpdateTabDropMarker(e.GetPosition(TabDragCanvas));
        UpdateTabGhostPosition(e.GetPosition(TabDragCanvas));
        e.Handled = true;
    }

    /// <summary>Shows the drag ghost (a copy of the dragged tab's header)
    /// following the pointer, and hides the tab's original slot.</summary>
    private void ShowTabGhost()
    {
        if (_dragTab is null)
            return;
        // Capture the real width BEFORE hiding the slot (a collapsed element
        // measures 0).
        var slot = TabStrip.ItemContainerGenerator.ContainerFromItem(_dragTab) as FrameworkElement;
        var width = slot is null ? 120 : slot.ActualWidth;
        _ghostWidth = Math.Max(100, width);
        TabDragGhostHost.Width = _ghostWidth;
        TabDragGhost.Content = _dragTab;
        TabDragGhostHost.Height = Math.Min(30, TabBarGrid.ActualHeight - 6);
        _ghostVisible = true;
        Canvas.SetLeft(TabDragGhostHost, _dragTabStart.X - _ghostWidth / 2);
        Canvas.SetTop(TabDragGhostHost, (TabBarGrid.ActualHeight - TabDragGhostHost.Height) / 2.0);
        TabDragGhostHost.Visibility = Visibility.Visible;
        TabDragGhostHost.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        // Hide the original slot: it is now represented by the ghost.
        HideTabSlot(_dragTab);
    }

    /// <summary>Hides a tab's ListBoxItem container (the dragged tab's slot)
    /// so the strip shows a gap where it was picked up. Fades and shrinks it
    /// so the tab reads as "lifting off" rather than vanishing.</summary>
    private void HideTabSlot(WorkspaceTab tab)
    {
        var item = TabStrip.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement;
        if (item is null)
            return;
        var transform = GetTabOffset(item);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
        item.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)));
        // Collapse only after the fade so the gap opens smoothly.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            // The drag may have ended and the slot restored meanwhile; only
            // collapse if this tab is still the one being dragged.
            if (ReferenceEquals(_dragTab, tab))
                item.Visibility = Visibility.Collapsed;
        };
        t.Start();
    }

    private TranslateTransform GetTabOffset(FrameworkElement item)
    {
        if (_tabOffsets.TryGetValue(item, out var transform))
            return transform;
        transform = new TranslateTransform();
        item.RenderTransform = transform;
        _tabOffsets[item] = transform;
        return transform;
    }

    /// <summary>Animated push-away: tabs after the drop slot slide right so the
    /// strip always previews the final layout.</summary>
    private void AnimateTabPush(Dictionary<int, double> targetOffsets)
    {
        foreach (var item in TabStrip.Items)
        {
            var container = TabStrip.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
            if (container is null || ReferenceEquals(item, _dragTab))
                continue;
            var idx = TabStrip.Items.IndexOf(item);
            var target = targetOffsets.TryGetValue(idx, out var v) ? v : 0.0;
            var transform = GetTabOffset(container);
            if (Math.Abs(transform.X - target) < 0.5)
                continue;
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(transform.X, target, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }

    /// <summary>Computes the push offset (by collection index) that previews the
    /// final layout: every tab at or after the drop slot shifts right by the
    /// ghost's width, the rest stay put.</summary>
    private Dictionary<int, double> PushOffsets(WorkspaceTab dragged, int slot)
    {
        var offsets = new Dictionary<int, double>();
        var project = _workspace.SelectedProject!;
        var draggedIndex = project.Tabs.IndexOf(dragged);
        var visible = 0;
        for (var i = 0; i < project.Tabs.Count; i++)
        {
            if (i == draggedIndex)
                continue;
            if (visible >= slot)
                offsets[i] = _ghostWidth;
            visible++;
        }
        return offsets;
    }

    private void UpdateTabGhostPosition(Point pt)
    {
        if (!_ghostVisible || _dragTab is null)
            return;
        Canvas.SetLeft(TabDragGhostHost, pt.X - _ghostWidth / 2);
    }

    /// <summary>Snaps the insertion marker to the gap between tabs.</summary>
    private void UpdateTabDropMarker(Point pt)
    {
        if (_dragTab is null || _workspace.SelectedProject is not { } project)
            return;

        // A lone tab has nowhere to go, and leaving the strip vertically
        // hides the marker until the pointer comes back.
        var slot = project.Tabs.Count > 1 && pt.Y >= -8 && pt.Y <= TabBarGrid.ActualHeight + 8
            ? TabInsertIndexAt(pt, project.Tabs, _dragTab)
            : null;

        // Keep tabs pushed aside so the gap matches the ghost position.
        if (slot is { } s)
        {
            if (_tabSlotIndex != s)
            {
                _tabSlotIndex = s;
                AnimateTabPush(PushOffsets(_dragTab, s));
            }
        }
        else if (_tabSlotIndex != -1)
        {
            _tabSlotIndex = -1;
            AnimateTabPush(new Dictionary<int, double>());
        }

        if (!_markerVisible && slot is not null)
        {
            TabDropMarker.BeginAnimation(UIElement.OpacityProperty, null);
            TabDropMarker.Opacity = 0;
            TabDropMarker.Visibility = Visibility.Visible;
            _markerVisible = true;
        }

        if (slot is { } s2)
        {
            var left = VisibleSlotX(project.Tabs, _dragTab, s2) - 1.0;
            Canvas.SetTop(TabDropMarker, (TabBarGrid.ActualHeight - TabDropMarker.Height) / 2.0);
            TabDropMarker.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            Canvas.SetLeft(TabDropMarker, left);
            TabDropMarker.Width = 2.0;
        }
        else if (_markerVisible)
        {
            TabDropMarker.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(90)));
        }
    }

    /// <summary>Drop slot under the pointer: the number of visible tabs (all
    /// but the dragged one) whose right edge is left of the pointer. Null when
    /// the pointer sits over the dragged tab's own home slot, where dropping
    /// is a no-op. Widths are accumulated in layout space and shifted by the
    /// strip's scroll offset, so a scrolled strip still maps correctly (a
    /// visual transform would fold in the push animations and feed back).</summary>
    private int? TabInsertIndexAt(Point pt, ObservableCollection<WorkspaceTab> tabs, WorkspaceTab dragged)
    {
        var draggedIndex = tabs.IndexOf(dragged);
        var offset = TabScrollViewer?.HorizontalOffset ?? 0;
        var width = 0.0;
        var slot = 0;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (i == draggedIndex)
                continue;
            width += (TabStrip.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)?.ActualWidth ?? 0;
            if (pt.X <= width - offset)
                return slot == draggedIndex ? null : slot;
            slot++;
        }
        return slot == draggedIndex ? null : slot;
    }

    /// <summary>X of the gap after <paramref name="slot"/> visible tabs (all
    /// but the dragged one), i.e. where the drop marker sits.</summary>
    private double VisibleSlotX(ObservableCollection<WorkspaceTab> tabs, WorkspaceTab dragged, int slot)
    {
        var draggedIndex = tabs.IndexOf(dragged);
        var offset = TabScrollViewer?.HorizontalOffset ?? 0;
        var x = 0.0;
        var visible = 0;
        for (var i = 0; i < tabs.Count && visible < slot; i++)
        {
            if (i == draggedIndex)
                continue;
            x += (TabStrip.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)?.ActualWidth ?? 0;
            visible++;
        }
        return x - offset;
    }

    /// <summary>Drops a dragged tab. The strip already previews the final
    /// layout, so the collection move plus a transform reset (layout and
    /// offset cancel out exactly) puts the tab in place with no jump.</summary>
    private void HandleTabDragDrop()
    {
        var dragged = _dragTab;
        var wasActive = _dragTabActive;
        var slot = _tabSlotIndex;
        var project = _workspace.SelectedProject;
        _dragTab = null;
        _dragTabActive = false;
        _tabSlotIndex = -1;
        HideTabGhost();
        HideTabMarker();
        ReleaseTabDragCapture();

        if (!wasActive || dragged is null || slot < 0 || project is null)
        {
            ResetTabOffsets();
            return;
        }

        project.MoveTab(dragged, slot);
        // After the reorder each tab's layout position is exactly what the
        // push previewed, so clearing the offsets is invisible.
        ResetTabOffsets();
        TabStrip.UpdateLayout();
    }

    /// <summary>Restores the hidden dragged-tab slot and clears all push
    /// offsets.</summary>
    private void ResetTabOffsets()
    {
        foreach (var (item, transform) in _tabOffsets)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
            if (item is FrameworkElement fe)
            {
                fe.BeginAnimation(UIElement.OpacityProperty, null);
                fe.Opacity = 1;
                if (fe.Visibility == Visibility.Collapsed)
                    fe.Visibility = Visibility.Visible;
            }
        }
        _tabOffsets.Clear();
    }

    /// <summary>Clears all tab-drag state: hidden marker and ghost, restored
    /// offsets, released capture. Used when the dragged tab is closed
    /// mid-drag.</summary>
    private void CancelTabDrag()
    {
        _dragTab = null;
        _dragTabActive = false;
        _tabSlotIndex = -1;
        HideTabGhost();
        HideTabMarker();
        ResetTabOffsets();
        ReleaseTabDragCapture();
    }

    private void HideTabGhost()
    {
        if (!_ghostVisible)
            return;
        TabDragGhostHost.BeginAnimation(UIElement.OpacityProperty, null);
        TabDragGhostHost.Visibility = Visibility.Collapsed;
        TabDragGhostHost.Opacity = 0;
        _ghostVisible = false;
    }

    private void HideTabMarker()
    {
        if (!_markerVisible)
            return;
        TabDropMarker.BeginAnimation(UIElement.OpacityProperty, null);
        TabDropMarker.Visibility = Visibility.Collapsed;
        TabDropMarker.Opacity = 0;
        _markerVisible = false;
    }

    private void ReleaseTabDragCapture()
    {
        if (System.Windows.Input.Mouse.Captured == this)
            System.Windows.Input.Mouse.Capture(null);
    }
}
