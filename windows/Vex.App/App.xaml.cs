using System.Windows;

namespace Vex.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF throttles Storyboard/timeline animations to 60 FPS no matter the
        // display (a null DesiredFrameRate falls back to 60). Raise the ceiling
        // so every animation samples once per rendered frame — i.e. at the
        // monitor's native refresh rate on 120/144/240 Hz panels.
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline),
            new PropertyMetadata(240));

        // Chrome colors follow the active terminal theme (sidebar, tab strip,
        // pane chrome, accents). Resolve the stored appearance first so the
        // very first frame is already dark or light — never a half-applied
        // mix. Re-apply whenever the theme or appearance changes.
        Vex.App.Model.AppSettings.Instance.InitializeAppearance();
        ChromePalette.Apply(Vex.App.Model.AppSettings.Instance.ThemeName);
        Vex.App.Model.EditorHighlighting.SetAppearance(
            Vex.App.Model.AppSettings.Instance.IsDarkAppearance);
        Vex.App.Model.AppSettings.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(Vex.App.Model.AppSettings.ThemeName)
                or nameof(Vex.App.Model.AppSettings.Appearance)))
                return;

            var settings = Vex.App.Model.AppSettings.Instance;
            ChromePalette.Apply(settings.ThemeName);

            if (e.PropertyName == nameof(Vex.App.Model.AppSettings.Appearance))
            {
                // Editors carry their own syntax palette, so an appearance
                // flip swaps One Dark for One Light (and back) live.
                Vex.App.Model.EditorHighlighting.SetAppearance(settings.IsDarkAppearance);
                Vex.App.Model.EditorPane.OnAppearanceChanged();
            }
        };

        // Required for ported TUI applications (vim, agy, pi, etc.) to
        // output VT sequences properly under ConPTY.
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
    }
}

