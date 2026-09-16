using System.Windows;

namespace Vex.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public sealed partial class App : Application
{
    private readonly Task _settingsTask;
    private readonly Task<Model.Workspace> _sessionTask;

    public App()
    {
        Model.StartupMark.Note("app constructed");

        // The generated entry point parses App.xaml after this constructor.
        // Start all thread-safe disk and font work now so it overlaps that
        // otherwise unavoidable WPF resource initialization.
        _settingsTask = Task.Run(Model.AppSettings.Preload);
        _sessionTask = Task.Run(Model.SessionStore.LoadAsync);

        _ = _settingsTask.ContinueWith(_ =>
        {
            Model.StartupMark.Note("terminal prewarm begin");
            var settings = Model.AppSettings.Instance;
            Terminal.Native.NativeTerminalControl.Prewarm(settings.ThemeName, settings.FontFamily);
            Model.StartupMark.Note("terminal prewarm ready");
        }, TaskScheduler.Default);

        if (Model.StartupMark.IsEnabled)
        {
            _ = _sessionTask.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                    Model.StartupMark.Note($"session task done ({t.Result.Projects.Count} projects)");
                else
                    Model.StartupMark.Note("session task faulted");
            }, TaskScheduler.Default);
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Model.StartupMark.Note("OnStartup begin");
        if (Model.StartupMark.IsEnabled)
        {
            try
            {
                var delta = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
                Model.StartupMark.Note($"process start {delta.TotalMilliseconds:F0}ms before OnStartup");
            }
            catch { }
        }

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

        await _settingsTask.ConfigureAwait(true);
        Model.StartupMark.Note("settings ready");

        // Chrome colors follow the active terminal theme (sidebar, tab strip,
        // pane chrome, accents). Resolve the stored appearance first so the
        // very first frame is already dark or light — never a half-applied
        // mix. Re-apply whenever the theme or appearance changes.
        Model.StartupMark.Note("appearance begin");
        Vex.App.Model.AppSettings.Instance.InitializeAppearance();
        ChromePalette.Apply(Vex.App.Model.AppSettings.Instance.ThemeName);
        Model.StartupMark.Note("palette applied");
        Vex.App.Model.EditorHighlighting.SetAppearance(
            Vex.App.Model.AppSettings.Instance.IsDarkAppearance);
        Model.StartupMark.Note("appearance ready");
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

        // The session read started alongside settings and normally completes
        // before appearance setup. Bind the final model once so window
        // construction cannot realize or subscribe to throwaway state.
        var workspace = await _sessionTask.ConfigureAwait(true);
        Model.StartupMark.Note("session ready");

        Model.StartupMark.Note("window construction begin");
        var window = new MainWindow(workspace);
        Model.StartupMark.Note("window constructed");
        window.Show();
        Model.StartupMark.Note("window shown");
    }
}
