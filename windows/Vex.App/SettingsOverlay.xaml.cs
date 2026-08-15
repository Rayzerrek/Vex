using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

public partial class SettingsOverlay : OverlayControl
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
    private bool _fontsWarmed;

    public SettingsOverlay()
    {
        InitializeComponent();

        HideOnWindowDeactivate();

        // Terminal font size applies on release, not per thumb tick: every
        // commit resizes the ConPTY session in each pane, which turns a drag
        // into a slideshow when fired per pixel.
        FontSizeSlider.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => CommitFontSize()));
        FontSizeSlider.PreviewMouseLeftButtonUp += (_, _) => CommitFontSize();
        FontSizeSlider.KeyUp += (_, _) => CommitFontSize();

        ThemeList.ItemsSource = BuiltInThemes.All;
        var currentTheme = BuiltInThemes.All.FirstOrDefault(t => t.Name == AppSettings.Instance.ThemeName);
        ThemeList.SelectedItem = currentTheme ?? BuiltInThemes.VexDark;

        ShellPowerShell.IsChecked = AppSettings.Instance.Shell == "PowerShell";
        ShellNushell.IsChecked = AppSettings.Instance.Shell != "PowerShell";

        _activePage = PageGeneral;

        // Follow theme changes made from the quick switcher (Ctrl+Shift+M)
        // so the card selection stays in sync if both are open.
        AppSettings.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppSettings.ThemeName))
                return;
            var theme = BuiltInThemes.All.FirstOrDefault(t => t.Name == AppSettings.Instance.ThemeName);
            if (theme != null && !ReferenceEquals(ThemeList.SelectedItem, theme))
                ThemeList.SelectedItem = theme;
        };

        DataContext = AppSettings.Instance;
    }

    /// <summary>Fired once the closing animation finishes and the overlay is
    /// fully hidden; the window uses it to hand keyboard focus back to the
    /// active pane.</summary>
    public event Action? Hidden;

    protected override void HideCore() => Hide();

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
        OpenPopup(this);

        // Rasterize the panel once and animate the cached bitmap; without the
        // cache every frame re-renders the whole subtree (four pages).
        Panel.CacheMode = new BitmapCache();

        // Enumerating system fonts takes hundreds of milliseconds; warm the
        // list on a background thread so the Terminal page never hitches.
        WarmFonts();

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

        Panel.CacheMode = new BitmapCache();

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
            ClosePopup(this);
            Backdrop.Opacity = 0;
            Panel.Opacity = 0;
            Panel.CacheMode = null;
            Hidden?.Invoke();
        }
        else
        {
            Backdrop.Opacity = 1;
            Panel.Opacity = 1;
            // Drop the cache now: live content (theme cards, toggles) would
            // otherwise re-render the whole panel raster on every change.
            Panel.CacheMode = null;
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

        if (ReferenceEquals(page, PageTerminal))
            EnsureFontList();

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

        // A page's first visit realizes its whole subtree (41 theme cards);
        // let that layout pass finish before the transition starts so the
        // first animation frame does not pay for the realization.
        if (!page.IsMeasureValid)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                if (ReferenceEquals(_inPage, page) && IsVisible)
                    StartPageTransition();
            });
            return;
        }

        StartPageTransition();
    }

    private void StartPageTransition()
    {
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

    /// <summary>Attaches the font list the first time the Terminal page is
    /// visited; the eager XAML binding used to enumerate every system font on
    /// the UI thread while the main window was still loading.</summary>
    private void EnsureFontList()
    {
        if (FontCombo.ItemsSource != null)
            return;
        FontCombo.ItemsSource = AppSettings.Instance.AvailableFonts;
    }

    private void WarmFonts()
    {
        if (_fontsWarmed)
            return;
        _fontsWarmed = true;
        _ = Task.Run(() => _ = AppSettings.Instance.AvailableFonts);
    }

    private void CommitFontSize()
    {
        var size = (int)Math.Round(FontSizeSlider.Value);
        if (AppSettings.Instance.FontSize != size)
            AppSettings.Instance.FontSize = size;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Escape closes the overlay even when a control inside it (font
        // combo, slider) has keyboard focus.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Hide();
            e.Handled = true;
        }
    }

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
