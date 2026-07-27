using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Kero.App.Model;

namespace Kero.App;

public partial class SettingsOverlay : UserControl
{
    private bool _isOpen;

    public SettingsOverlay()
    {
        InitializeComponent();
        DataContext = AppSettings.Instance;
        
        ThemeListBox.ItemsSource = BuiltInThemes.All.Select(t => t.Name).ToList();
        ThemeListBox.SelectedItem = AppSettings.Instance.ThemeName;
    }

    public void Toggle()
    {
        if (_isOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (_isOpen) return;
        _isOpen = true;
        Visibility = Visibility.Visible;
        
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };
        SlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
    }

    public void Close()
    {
        if (!_isOpen) return;
        _isOpen = false;
        
        var anim = new DoubleAnimation(320, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (s, e) => Visibility = Visibility.Collapsed;
        SlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Close();

    private void ThemeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeListBox.SelectedItem is string themeName)
        {
            AppSettings.Instance.ThemeName = themeName;
        }
    }
}
