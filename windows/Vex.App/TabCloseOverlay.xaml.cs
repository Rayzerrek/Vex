using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Vex.App.Model;

namespace Vex.App;

public sealed class WorkspaceExitDisplayItem
{
    public string AppName { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public ImageSource? IconImage { get; init; }
    public Visibility IconVisibility => IconImage != null ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class TabCloseOverlay : OverlayControl
{
    private Action? _onConfirm;

    /// <summary>Raised after the overlay closed, so keyboard focus can return to the active terminal.</summary>
    public event Action? Hidden;

    public TabCloseOverlay()
    {
        InitializeComponent();
        HideOnWindowDeactivate();
        PreviewKeyDown += TabCloseOverlay_PreviewKeyDown;
    }

    protected override void HideCore() => Hide();

    public void Show(TabCloseInfo info, Action onConfirm)
    {
        _onConfirm = onConfirm;

        SingleHeaderGrid.Visibility = Visibility.Visible;
        ExitListPanel.Visibility = Visibility.Collapsed;

        TitleText.Text = info.Title;
        MessageText.Text = info.Message;
        ConfirmButton.Content = info.IsAgent ? "Terminate" : (info.IsDirty ? "Discard & Close" : "Close Tab");

        if (info.Icon?.Image is { } drawing)
        {
            IconImage.Source = drawing;
            IconImage.Visibility = Visibility.Visible;
            WarningIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            IconImage.Visibility = Visibility.Collapsed;
            WarningIcon.Visibility = Visibility.Visible;
        }

        Visibility = Visibility.Visible;
        OpenPopup(this);
        AnimateOverlayOpen(Backdrop, Panel, PanelScale, PanelTranslate);

        Dispatcher.BeginInvoke(() => ConfirmButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void ShowExit(WorkspaceExitInfo exitInfo, Action onConfirm)
    {
        _onConfirm = onConfirm;

        SingleHeaderGrid.Visibility = Visibility.Collapsed;
        ExitListPanel.Visibility = Visibility.Visible;
        ExitSummaryText.Text = $"{exitInfo.Items.Count} active application{(exitInfo.Items.Count > 1 ? "s" : "")} will be terminated:";

        var displayItems = new List<WorkspaceExitDisplayItem>(exitInfo.Items.Count);
        foreach (var item in exitInfo.Items)
        {
            displayItems.Add(new WorkspaceExitDisplayItem
            {
                AppName = item.AppName,
                ProjectName = item.ProjectName,
                IconImage = item.Icon?.Image
            });
        }

        ExitItemsList.ItemsSource = displayItems;
        ConfirmButton.Content = "Exit Vex";

        Visibility = Visibility.Visible;
        OpenPopup(this);
        AnimateOverlayOpen(Backdrop, Panel, PanelScale, PanelTranslate);

        Dispatcher.BeginInvoke(() => ConfirmButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Hide() => HideWithAnimation(Backdrop, Panel, () => Hidden?.Invoke());

    private void TabCloseOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Confirm_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Hide();
    private void Panel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var action = _onConfirm;
        Hide();
        action?.Invoke();
    }
}
