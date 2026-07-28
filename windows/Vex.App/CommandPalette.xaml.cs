using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vex.App.Model;

namespace Vex.App;

public partial class CommandPalette : UserControl
{
    private List<PaletteItem> _allItems = new();

    public CommandPalette()
    {
        InitializeComponent();
        Loaded += (s, e) => {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.Deactivated += (ws, we) => { Hide(); };
            }
        };
    }

    public void Show(IEnumerable<PaletteItem> items)
    {
        _allItems = items.ToList();
        Visibility = Visibility.Visible;
        if (Parent is System.Windows.Controls.Primitives.Popup popup) popup.IsOpen = true;
        SearchBox.Text = "";
        UpdateFilter();
        
        // Use Dispatcher to focus after the popup opens
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        if (Parent is System.Windows.Controls.Primitives.Popup popup) popup.IsOpen = false;
    }

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
