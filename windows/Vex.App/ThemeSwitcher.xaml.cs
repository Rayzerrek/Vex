using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vex.App.Model;

namespace Vex.App;

public partial class ThemeSwitcher : UserControl
{
    private string _openingTheme = "";

    public ThemeSwitcher()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window)
                window.Deactivated += (_, _) => Hide();
        };
        ThemeList.ItemsSource = BuiltInThemes.All;
    }

    public void Show()
    {
        // Snapshot so Escape can revert a previewed theme.
        _openingTheme = AppSettings.Instance.ThemeName;

        Visibility = Visibility.Visible;
        if (Parent is System.Windows.Controls.Primitives.Popup popup) popup.IsOpen = true;

        ThemeList.SelectedItem = BuiltInThemes.All.FirstOrDefault(t => t.Name == _openingTheme)
            ?? BuiltInThemes.VexDark;

        AnimateOpen();

        Dispatcher.BeginInvoke(() => ThemeList.Focus(), System.Windows.Threading.DispatcherPriority.Input);
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
