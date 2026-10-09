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
    private enum OverlayAnimationState { Hidden, Visible, Closing }

    private OverlayAnimationState _animationState;
    private long _animationVersion;

    protected OverlayControl()
    {
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(this, true);
    }

    private static DoubleAnimation Anim(double from, double to, Duration duration) =>
        new(from, to, duration)
        {
            EasingFunction = UiMotion.MovementEasing,
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

    protected void AnimateOverlayOpen(FrameworkElement backdrop, FrameworkElement panel,
        ScaleTransform scale, TranslateTransform translate, Action? onOpened = null)
    {
        if (_animationState == OverlayAnimationState.Hidden)
        {
            backdrop.BeginAnimation(OpacityProperty, null);
            panel.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            backdrop.Opacity = panel.Opacity = 0;
            scale.ScaleX = scale.ScaleY = 0.96;
            translate.Y = 8;
        }

        _animationState = OverlayAnimationState.Visible;
        var version = ++_animationVersion;
        var motion = Anim(translate.Y, 0, UiMotion.OverlayOpenDuration);
        motion.Completed += (_, _) =>
        {
            if (_animationVersion == version && _animationState == OverlayAnimationState.Visible)
                onOpened?.Invoke();
        };
        backdrop.BeginAnimation(OpacityProperty, Anim(backdrop.Opacity, 1, UiMotion.OverlayBackdropDuration));
        panel.BeginAnimation(OpacityProperty, Anim(panel.Opacity, 1, UiMotion.OverlayFadeDuration));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(scale.ScaleX, 1, UiMotion.OverlayOpenDuration));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(scale.ScaleY, 1, UiMotion.OverlayOpenDuration));
        translate.BeginAnimation(TranslateTransform.YProperty, motion);
    }

    /// <summary>Fade-out + collapse + popup close, the standard dismissal for
    /// the quick overlays; no-op when already hidden. <paramref name="onHidden"/>
    /// runs after the popup closed, so callers can restore focus.</summary>
    protected void HideWithAnimation(FrameworkElement backdrop, FrameworkElement panel, Action? onHidden = null)
    {
        if (Visibility != Visibility.Visible || _animationState == OverlayAnimationState.Closing)
            return;

        _animationState = OverlayAnimationState.Closing;
        var version = ++_animationVersion;
        var fade = Anim(panel.Opacity, 0, UiMotion.OverlayCloseDuration);
        fade.Completed += (_, _) =>
        {
            // Reopening replaces the visuals, but an older clock can still
            // deliver its completion and must not close the new interaction.
            if (_animationVersion != version || _animationState != OverlayAnimationState.Closing)
                return;
            _animationState = OverlayAnimationState.Hidden;
            Visibility = Visibility.Collapsed;
            ClosePopup(this);
            onHidden?.Invoke();
        };
        backdrop.BeginAnimation(OpacityProperty, Anim(backdrop.Opacity, 0, UiMotion.OverlayCloseDuration));
        panel.BeginAnimation(OpacityProperty, fade);
    }
}
