using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kero.App.Model;

namespace Kero.App;

public partial class CommandPalette : UserControl
{
    private List<PaletteItem> _allItems = new();

    public CommandPalette()
    {
        InitializeComponent();
    }

    public void Show(IEnumerable<PaletteItem> items)
    {
        _allItems = items.ToList();
        Visibility = Visibility.Visible;
        SearchBox.Text = "";
        UpdateFilter();
        SearchBox.Focus();
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
    }

    private void UserControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Visibility == Visibility.Visible)
        {
            SearchBox.Focus();
        }
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

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ExecuteSelected();
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
