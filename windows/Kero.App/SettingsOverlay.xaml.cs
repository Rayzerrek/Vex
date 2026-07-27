using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kero.App.Model;

namespace Kero.App;

public partial class SettingsOverlay : UserControl
{
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
        ThemeListBox.SelectedItem = currentTheme ?? BuiltInThemes.KeroDark;

        DataContext = AppSettings.Instance;
    }

    public void Toggle()
    {
        if (Visibility == Visibility.Visible) Hide();
        else Show();
    }

    public void Show()
    {
        Visibility = Visibility.Visible;
        if (Parent is System.Windows.Controls.Primitives.Popup popup) popup.IsOpen = true;
        
        var anim = new DoubleAnimation(320, 0, new Duration(TimeSpan.FromMilliseconds(250)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    public void Hide()
    {
        var anim = new DoubleAnimation(0, 320, new Duration(TimeSpan.FromMilliseconds(200)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (s, e) => {
            Visibility = Visibility.Collapsed;
            if (Parent is System.Windows.Controls.Primitives.Popup popup) popup.IsOpen = false;
        };
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Hide();

    private void ThemeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeListBox.SelectedItem is string themeName)
        {
            AppSettings.Instance.ThemeName = themeName;
        }
    }
}
