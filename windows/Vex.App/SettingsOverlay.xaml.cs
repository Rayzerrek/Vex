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
using Vex.Terminal;

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

        _activePage = PageGeneral;

        // Follow theme changes made from the quick switcher (Ctrl+Shift+M)
        // so the card selection stays in sync if both are open, and rebind
        // when the appearance flips (the offered set changes with it).
        AppSettings.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.Appearance))
                SyncAppearanceToggles();
            if (e.PropertyName is not (nameof(AppSettings.ThemeName) or nameof(AppSettings.Appearance))
                || ThemeList.ItemsSource == null)
                return;
            RebindThemeList();
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

        // Reflect the persisted appearance even if it was last changed before
        // this overlay existed.
        SyncAppearanceToggles();

        // Rasterize the panel once and animate the cached bitmap; without the
        // cache every frame re-renders the whole subtree (four pages).
        Panel.CacheMode = new BitmapCache();

        // Enumerating system fonts takes hundreds of milliseconds; warm the
        // list on a background thread so the Terminal page never hitches.
        WarmFonts();
        WarmShells();

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
        if (ReferenceEquals(page, PageAppearance))
            EnsureThemeList();

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

    // ---- Shell selection ----------------------------------------------------

    // Detection probes the filesystem and PATH once; like font enumeration it
    // is kept off the startup path and runs behind the first frame.
    private bool _shellsWarmed;

    private void WarmShells()
    {
        if (_shellsWarmed)
            return;
        _shellsWarmed = true;
        _ = Task.Run(() => Dispatcher.BeginInvoke(DispatcherPriority.Background, PopulateShellList));
    }

    private sealed record ShellChoice(string Id, string DisplayName, string? Detail);

    private void PopulateShellList()
    {
        var settings = AppSettings.Instance;
        var choices = new List<ShellChoice>
        {
            new(ShellRegistry.SystemDefaultId, "System default",
                TerminalSession.DefaultShell()),
        };
        foreach (var shell in ShellRegistry.Detected())
            choices.Add(new ShellChoice(shell.Id, shell.Name, shell.Program));

        // Custom entries survive even when their program path is gone, so a
        // temporarily unplugged drive does not silently drop a configured
        // shell from the picker.
        foreach (var custom in settings.CustomShells.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
            choices.Add(new ShellChoice(custom.Id, custom.Name,
                string.IsNullOrEmpty(custom.Arguments) ? custom.Program : $"{custom.Program} {custom.Arguments}"));

        ShellCombo.ItemsSource = choices;
        SelectShellComboItem(settings.ShellId);
        UpdateCustomShellVisibility();
    }

    private void SelectShellComboItem(string id)
    {
        var match = ((IEnumerable<ShellChoice>?)ShellCombo.ItemsSource)?.FirstOrDefault(c => c.Id == id);
        if (match is not null)
            ShellCombo.SelectedItem = match;
    }

    private void ShellCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The initial population assignment fires this too; skip before both
        // lists exist so nothing is persisted mid-setup.
        if (_shellsWarmed && ShellDetail is not null)
        {
            if (ShellCombo.SelectedItem is ShellChoice choice)
                AppSettings.Instance.ShellId = choice.Id;
            UpdateCustomShellVisibility();
        }
        UpdateShellDetail();
    }

    private void UpdateShellDetail()
    {
        if (ShellDetail is null || !_shellsWarmed)
            return;
        var detail = ShellCombo.SelectedItem as ShellChoice;
        ShellDetail.Text = detail?.Detail ?? "Interpreter used for new panes.";
    }

    private void UpdateCustomShellVisibility()
    {
        if (CustomShellEmpty is null || CustomShellList is null || AppSettings.Instance.CustomShells is not { } shells)
            return;
        var any = shells.Count > 0;
        CustomShellEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        CustomShellList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any && _shellsWarmed && ShellCombo.SelectedItem is ShellChoice { Id: var selectedId }
            && selectedId != ShellRegistry.SystemDefaultId
            && !ShellRegistry.HasDetected(selectedId))
        {
            // The removed custom shell was selected; fall back to system
            // default. Pick the entry explicitly — the stale custom choice may
            // still sit in ItemsSource until the list is rebuilt.
            AppSettings.Instance.ShellId = ShellRegistry.SystemDefaultId;
            var systemChoice = ((IEnumerable<ShellChoice>?)ShellCombo.ItemsSource)?
                .FirstOrDefault(c => c.Id == ShellRegistry.SystemDefaultId);
            if (systemChoice is not null)
                ShellCombo.SelectedItem = systemChoice;
        }
    }

    private void CustomShellAdd_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Instance;
        var profile = new ShellProfile
        {
            Id = "custom-" + Guid.NewGuid().ToString("N"),
            Name = "",
            Program = "",
            Arguments = "",
        };
        settings.CustomShells.Add(profile);
        settings.SaveSoon(); // List identity did not change, so force persistence.
        CustomShellList.Items.Refresh();
        UpdateCustomShellVisibility();
    }

    private void CustomShellRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShellProfile profile)
            return;
        var settings = AppSettings.Instance;
        settings.CustomShells.Add(profile); // Touch the list so the setter persists.
        settings.CustomShells.Remove(profile);
        settings.SaveSoon();
        CustomShellList.Items.Refresh();
        UpdateCustomShellVisibility();
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

    private void EnsureThemeList()
    {
        if (ThemeList.ItemsSource == null)
            RebindThemeList();
    }

    /// <summary>Binds the card grid to the themes of the active appearance.
    /// Called on first page visit and whenever the appearance flips, so the
    /// offered cards always match the surrounding chrome.</summary>
    private void RebindThemeList()
    {
        var themes = BuiltInThemes.ForAppearance(AppSettings.Instance.IsDarkAppearance);
        ThemeList.ItemsSource = themes;
        var currentTheme = themes.FirstOrDefault(t => t.Name == AppSettings.Instance.ThemeName);
        ThemeList.SelectedItem = currentTheme ?? themes.FirstOrDefault();
    }

    private bool _suppressAppearanceSync;

    private void SyncAppearanceToggles()
    {
        _suppressAppearanceSync = true;
        var dark = AppSettings.Instance.IsDarkAppearance;
        AppearanceDarkOption.IsChecked = dark;
        AppearanceLightOption.IsChecked = !dark;
        _suppressAppearanceSync = false;
    }

    private void Appearance_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAppearanceSync || sender is not RadioButton { Tag: string tag })
            return;
        AppSettings.Instance.SetAppearance(
            tag == "light" ? AppSettings.LightAppearance : AppSettings.DarkAppearance);
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
