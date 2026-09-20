using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Vex.App;

/// <summary>
/// Shared plumbing for the popup-hosted overlays (command palette, theme
/// switcher, tab peek): popup open/close, optional hide-on-deactivate, and
/// the standard open/close animation curves. Settings opts out of
/// hide-on-deactivate so it survives Alt+Tab and file dialogs.
/// </summary>
public abstract class OverlayControl : UserControl
{
    protected OverlayControl()
    {
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(this, true);
    }

    protected static DoubleAnimation Anim(double from, double to, double ms) =>
        new(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
    protected static void OpenPopup(FrameworkElement element)
    {
        if (element.Parent is Popup popup)
            popup.IsOpen = true;
    }

    protected static void ClosePopup(FrameworkElement element)
    {
        if (element.Parent is Popup popup)
            popup.IsOpen = false;
    }

    /// <summary>Dismisses the overlay when the window loses activation, like a
    /// context menu; keyboard focus can then never be stranded in it.</summary>
    protected void HideOnWindowDeactivate()
    {
        var subscribed = false;
        void Subscribe(Window window)
        {
            if (subscribed)
                return;
            subscribed = true;
            window.Deactivated += (_, _) => HideCore();
        }

        if (Window.GetWindow(this) is { } currentWindow)
            Subscribe(currentWindow);

        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window)
                Subscribe(window);
        };
    }
    protected abstract void HideCore();

    protected static void AnimateOverlayOpen(FrameworkElement backdrop, FrameworkElement panel,
        ScaleTransform scale, TranslateTransform translate)
    {
        backdrop.BeginAnimation(OpacityProperty, Anim(0, 1, 110));
        panel.BeginAnimation(OpacityProperty, Anim(0, 1, 150));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.96, 1, 170));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.96, 1, 170));
        translate.BeginAnimation(TranslateTransform.YProperty, Anim(8, 0, 170));
    }

    protected static void AnimateOverlayClose(FrameworkElement backdrop, FrameworkElement panel, Action onClosed)
    {
        var fade = Anim(1, 0, 90);
        fade.Completed += (_, _) => onClosed();
        backdrop.BeginAnimation(OpacityProperty, Anim(1, 0, 90));
        panel.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Fade-out + collapse + popup close, the standard dismissal for
    /// the quick overlays; no-op when already hidden. <paramref name="onHidden"/>
    /// runs after the popup closed, so callers can restore focus.</summary>
    protected void HideWithAnimation(FrameworkElement backdrop, FrameworkElement panel, Action? onHidden = null)
    {
        if (Visibility != Visibility.Visible)
            return;
        AnimateOverlayClose(backdrop, panel, () =>
        {
            Visibility = Visibility.Collapsed;
            ClosePopup(this);
            onHidden?.Invoke();
        });
    }
}
