using System.Windows;
using System.Windows.Controls;
using Vex.App.Model;

namespace Vex.App;

public sealed partial class MainWindow
{
    private void TitleEditBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitTabRename(sender);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            CancelTabRename(sender);
            e.Handled = true;
        }
    }

    private void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitTabRename(sender);
    }

    private void CommitTabRename(object sender)
    {
        if (sender is System.Windows.Controls.TextBox textBox && textBox.DataContext is WorkspaceTab tab)
        {
            var panel = (Grid)textBox.Parent;
            var textBlock = (TextBlock)panel.FindName("TitleBlock");
            if (textBlock != null)
            {
                var newTitle = textBox.Text.Trim();
                if (!string.IsNullOrEmpty(newTitle) && newTitle != tab.Title)
                {
                    tab.Title = newTitle;
                    tab.HasCustomTitle = true;
                }

                textBox.Visibility = Visibility.Collapsed;
                textBlock.Visibility = Visibility.Visible;
            }
        }
    }

    private void CancelTabRename(object sender)
    {
        if (sender is System.Windows.Controls.TextBox textBox && textBox.DataContext is WorkspaceTab tab)
        {
            var panel = (Grid)textBox.Parent;
            var textBlock = (TextBlock)panel.FindName("TitleBlock");
            if (textBlock != null)
            {
                textBox.Text = tab.Title; // revert
                textBox.Visibility = Visibility.Collapsed;
                textBlock.Visibility = Visibility.Visible;
            }
        }
    }

    private WorkspaceTab? _pendingBackgroundCloseTab;

    private void RequestCloseTab(WorkspaceTab tab, FrameworkElement? anchorElement)
    {
        if (_workspace.SelectedProject is not { } project)
            return;

        if (project.Tabs.Count <= 1)
            return;

        var info = TabCloseConfirmation.GetCloseInfo(tab);
        if (!info.NeedsConfirmation)
        {
            project.CloseTab(tab);
            return;
        }

        var isActive = ReferenceEquals(tab, project.SelectedTab);
        if (isActive)
        {
            TabCloseOverlay.Show(info, () => project.CloseTab(tab));
        }
        else
        {
            _pendingBackgroundCloseTab = tab;
            TabClosePopupTitle.Text = info.Title;
            TabClosePopupMessage.Text = info.Message;
            TabClosePopupConfirmBtn.Content = info.IsAgent ? "Terminate" : (info.IsDirty ? "Discard" : "Close Tab");

            if (info.Icon?.Image is { } drawing)
            {
                TabClosePopupIcon.Source = drawing;
                TabClosePopupIcon.Visibility = Visibility.Visible;
                TabClosePopupWarning.Visibility = Visibility.Collapsed;
            }
            else
            {
                TabClosePopupIcon.Visibility = Visibility.Collapsed;
                TabClosePopupWarning.Visibility = Visibility.Visible;
            }

            TabClosePopup.PlacementTarget = anchorElement;
            TabClosePopup.IsOpen = true;
        }
    }

    private void TabClosePopupCancel_Click(object sender, RoutedEventArgs e)
    {
        TabClosePopup.IsOpen = false;
        _pendingBackgroundCloseTab = null;
    }

    private void TabClosePopupConfirm_Click(object sender, RoutedEventArgs e)
    {
        TabClosePopup.IsOpen = false;
        if (_pendingBackgroundCloseTab is { } tab && _workspace.SelectedProject is { } project)
        {
            project.CloseTab(tab);
        }
        _pendingBackgroundCloseTab = null;
    }

    private void TabClosePopup_Closed(object? sender, EventArgs e)
    {
        _pendingBackgroundCloseTab = null;
    }

    private void TabClosePopup_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            TabClosePopup.IsOpen = false;
            _pendingBackgroundCloseTab = null;
            FocusActivePane();
            e.Handled = true;
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement anchor && anchor.DataContext is WorkspaceTab tab)
            RequestCloseTab(tab, anchor);
        e.Handled = true;
    }
}
