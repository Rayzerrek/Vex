using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        AnimateOpen();

        // Use Dispatcher to focus after the popup opens
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Hide()
    {
        if (Visibility != Visibility.Visible)
            return;
        AnimateClose();
    }

    private static DoubleAnimation Anim(double from, double to, double ms) =>
        new(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

    private void AnimateOpen()
    {
        Backdrop.BeginAnimation(OpacityProperty, Anim(0, 1, 110));
        Panel.BeginAnimation(OpacityProperty, Anim(0, 1, 150));
        PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.96, 1, 170));
        PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.96, 1, 170));
        PanelTranslate.BeginAnimation(TranslateTransform.YProperty, Anim(8, 0, 170));
    }

    private void AnimateClose()
    {
        var fade = Anim(1, 0, 90);
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            if (Parent is System.Windows.Controls.Primitives.Popup popup) popup.IsOpen = false;
        };
        Backdrop.BeginAnimation(OpacityProperty, Anim(1, 0, 90));
        Panel.BeginAnimation(OpacityProperty, fade);
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
