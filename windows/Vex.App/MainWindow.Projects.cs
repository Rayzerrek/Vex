using System.Windows;
using System.Windows.Controls;
using Vex.App.Model;
using Microsoft.Win32;

namespace Vex.App;

public sealed partial class MainWindow
{
    private WslProjectOverlay? _wslProjectOverlay;

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(StyleProperty, "DarkContextMenu");
        menu.Items.Add(ShellMenuItem("Add WSL project…", () => ShowWslProjectPicker()));
        menu.Items.Add(ShellMenuItem("Add Windows project…", AddWindowsProject));
        menu.PlacementTarget = ProjectPillButton;
        menu.IsOpen = true;
    }


    private void AddWindowsProject()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a Windows project directory" };
        if (dialog.ShowDialog(this) != true) return;
        var shellId = AppSettings.Instance.ShellId;
        if (ShellRegistry.IsWsl(shellId)) shellId = ShellRegistry.HasDetected("pwsh") ? "pwsh" : ShellRegistry.SystemDefaultId;
        _workspace.NewProject(dialog.FolderName, shellId);
    }

    private void ShowWslProjectPicker(string? error = null)
    {
        if (_wslProjectOverlay is null)
        {
            _wslProjectOverlay = new WslProjectOverlay();
            Grid.SetRowSpan(_wslProjectOverlay, 2);
            Panel.SetZIndex(_wslProjectOverlay, 1002);
            _wslProjectOverlay.Hidden += FocusActivePane;
            _wslProjectOverlay.ProjectOpened += (directory, shellId) => _workspace.NewProject(directory, shellId);
            MainGrid.Children.Add(_wslProjectOverlay);
        }
        _wslProjectOverlay.Show(error);
    }

    private void ProjectPickerAddWsl_Click(object sender, RoutedEventArgs e)
    {
        ProjectPickerPopup.IsOpen = false;
        ShowWslProjectPicker();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Toggle();
    }

    private void ProjectPillButton_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _projectPickerClosedTimestamp < 200)
            return;

        ProjectPickerPopup.IsOpen = !ProjectPickerPopup.IsOpen;
    }

    private void ProjectPickerPopup_Opened(object? sender, EventArgs e)
    {
        _workspace.SelectedProject?.RefreshGitBranch();
        ProjectPickerList.Focus();
        if (ProjectPickerList.SelectedItem is { } item)
            ProjectPickerList.ScrollIntoView(item);
    }

    private void ProjectPickerPopup_Closed(object? sender, EventArgs e)
    {
        _projectPickerClosedTimestamp = Environment.TickCount64;
        FocusActivePane();
    }

    private void ProjectPickerList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is { Name: "RemoveBtn" })
            return;

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: Project project })
        {
            _workspace.SelectedProject = project;
            ProjectPickerPopup.IsOpen = false;
            FocusActivePane();
        }
    }

    private void ProjectPicker_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            ProjectPickerPopup.IsOpen = false;
            FocusActivePane();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.FocusedElement is not Button && ProjectPickerList.SelectedItem is Project project)
        {
            _workspace.SelectedProject = project;
            ProjectPickerPopup.IsOpen = false;
            FocusActivePane();
            e.Handled = true;
        }
    }

    private void ProjectPickerAddFolder_Click(object sender, RoutedEventArgs e)
    {
        ProjectPickerPopup.IsOpen = false;
        AddWindowsProject();
    }
}
