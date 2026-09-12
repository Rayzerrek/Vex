using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vex.App.Model;

namespace Vex.App;

public sealed partial class CommandPalette : OverlayControl
{
    private List<PaletteItem> _allItems = new();

    /// <summary>Raised after the palette closed (including Escape and backdrop
    /// clicks), so the window can hand keyboard focus back to the terminal.</summary>
    public event Action? Hidden;

    public CommandPalette()
    {
        InitializeComponent();
        HideOnWindowDeactivate();
    }

    protected override void HideCore() => Hide();

    public void Show(IEnumerable<PaletteItem> items)
    {
        _allItems = items.ToList();
        Visibility = Visibility.Visible;
        OpenPopup(this);
        SearchBox.Text = "";
        UpdateFilter();
        AnimateOpen();

        // Use Dispatcher to focus after the popup opens
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Hide() => HideWithAnimation(Backdrop, Panel, () => Hidden?.Invoke());

    private void AnimateOpen() => AnimateOverlayOpen(Backdrop, Panel, PanelScale, PanelTranslate);

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Hide();
    }

    private void Border_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateFilter();
    }

    private void UpdateFilter()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            ResultList.ItemsSource = _allItems;
        }
        else
        {
            ResultList.ItemsSource = _allItems
                .Where(x => x.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || 
                            (x.Subtitle != null && x.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        EmptyHint.Visibility = ResultList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (ResultList.Items.Count > 0)
        {
            ResultList.SelectedIndex = 0;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (ResultList.SelectedIndex < ResultList.Items.Count - 1)
            {
                ResultList.SelectedIndex++;
                ResultList.ScrollIntoView(ResultList.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (ResultList.SelectedIndex > 0)
            {
                ResultList.SelectedIndex--;
                ResultList.ScrollIntoView(ResultList.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ExecuteSelected();
            e.Handled = true;
        }
    }

    private void ResultList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Single click activates a command (VS Code palette behavior); the
        // old double-click requirement hid the primary interaction.
        if (ItemsControl.ContainerFromElement(ResultList, e.OriginalSource as DependencyObject)
            is ListBoxItem { } container)
        {
            container.IsSelected = true;
            ExecuteSelected();
            e.Handled = true;
        }
    }

    private void ExecuteSelected()
    {
        if (ResultList.SelectedItem is PaletteItem item)
        {
            Hide();
            item.Action?.Invoke();
        }
    }
}
