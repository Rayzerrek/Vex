using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vex.App.Model;

namespace Vex.App;

public partial class SettingsOverlay : UserControl
{
    private const double PanelWidth = 320;
    private const double AnimDuration = 300;

    private bool _animInProgress;
    private Stopwatch? _animClock;
    private bool _animClosing;
    private double _animFromX;
    private double _animToX;

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

        ThemeListBox.ItemsSource = BuiltInThemes.All;
        var currentTheme = BuiltInThemes.All.FirstOrDefault(t => t.Name == AppSettings.Instance.ThemeName);
        ThemeListBox.SelectedItem = currentTheme ?? BuiltInThemes.VexDark;

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
            // If a close is already running, stop it and reverse.
            CompositionTarget.Rendering -= OnAnimRendering;
            _animInProgress = false;
        }

        Visibility = Visibility.Visible;
        if (Parent is System.Windows.Controls.Primitives.Popup popup) popup.IsOpen = true;

        // Start from wherever the panel currently is (mid-animation or fully hidden).
        _animFromX = SlideTransform.X;
        _animToX = 0;
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

        _animFromX = SlideTransform.X;
        _animToX = PanelWidth;
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

        var x = _animFromX + ((_animToX - _animFromX) * eased);
        SlideTransform.X = x;

        if (_animClosing)
        {
            // Backdrop fades out in sync; panel slides out.
            Backdrop.Opacity = 1 - eased;
        }
        else
        {
            // Backdrop fades in immediately; panel slides in.
            Backdrop.Opacity = eased;
        }

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
            SlideTransform.X = PanelWidth;
        }
        else
        {
            Backdrop.Opacity = 1;
            SlideTransform.X = 0;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Hide();

    private void ThemeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeListBox.SelectedItem is TerminalTheme theme)
        {
            AppSettings.Instance.ThemeName = theme.Name;
        }
    }
}
