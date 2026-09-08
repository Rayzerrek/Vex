using System.Windows;

namespace Vex.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Model.StartupMark.Note("OnStartup begin");

        // WPF throttles Storyboard/timeline animations to 60 FPS no matter the
        // display (a null DesiredFrameRate falls back to 60). 120 covers
        // 60/120 Hz panels at one sample per frame; 240 doubled timeline-clock
        // pressure on the UI thread (which also feeds VT output and builds
        // GlyphRuns), starving the terminal pump during animations.
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline),
            new PropertyMetadata(120));

        // Required for ported TUI applications (vim, agy, pi, etc.) to
        // output VT sequences properly under ConPTY.
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");

        // Parallel preload: kick off settings and session file reads
        // simultaneously on background threads. The two file reads were
        // previously serial on the UI thread, blocking the first frame.
        // Running them in parallel cuts the I/O wait roughly in half, and
        // moving the deserialization off the UI thread means WPF can start
        // its rendering pipeline sooner.
        var settingsTask = Task.Run(Vex.App.Model.AppSettings.Preload);
        var sessionTask = Task.Run(Vex.App.Model.SessionStore.LoadAsync);

        // Prewarm font metrics, OpenType glyph tables, and theme palette on a
        // background task as soon as settings load. Runs completely in parallel
        // with session restore and chrome color setup, eliminating font parsing
        // and brush allocation stalls on the UI thread during MainWindow creation.
        var prewarmTask = settingsTask.ContinueWith(_ =>
        {
            var s = Vex.App.Model.AppSettings.Instance;
            Vex.App.Terminal.Native.NativeTerminalControl.Prewarm(s.ThemeName, s.FontFamily);
        }, TaskScheduler.Default);

        await settingsTask.ConfigureAwait(true);
        Model.StartupMark.Note("settings ready");

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

        // Await the session data (it started in parallel with settings, so
        // it may already be done by now). Creating the window after both
        // are ready avoids a flash of empty content.
        var workspace = await sessionTask.ConfigureAwait(true);
        Model.StartupMark.Note("session ready");

        // Create and show the main window with the pre-loaded workspace.
        var window = new MainWindow(workspace);
        window.Show();
        Model.StartupMark.Note("window shown");
    }
}

