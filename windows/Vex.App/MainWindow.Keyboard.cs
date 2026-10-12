using System.Windows;
using Vex.App.Model;

namespace Vex.App;

public sealed partial class MainWindow
{
    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var modifiers = System.Windows.Input.Keyboard.Modifiers;

        // Plain Escape closes popups and overlays while open, before the
        // terminal (or any focused control inside it) sees the key.
        if (key == System.Windows.Input.Key.Escape && modifiers == System.Windows.Input.ModifierKeys.None)
        {
            if (_tabClosePopup is { IsOpen: true })
            {
                TabClosePopup.IsOpen = false;
                _pendingBackgroundCloseTab = null;
                FocusActivePane();
                e.Handled = true;
                return;
            }

            if (_tabCloseOverlay is { Visibility: Visibility.Visible })
            {
                _tabCloseOverlay.Hide();
                e.Handled = true;
                return;
            }

            if (_settingsOverlay is { Visibility: Visibility.Visible })
            {
                _settingsOverlay.Hide();
                e.Handled = true;
                return;
            }
            if (_wslProjectOverlay is { Visibility: Visibility.Visible })
            {
                _wslProjectOverlay.Hide();
                e.Handled = true;
                return;
            }
            if (_savedLayoutOverlay is { Visibility: Visibility.Visible })
            {
                _savedLayoutOverlay.Hide();
                e.Handled = true;
                return;
            }
        }
        if (!TryGetWorkspaceShortcut(modifiers, key, out var shortcut, out var projectIndex))
            return;

