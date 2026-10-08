using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vex.App.Model;

namespace Vex.App;

public sealed partial class MainWindow
{
    private SavedLayoutOverlay? _savedLayoutOverlay;
    private SavedLayoutOverlay SavedLayoutOverlay
    {
        get
        {
            if (_savedLayoutOverlay is null)
            {
                _savedLayoutOverlay = new SavedLayoutOverlay();
                Grid.SetRowSpan(_savedLayoutOverlay, 2);
                Panel.SetZIndex(_savedLayoutOverlay, 1002);
                _savedLayoutOverlay.Hidden += FocusActivePane;
                MainGrid.Children.Add(_savedLayoutOverlay);
            }
            return _savedLayoutOverlay;
        }
    }

    private void SaveCurrentLayout(WorkspaceTab? tab = null)
    {
        if (_workspace.SelectedProject is not { } project) return;
        tab ??= project.SelectedTab;
        if (tab is not null) SavedLayoutOverlay.Show(SavedTerminalLayouts.CaptureLayout(tab, project.WorkingDirectory));
    }

    private void OpenSavedLayout(SavedTerminalLayout layout)
    {
        try { _workspace.SelectedProject?.OpenSavedLayout(layout); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            SavedLayoutOverlay.Show(layout, editing: true, error: error.Message);
        }
    }

    private void TabMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var layoutsMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "SavedLayouts"));
        layoutsMenu.Items.Clear();
        var layouts = AppSettings.Instance.SavedLayouts;
        var style = (Style)FindResource("DarkMenuItem");
        foreach (var layout in layouts)
        {
            var item = new MenuItem { Header = layout.Name, Style = style, IsEnabled = _workspace.SelectedProject?.CanCreateTab == true };
            item.Click += (_, _) => OpenSavedLayout(layout);
            layoutsMenu.Items.Add(item);
        }
        layoutsMenu.IsEnabled = layouts.Count > 0;
        if (layouts.Count == 0) return;
        layoutsMenu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "Edit layout…", Style = style };
        foreach (var layout in layouts)
        {
            var item = new MenuItem { Header = layout.Name, Style = style };
            item.Click += (_, _) => SavedLayoutOverlay.Show(layout, editing: true);
            edit.Items.Add(item);
        }
        layoutsMenu.Items.Add(edit);
    }

    private void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        var menu = ItemsControl.ItemsControlFromItemContainer((MenuItem)sender) as ContextMenu;
        SaveCurrentLayout((menu?.PlacementTarget as FrameworkElement)?.DataContext as WorkspaceTab);
    }
}
