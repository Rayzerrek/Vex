using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vex.App.Model;

namespace Vex.App;

public partial class ThemeSwitcher : OverlayControl
{
    private string _openingTheme = "";

    public ThemeSwitcher()
    {
        InitializeComponent();
        HideOnWindowDeactivate();
    }

    protected override void HideCore() => Hide();

    public void Show()
    {
        if (ThemeList.ItemsSource == null)
            ThemeList.ItemsSource = BuiltInThemes.All;

        // Snapshot so Escape can revert a previewed theme.
        _openingTheme = AppSettings.Instance.ThemeName;

        Visibility = Visibility.Visible;
        OpenPopup(this);

        ThemeList.SelectedItem = BuiltInThemes.All.FirstOrDefault(t => t.Name == _openingTheme)
            ?? BuiltInThemes.VexDark;

        AnimateOverlayOpen(Backdrop, Panel, PanelScale, PanelTranslate);

        Dispatcher.BeginInvoke(() => ThemeList.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Hide() => HideWithAnimation(Backdrop, Panel);

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Hide();

    private void Panel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ThemeList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Item containers are non-focusable, so click-selection is done by hand.
        if (ItemsControl.ContainerFromElement(ThemeList, e.OriginalSource as DependencyObject) is ListBoxItem item)
        {
            ThemeList.SelectedItem = item.DataContext;
            Hide();
            e.Handled = true;
        }
    }

    private void ThemeList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                AppSettings.Instance.ThemeName = _openingTheme;
                Hide();
                e.Handled = true;
                break;
            case Key.Enter:
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
        var count = BuiltInThemes.All.Length;
        if (count == 0)
            return;
        var next = ((ThemeList.SelectedIndex + delta) % count + count) % count;
        ThemeList.SelectedIndex = next;
    }

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection is the live preview: moving across cards re-tints the
        // whole app immediately, so Enter/click simply keep it.
        if (ThemeList.SelectedItem is TerminalTheme theme && AppSettings.Instance.ThemeName != theme.Name)
            AppSettings.Instance.ThemeName = theme.Name;
    }
}