        switch (shortcut)
        {
            case WorkspaceShortcut.CommandPalette:
                ShowCommandPalette();
                e.Handled = true;
                break;
            case WorkspaceShortcut.PreviousTab:
                CycleTab(-1);
                e.Handled = true;
                break;
            case WorkspaceShortcut.NextTab:
                CycleTab(1);
                e.Handled = true;
                break;
            case WorkspaceShortcut.SplitRight:
                _workspace.SelectedProject?.SelectedTab?.Split(System.Windows.Controls.Orientation.Horizontal);
                e.Handled = true;
                break;
            case WorkspaceShortcut.SplitDown:
                _workspace.SelectedProject?.SelectedTab?.Split(System.Windows.Controls.Orientation.Vertical);
                e.Handled = true;
                break;
            case WorkspaceShortcut.NewTab:
                _workspace.SelectedProject?.NewTab();
                e.Handled = true;
                break;
            case WorkspaceShortcut.ClosePaneOrTab:
                CloseActivePaneOrTab();
                e.Handled = true;
                break;
            case WorkspaceShortcut.ThemeSwitcher:
                ToggleThemeSwitcher();
                e.Handled = true;
                break;
            case WorkspaceShortcut.TabPeek:
                ToggleTabPeek();
                e.Handled = true;
                break;
            case WorkspaceShortcut.SwitchProject:
                SwitchToProject(projectIndex);
                e.Handled = true;
                break;
            case WorkspaceShortcut.NextProject:
                CycleProject(1);
                e.Handled = true;
                break;
            case WorkspaceShortcut.PreviousProject:
                CycleProject(-1);
                e.Handled = true;
                break;
            case WorkspaceShortcut.ProjectPicker:
                ToggleProjectPicker();
                e.Handled = true;
                break;
        }
    }

    private void CloseActivePaneOrTab()
    {
        if (_workspace.SelectedProject?.SelectedTab is not { } tab)
            return;

        if (tab.PaneCount > 1 && tab.ActiveLeaf is { } activeLeaf)
        {
            var info = TabCloseConfirmation.GetCloseInfo(activeLeaf);
            if (info.NeedsConfirmation)
            {
                TabCloseOverlay.Show(info, () => activeLeaf.Close());
            }
            else
            {
                activeLeaf.Close();
            }
        }
        else
        {
            RequestCloseTab(tab, null);
        }
    }
    private void CycleTab(int direction)
    {
        if (_workspace.SelectedProject is { } project && project.Tabs.Count > 1 && project.SelectedTab is { } currentTab)
        {
            var index = project.Tabs.IndexOf(currentTab);
            if (index >= 0)
            {
                var nextIndex = (index + direction) % project.Tabs.Count;
                if (nextIndex < 0) nextIndex += project.Tabs.Count;
                project.SelectedTab = project.Tabs[nextIndex];
            }
        }
    }

    private void SwitchToProject(int index)
    {
        if (_workspace.SelectProjectByIndex(index))
        {
            if (_projectPickerPopup is { } popup)
                popup.IsOpen = false;
            _tabPeek?.Hide();
            _paletteOverlay?.Hide();
            _settingsOverlay?.Hide();
            _themeSwitcher?.Hide();
            FocusActivePane();
        }
    }

    private void CycleProject(int direction)
    {
        if (_workspace.CycleProject(direction))
        {
            if (_projectPickerPopup is { } popup)
                popup.IsOpen = false;
            _tabPeek?.Hide();
            _paletteOverlay?.Hide();
            _settingsOverlay?.Hide();
            _themeSwitcher?.Hide();
            FocusActivePane();
        }
    }

    private void ToggleProjectPicker()
    {
        if (ProjectPickerPopup.IsOpen)
        {
            ProjectPickerPopup.IsOpen = false;
        }
        else
        {
            _paletteOverlay?.Hide();
            _settingsOverlay?.Hide();
            _themeSwitcher?.Hide();
            _tabPeek?.Hide();
            ProjectPickerPopup.IsOpen = true;
        }
    }

    internal static bool TryGetProjectIndex(System.Windows.Input.Key key, out int index)
    {
        if (key >= System.Windows.Input.Key.D1 && key <= System.Windows.Input.Key.D9)
        {
            index = key - System.Windows.Input.Key.D1;
            return true;
        }
        if (key == System.Windows.Input.Key.D0)
        {
            index = 9;
            return true;
        }
        if (key >= System.Windows.Input.Key.NumPad1 && key <= System.Windows.Input.Key.NumPad9)
        {
            index = key - System.Windows.Input.Key.NumPad1;
            return true;
        }
        if (key == System.Windows.Input.Key.NumPad0)
        {
            index = 9;
            return true;
        }

        index = -1;
        return false;
    }

    internal static bool TryGetWorkspaceShortcut(
        System.Windows.Input.ModifierKeys modifiers,
        System.Windows.Input.Key key,
        out WorkspaceShortcut shortcut,
        out int projectIndex)
    {
        projectIndex = -1;

        if (modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
        {
            if (key == System.Windows.Input.Key.P) { shortcut = WorkspaceShortcut.CommandPalette; return true; }
            if (key == System.Windows.Input.Key.Tab) { shortcut = WorkspaceShortcut.PreviousTab; return true; }
            if (key == System.Windows.Input.Key.R) { shortcut = WorkspaceShortcut.SplitRight; return true; }
            if (key == System.Windows.Input.Key.D) { shortcut = WorkspaceShortcut.SplitDown; return true; }
            if (key == System.Windows.Input.Key.T) { shortcut = WorkspaceShortcut.NewTab; return true; }
            if (key == System.Windows.Input.Key.W) { shortcut = WorkspaceShortcut.ClosePaneOrTab; return true; }
            if (key == System.Windows.Input.Key.M) { shortcut = WorkspaceShortcut.ThemeSwitcher; return true; }
            if (key == System.Windows.Input.Key.Space) { shortcut = WorkspaceShortcut.TabPeek; return true; }
            if (key == System.Windows.Input.Key.PageDown) { shortcut = WorkspaceShortcut.NextProject; return true; }
            if (key == System.Windows.Input.Key.PageUp) { shortcut = WorkspaceShortcut.PreviousProject; return true; }
            if (key == System.Windows.Input.Key.O) { shortcut = WorkspaceShortcut.ProjectPicker; return true; }
            if (TryGetProjectIndex(key, out projectIndex)) { shortcut = WorkspaceShortcut.SwitchProject; return true; }
        }
        else if (modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            if (key == System.Windows.Input.Key.Tab) { shortcut = WorkspaceShortcut.NextTab; return true; }
        }

        shortcut = default;
        return false;
    }
}
