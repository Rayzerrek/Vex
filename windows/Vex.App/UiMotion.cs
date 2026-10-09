using System.Windows;
using System.Windows.Media.Animation;

namespace Vex.App;

/// <summary>Shared motion timings and curves for equivalent actions in the workspace UI.</summary>
public static class UiMotion
{
    /// <summary>Hover feedback on buttons, cards, and contextual actions.</summary>
    public static Duration FeedbackDuration { get; } = new(TimeSpan.FromMilliseconds(120));

    /// <summary>Movement of toggle thumbs and tab reorder previews.</summary>
    public static Duration RepositionDuration { get; } = new(TimeSpan.FromMilliseconds(180));

    /// <summary>Backdrop appearance when opening an overlay.</summary>
    public static Duration OverlayBackdropDuration { get; } = new(TimeSpan.FromMilliseconds(110));

    /// <summary>Content appearance when opening an overlay.</summary>
    public static Duration OverlayFadeDuration { get; } = new(TimeSpan.FromMilliseconds(150));

    /// <summary>Scale and translation when opening an overlay.</summary>
    public static Duration OverlayOpenDuration { get; } = new(TimeSpan.FromMilliseconds(170));

    /// <summary>Dismissal of an overlay before returning focus to the workspace.</summary>
    public static Duration OverlayCloseDuration { get; } = new(TimeSpan.FromMilliseconds(90));

    /// <summary>Fade between settings pages.</summary>
    public static Duration PageTransitionDuration { get; } = new(TimeSpan.FromMilliseconds(130));

    /// <summary>Shared hover curve, frozen for use across WPF templates.</summary>
    public static QuadraticEase FeedbackEasing { get; } = CreateFeedbackEasing();

    /// <summary>Shared movement curve, frozen for use across WPF templates.</summary>
    public static CubicEase MovementEasing { get; } = CreateMovementEasing();

    private static QuadraticEase CreateFeedbackEasing()
    {
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        easing.Freeze();
        return easing;
    }

    private static CubicEase CreateMovementEasing()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        easing.Freeze();
        return easing;
    }
}
