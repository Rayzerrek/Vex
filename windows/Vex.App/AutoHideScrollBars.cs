using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Vex.App;

/// <summary>
/// Overlay auto-hide scrollbars: the thumb stays invisible until the user
/// scrolls or hovers the bar, then fades out after a short idle. When the
/// viewport no longer needs scrolling the bar fades away instead of snapping
/// off. Attach via <see cref="EnabledProperty"/> on a ScrollViewer.
/// </summary>
public static class AutoHideScrollBars
{
    private const double FadeInMs = 120;
    private const double FadeOutMs = 220;
    private const double HideDelayMs = 900;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AutoHideScrollBars),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(ScrollViewer element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(ScrollViewer element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
            return;

        if (e.OldValue is true)
            Detach(viewer);

        if (e.NewValue is true)
            Attach(viewer);
    }

    private static void Attach(ScrollViewer viewer)
    {
        var state = new ScrollBarAutoHideState(viewer);
        viewer.SetValue(StateProperty, state);
        if (viewer.IsLoaded)
            state.Hook();
        else
            viewer.Loaded += OnViewerLoaded;
        viewer.Unloaded += OnViewerUnloaded;
    }

    private static void Detach(ScrollViewer viewer)
    {
        viewer.Loaded -= OnViewerLoaded;
        viewer.Unloaded -= OnViewerUnloaded;
        if (viewer.GetValue(StateProperty) is ScrollBarAutoHideState state)
        {
            state.Unhook();
            viewer.ClearValue(StateProperty);
        }
    }

    private static void OnViewerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer && viewer.GetValue(StateProperty) is ScrollBarAutoHideState state)
            state.Hook();
    }

    private static void OnViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer && viewer.GetValue(StateProperty) is ScrollBarAutoHideState state)
            state.Unhook();
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(ScrollBarAutoHideState), typeof(AutoHideScrollBars));

    private sealed class ScrollBarAutoHideState
    {
        private readonly ScrollViewer _viewer;
        private readonly DispatcherTimer _hideTimer;
        private ScrollBar? _vertical;
        private ScrollBar? _horizontal;
        private bool _hooked;
        private bool _pointerOverBar;

        public ScrollBarAutoHideState(ScrollViewer viewer)
        {
            _viewer = viewer;
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideDelayMs) };
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer.Stop();
                if (!_pointerOverBar)
                    FadeTo(0, FadeOutMs);
            };
        }

        public void Hook()
        {
            if (_hooked)
                return;
            _hooked = true;

            _viewer.ScrollChanged += OnScrollChanged;
            _viewer.LayoutUpdated += OnLayoutUpdated;
            EnsureBars();
            SyncNeeded();
        }

        public void Unhook()
        {
            if (!_hooked)
                return;
            _hooked = false;
            _hideTimer.Stop();
            _viewer.ScrollChanged -= OnScrollChanged;
            _viewer.LayoutUpdated -= OnLayoutUpdated;
            UnwireBar(_vertical);
            UnwireBar(_horizontal);
            _vertical = null;
            _horizontal = null;
        }

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (_vertical is not null || _horizontal is not null)
            {
                _viewer.LayoutUpdated -= OnLayoutUpdated;
                return;
            }
            EnsureBars();
            if (_vertical is not null || _horizontal is not null)
                _viewer.LayoutUpdated -= OnLayoutUpdated;
        }

        private void WireBar(ScrollBar? bar)
        {
            if (bar is null)
                return;
            bar.MouseEnter += OnBarMouseEnter;
            bar.MouseLeave += OnBarMouseLeave;
            bar.PreviewMouseDown += OnBarMouseDown;
        }

        private void UnwireBar(ScrollBar? bar)
        {
            if (bar is null)
                return;
            bar.MouseEnter -= OnBarMouseEnter;
            bar.MouseLeave -= OnBarMouseLeave;
            bar.PreviewMouseDown -= OnBarMouseDown;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            EnsureBars();
            SyncNeeded();

            // Extent-only changes (content grew/shrank) should not flash the
            // bar; only real viewport motion counts as "using" it.
            if (e.VerticalChange == 0 && e.HorizontalChange == 0)
                return;

            Pulse();
        }

        private void EnsureBars()
        {
            if (_vertical is not null || _horizontal is not null)
                return;

            _vertical = FindScrollBar(_viewer, Orientation.Vertical);
            _horizontal = FindScrollBar(_viewer, Orientation.Horizontal);
            WireBar(_vertical);
            WireBar(_horizontal);
            SetOpacity(_vertical, 0);
            SetOpacity(_horizontal, 0);
        }

        private void OnBarMouseEnter(object sender, MouseEventArgs e)
        {
            if (!IsBarNeeded(sender as ScrollBar))
                return;
            _pointerOverBar = true;
            _hideTimer.Stop();
            FadeTo(1, FadeInMs);
        }

        private void OnBarMouseLeave(object sender, MouseEventArgs e)
        {
            _pointerOverBar = false;
            ScheduleHide();
        }

        private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsBarNeeded(sender as ScrollBar))
                return;
            _hideTimer.Stop();
            FadeTo(1, FadeInMs);
        }

        private void Pulse()
        {
            if (!AnyBarNeeded())
                return;
            FadeTo(1, FadeInMs);
            if (!_pointerOverBar)
                ScheduleHide();
        }

        private void ScheduleHide()
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void SyncNeeded()
        {
            // When scrolling is no longer possible, fade out even if the
            // pointer is still over the (now empty) track.
            if (!AnyBarNeeded())
            {
                _hideTimer.Stop();
                FadeTo(0, FadeOutMs);
            }
        }

        private bool AnyBarNeeded()
            => IsBarNeeded(_vertical) || IsBarNeeded(_horizontal);

        private bool IsBarNeeded(ScrollBar? bar)
        {
            if (bar is null || bar.Visibility != Visibility.Visible)
                return false;
            return bar.Orientation == Orientation.Vertical
                ? _viewer.ScrollableHeight > 0.5
                : _viewer.ScrollableWidth > 0.5;
        }

        private void FadeTo(double opacity, double ms)
        {
            AnimateOpacity(_vertical, opacity, ms);
            AnimateOpacity(_horizontal, opacity, ms);
        }

        private static void SetOpacity(ScrollBar? bar, double opacity)
        {
            if (bar is null)
                return;
            bar.BeginAnimation(UIElement.OpacityProperty, null);
            bar.Opacity = opacity;
        }

        private static void AnimateOpacity(ScrollBar? bar, double to, double ms)
        {
            if (bar is null)
                return;

            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
            anim.Completed += (_, _) =>
            {
                bar.BeginAnimation(UIElement.OpacityProperty, null);
                bar.Opacity = to;
            };
            bar.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private static ScrollBar? FindScrollBar(DependencyObject root, Orientation orientation)
        {
            var count = VisualTreeHelperCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is ScrollBar bar && bar.Orientation == orientation)
                    return bar;
                var nested = FindScrollBar(child, orientation);
                if (nested is not null)
                    return nested;
            }
            return null;
        }

        private static int VisualTreeHelperCount(DependencyObject root)
        {
            try
            {
                return System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            }
            catch
            {
                return 0;
            }
        }
    }
}
