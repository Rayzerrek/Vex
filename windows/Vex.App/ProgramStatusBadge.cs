using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vex.App.Model;
using Vex.Libghostty;

namespace Vex.App;

/// <summary>Task state independent of application icons; only visible indeterminate work animates.</summary>
public sealed class ProgramStatusBadge : TextBlock
{
    private readonly RotateTransform _spin = new();
    internal bool IsSpinning => _spin.HasAnimatedProperties;

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status),
        typeof(ProgramStatusSummary), typeof(ProgramStatusBadge), new PropertyMetadata(ProgramStatusSummary.Empty, OnStatusChanged));
    public static readonly DependencyProperty OriginProperty = DependencyProperty.Register(nameof(Origin),
        typeof(string), typeof(ProgramStatusBadge), new PropertyMetadata("", OnStatusChanged));

    public ProgramStatusBadge()
    {
        Visibility = Visibility.Collapsed;
        Width = 12;
        FontSize = 12;
        FontWeight = FontWeights.SemiBold;
        TextAlignment = TextAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(ForegroundProperty, "VexTextDim");
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _spin;
        Loaded += (_, _) => UpdateWorkingAnimation();
        Unloaded += (_, _) => StopWorkingAnimation();
        IsVisibleChanged += (_, _) => UpdateWorkingAnimation();
    }

    public ProgramStatusSummary Status
    {
        get => (ProgramStatusSummary?)GetValue(StatusProperty) ?? ProgramStatusSummary.Empty;
        set => SetValue(StatusProperty, value);
    }

    /// <summary>Trusted terminal location is shown separately from program-supplied text.</summary>
    public string Origin
    {
        get => (string?)GetValue(OriginProperty) ?? "";
        set => SetValue(OriginProperty, value);
    }

    private static void OnStatusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var badge = (ProgramStatusBadge)sender;
        var status = badge.Status;
        badge.Text = ProgramStatusPresentation.StatusSymbol(status);
        badge.Visibility = badge.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var tooltip = ProgramStatusPresentation.StatusTooltip(status);
        badge.ToolTip = tooltip.Length == 0 ? null : badge.Origin.Length == 0 ? tooltip : badge.Origin + "\n" + tooltip;
        AutomationProperties.SetName(badge, badge.ToolTip as string ?? "");
        badge.SetResourceReference(ForegroundProperty, status.State switch
        {
            ProgramStatusState.Error => "VexPaneExited",
            ProgramStatusState.Blocked => "VexAccentPurple",
            ProgramStatusState.Working => "VexAccentBlue",
            _ => "VexTextDim",
        });
        badge.UpdateWorkingAnimation();
    }

    private void UpdateWorkingAnimation()
    {
        var animate = IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation
            && Status.State == ProgramStatusState.Working && Status.Progress is null;
        if (!animate) { StopWorkingAnimation(); return; }
        if (IsSpinning) return;
        _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    private void StopWorkingAnimation()
    {
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        _spin.Angle = 0;
    }
}
