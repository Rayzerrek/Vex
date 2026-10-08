using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Vex.App.Model;
using Vex.Libghostty;

namespace Vex.App;

/// <summary>A static program status glyph; hidden work never creates animation clocks or polling timers.</summary>
public sealed class ProgramStatusBadge : TextBlock
{
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
            _ => "VexTextDim",
        });
    }
}
