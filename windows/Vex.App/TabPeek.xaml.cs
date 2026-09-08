using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vex.App.Model;

namespace Vex.App;

public sealed partial class TabPeek : OverlayControl
{
    private Project? _project;

    public TabPeek()
    {
        InitializeComponent();
        HideOnWindowDeactivate();
    }

    protected override void HideCore() => Hide();

    public void Show(Project project)
    {
        _project = project;

        // Build previews once on open — O(panes), no terminal controls created.
        var previews = project.Tabs.Select(t => new TabPeekPreview(t)).ToList();
        TabList.ItemsSource = previews;

        // Pre-select the active tab so the user starts nearby.
        var activeIndex = project.SelectedTab is { } selected
            ? project.Tabs.IndexOf(selected) : -1;
        TabList.SelectedIndex = activeIndex >= 0 ? activeIndex : 0;

        Visibility = Visibility.Visible;
        OpenPopup(this);
        AnimateOverlayOpen(Backdrop, Panel, PanelScale, PanelTranslate);

        Dispatcher.BeginInvoke(() => TabList.Focus(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Hide() => HideWithAnimation(Backdrop, Panel);

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Hide();

    private void Panel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void TabList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(TabList, e.OriginalSource as DependencyObject)
            is ListBoxItem item)
        {
            TabList.SelectedItem = item.DataContext;
            ActivateSelected();
            Hide();
            e.Handled = true;
        }
    }

    private void TabList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Enter:
                ActivateSelected();
                Hide();
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var count = TabList.Items.Count;
        if (count == 0)
            return;
        var next = ((TabList.SelectedIndex + delta) % count + count) % count;
        TabList.SelectedIndex = next;
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection only moves the highlight card; Enter activates the tab.
    }

    private void ActivateSelected()
    {
        if (_project is null || TabList.SelectedItem is not TabPeekPreview preview)
            return;
        _project.SelectedTab = preview.Tab;
    }
}
