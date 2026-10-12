using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vex.App.Model;

namespace Vex.App;

public sealed partial class MainWindow
{
    private CommandPalette? _paletteOverlay;
    private CommandPalette PaletteOverlay
    {
        get
        {
            if (_paletteOverlay is null)
            {
                _paletteOverlay = new CommandPalette
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetRow(_paletteOverlay, 0);
                Grid.SetRowSpan(_paletteOverlay, 2);
                Panel.SetZIndex(_paletteOverlay, 1000);
                _paletteOverlay.Hidden += FocusActivePane;
                MainGrid.Children.Add(_paletteOverlay);
            }
            return _paletteOverlay;
        }
    }

    private SettingsOverlay? _settingsOverlay;
    private SettingsOverlay SettingsOverlay
    {
        get
        {
            if (_settingsOverlay is null)
            {
                _settingsOverlay = new SettingsOverlay
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetRow(_settingsOverlay, 0);
                Grid.SetRowSpan(_settingsOverlay, 2);
                Panel.SetZIndex(_settingsOverlay, 1001);
                _settingsOverlay.Hidden += FocusActivePane;
                MainGrid.Children.Add(_settingsOverlay);
            }
            return _settingsOverlay;
        }
    }

    private ThemeSwitcher? _themeSwitcher;
    private ThemeSwitcher ThemeSwitcher
    {
        get
        {
            if (_themeSwitcher is null)
            {
                _themeSwitcher = new ThemeSwitcher
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetRow(_themeSwitcher, 0);
                Grid.SetRowSpan(_themeSwitcher, 2);
                Panel.SetZIndex(_themeSwitcher, 1002);
                _themeSwitcher.Hidden += FocusActivePane;
                MainGrid.Children.Add(_themeSwitcher);
            }
            return _themeSwitcher;
        }
    }

    private TabPeek? _tabPeek;
    private TabPeek TabPeek
    {
        get
        {
            if (_tabPeek is null)
            {
                _tabPeek = new TabPeek
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetRow(_tabPeek, 0);
                Grid.SetRowSpan(_tabPeek, 2);
                Panel.SetZIndex(_tabPeek, 1003);
                _tabPeek.Hidden += FocusActivePane;
                MainGrid.Children.Add(_tabPeek);
            }
            return _tabPeek;
        }
    }
    private TabCloseOverlay? _tabCloseOverlay;
    private TabCloseOverlay TabCloseOverlay
    {
        get
        {
            if (_tabCloseOverlay is null)
            {
                _tabCloseOverlay = new TabCloseOverlay
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetRow(_tabCloseOverlay, 0);
                Grid.SetRowSpan(_tabCloseOverlay, 2);
                Panel.SetZIndex(_tabCloseOverlay, 1004);
                _tabCloseOverlay.Hidden += FocusActivePane;
                MainGrid.Children.Add(_tabCloseOverlay);
            }
            return _tabCloseOverlay;
        }
    }


    /// <summary>Hands keyboard focus back to the active pane after an overlay
    /// closes, so typing goes straight to the terminal instead of stranding
    /// on the window. Dispatched after the popup's own focus restoration, and
    /// skipped when another overlay opened on top (palette → settings/theme)
    /// so the new overlay keeps its focus.</summary>
    private void FocusActivePane()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if ((_paletteOverlay?.Visibility == Visibility.Visible) ||
                (_settingsOverlay?.Visibility == Visibility.Visible) ||
                (_themeSwitcher?.Visibility == Visibility.Visible) ||
                (_tabPeek?.Visibility == Visibility.Visible) ||
                (_tabCloseOverlay?.Visibility == Visibility.Visible) ||
                (_wslProjectOverlay?.Visibility == Visibility.Visible) ||
                (_projectPickerPopup?.IsOpen == true) ||
                (NewTabShellButton.ContextMenu?.IsOpen == true) ||
                (ProjectPillButton.ContextMenu?.IsOpen == true))
                return;
            _workspace.SelectedProject?.SelectedTab?.ActiveLeaf?.Focus();
        }, DispatcherPriority.ContextIdle);
    }

    private void ShowCommandPalette()
    {
        var items = PaletteProvider.GetItems(_workspace, action =>
        {
            if (action == "CloseTab") CloseActivePaneOrTab();
            else if (action == "NewProject") NewProject_Click(this, new RoutedEventArgs());
            else if (action == "Settings") Settings_Click(this, new RoutedEventArgs());
            else if (action == "ThemePicker") ToggleThemeSwitcher();
            else if (action == "TabPeek") ToggleTabPeek();
            else if (action == "NextProject") CycleProject(1);
            else if (action == "PrevProject") CycleProject(-1);
            else if (action == "ProjectPicker") ToggleProjectPicker();
            else if (action == "SaveLayout") SaveCurrentLayout();
        }, OpenSavedLayout);
        PaletteOverlay.Show(items);
    }

    private void ToggleThemeSwitcher()
    {
        if (_themeSwitcher is { Visibility: Visibility.Visible })
            _themeSwitcher.Hide();
        else
            ThemeSwitcher.Show();
    }

    private void ToggleTabPeek()
    {
        if (_tabPeek is { Visibility: Visibility.Visible })
            _tabPeek.Hide();
        else if (_workspace.SelectedProject is { } project)
        {
            TabPeek.Show(project);
        }
    }
}
