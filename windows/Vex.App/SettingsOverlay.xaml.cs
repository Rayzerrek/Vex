using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Vex.App.Model;

namespace Vex.App;

public sealed class SliderFillConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4)
            return 0.0;

        var value = System.Convert.ToDouble(values[0]);
        var min = System.Convert.ToDouble(values[1]);
        var max = System.Convert.ToDouble(values[2]);
        var width = System.Convert.ToDouble(values[3]);

        if (max <= min || width <= 0)
            return 0.0;

        var ratio = (value - min) / (max - min);
        return Math.Clamp(ratio, 0.0, 1.0) * width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public partial class SettingsOverlay : UserControl
{
    private const double AnimDuration = 280;
    private const double PageDuration = 130;

    private bool _animInProgress;
    private Stopwatch? _animClock;
    private bool _animClosing;
    private double _animFrom;

    private FrameworkElement? _activePage;
    private FrameworkElement? _outPage;
    private FrameworkElement? _inPage;
    private Stopwatch? _pageClock;
    private bool _pageFadingOut = true;

    public SettingsOverlay()
    {
        InitializeComponent();

        Loaded += (s, e) => {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.Deactivated += (ws, we) => { Hide(); };
            }
        };

        ThemeList.ItemsSource = BuiltInThemes.All;
        var currentTheme = BuiltInThemes.All.FirstOrDefault(t => t.Name == AppSettings.Instance.ThemeName);
        ThemeList.SelectedItem = currentTheme ?? BuiltInThemes.VexDark;

        ShellPowerShell.IsChecked = AppSettings.Instance.Shell == "PowerShell";
        ShellNushell.IsChecked = AppSettings.Instance.Shell != "PowerShell";

        _activePage = PageGeneral;

        DataContext = AppSettings.Instance;
    }

    public void Toggle()
    {
        if (Visibility == Visibility.Visible) Hide();
        else Show();
    }

    public void Show()
    {
        if (_animInProgress && _animClosing)
        {
            CompositionTarget.Rendering -= OnAnimRendering;
            _animInProgress = false;
        }

        Visibility = Visibility.Visible;
        if (Parent is System.Windows.Controls.Primitives.Popup popup) popup.IsOpen = true;

        // Continue from wherever the panel currently sits mid-animation.
        _animFrom = Panel.Opacity;
        _animClosing = false;
        _animInProgress = true;
        _animClock = Stopwatch.StartNew();
        CompositionTarget.Rendering -= OnAnimRendering;
        CompositionTarget.Rendering += OnAnimRendering;
    }

    public void Hide()
    {
        if (_animInProgress && !_animClosing)
        {
            CompositionTarget.Rendering -= OnAnimRendering;
            _animInProgress = false;
        }

        _animFrom = Panel.Opacity;
        _animClosing = true;
        _animInProgress = true;
        _animClock = Stopwatch.StartNew();
        CompositionTarget.Rendering -= OnAnimRendering;
        CompositionTarget.Rendering += OnAnimRendering;
    }

    private void OnAnimRendering(object? sender, EventArgs e)
    {
        var elapsed = _animClock?.Elapsed.TotalMilliseconds ?? AnimDuration;
        var t = Math.Min(elapsed / AnimDuration, 1.0);
        // Smoothstep: gentle start and end, no lurch.
        var eased = t * t * (3 - 2 * t);
        var p = _animClosing ? 1 - eased : eased;

        Backdrop.Opacity = p;
        Panel.Opacity = p;
        PanelScale.ScaleX = PanelScale.ScaleY = 0.96 + 0.04 * p;
        PanelTranslate.Y = 16 * (1 - p);

        if (eased < 1)
            return;

        CompositionTarget.Rendering -= OnAnimRendering;
        _animInProgress = false;
        _animClock = null;

        if (_animClosing)
        {
            Visibility = Visibility.Collapsed;
            if (Parent is System.Windows.Controls.Primitives.Popup popup) popup.IsOpen = false;
            Backdrop.Opacity = 0;
            Panel.Opacity = 0;
        }
        else
        {
            Backdrop.Opacity = 1;
            Panel.Opacity = 1;
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
            return;

        var page = tag switch
        {
            "general" => PageGeneral,
            "appearance" => PageAppearance,
            "terminal" => PageTerminal,
            "about" => PageAbout,
            _ => PageGeneral,
        };

        SelectPage(page);
    }

    private void SelectPage(FrameworkElement page)
    {
        if (ReferenceEquals(_activePage, page))
            return;

        _outPage = _activePage;
        _inPage = page;
        _activePage = page;

        page.Visibility = Visibility.Visible;
        page.Opacity = 0;
        SetTranslateX(page, 16);

        // First selection (opening the overlay) has nothing to fade out.
        if (_outPage == null)
        {
            page.Opacity = 1;
            SetTranslateX(page, 0);
            return;
        }

        _pageFadingOut = true;
        _pageClock = Stopwatch.StartNew();
        CompositionTarget.Rendering -= OnPageRendering;
        CompositionTarget.Rendering += OnPageRendering;
    }

    private void OnPageRendering(object? sender, EventArgs e)
    {
        var elapsed = _pageClock?.Elapsed.TotalMilliseconds ?? PageDuration;
        var t = Math.Min(elapsed / PageDuration, 1.0);
        var eased = t * t * (3 - 2 * t);

        if (_pageFadingOut)
        {
            if (_outPage != null)
            {
                _outPage.Opacity = 1 - eased;
                SetTranslateX(_outPage, -16 * eased);
            }

            if (eased < 1)
                return;

            if (_outPage != null)
                _outPage.Visibility = Visibility.Collapsed;

            _pageFadingOut = false;
            _pageClock = Stopwatch.StartNew();
        }
        else
        {
            if (_inPage != null)
            {
                _inPage.Opacity = eased;
                SetTranslateX(_inPage, 16 * (1 - eased));
            }

            if (eased < 1)
                return;

            if (_inPage != null)
            {
                _inPage.Opacity = 1;
                SetTranslateX(_inPage, 0);
            }

            CompositionTarget.Rendering -= OnPageRendering;
            _pageClock = null;
        }
    }

    private static void SetTranslateX(FrameworkElement element, double x)
    {
        if (element.RenderTransform is TranslateTransform t)
        {
            t.X = x;
        }
        else
        {
            element.RenderTransform = new TranslateTransform(x, 0);
        }
    }

    private void Shell_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string shell })
            AppSettings.Instance.Shell = shell;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Hide();

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeList.SelectedItem is TerminalTheme theme)
        {
            AppSettings.Instance.ThemeName = theme.Name;
        }
    }

    private void ThemeList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Item containers are non-focusable so the terminal keeps keyboard
        // focus, and WPF skips click-selection for items that cannot take
        // focus. Select the clicked card explicitly.
        if (ItemsControl.ContainerFromElement(ThemeList, e.OriginalSource as DependencyObject) is ListBoxItem item)
            ThemeList.SelectedItem = item.DataContext;
    }
}
