using System.Windows;

namespace Vex.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public sealed partial class App : Application
{
    private readonly Task _settingsTask;
    private readonly Task<Model.SessionSnapshot?> _sessionTask;

    public App()
    {
        Model.StartupMark.Note("app constructed");

        // Start settings and session I/O immediately. The prewarmer is started
        // once both values are available, without a second JSON parser that can
        // disagree with the source-generated deserializer.
        _settingsTask = Task.Run(Model.AppSettings.Preload);
        _sessionTask = Model.SessionStore.ReadSnapshotAsync();

        _ = Task.WhenAll(_settingsTask, _sessionTask).ContinueWith(_ =>
        {
            var snapshot = _sessionTask.Result;
            var projects = snapshot?.Projects;
            var selectedProjectIndex = snapshot?.SelectedProjectIndex ?? 0;
            var workingDirectory = projects is { Count: > 0 }
                && selectedProjectIndex >= 0
                && selectedProjectIndex < projects.Count
                ? projects[selectedProjectIndex].WorkingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var shellId = Model.AppSettings.Instance.ShellId;
            Terminal.Native.TerminalSessionPrewarmer.StartPrewarm(workingDirectory, shellId);
        }, TaskScheduler.Default);

        _ = _settingsTask.ContinueWith(_ =>
        {
            Model.StartupMark.Note("terminal prewarm font begin");
            var settings = Model.AppSettings.Instance;
            Terminal.Native.NativeTerminalControl.Prewarm(settings.ThemeName, settings.FontFamily);
            Model.StartupMark.Note("terminal prewarm font ready");
        }, TaskScheduler.Default);

        if (Model.StartupMark.IsEnabled)
        {
            _ = _sessionTask.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                    Model.StartupMark.Note("session task done");
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
        Model.StartupMark.Note("appearance ready");
        Vex.App.Model.AppSettings.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(Vex.App.Model.AppSettings.ThemeName)
                or nameof(Vex.App.Model.AppSettings.Appearance)))
                return;

            var settings = Vex.App.Model.AppSettings.Instance;
            ChromePalette.Apply(settings.ThemeName);

            // Editors paint their own surface, gutter and syntax palette on
            // top of the chrome brushes, so both properties need the redraw:
            // within one appearance a theme switch re-tints the editor
            // surface, and across appearances the syntax set flips too.
            Vex.App.Model.EditorPane.OnThemeChanged();
        };

        // Construct the workspace and window immediately without awaiting I/O.
        // The first frame renders a skeleton UI (like Ghostty/SuperLogical),
        // hiding the session loading latency entirely.
        Model.StartupMark.Note("window construction begin");
        var workspace = new Model.Workspace();
        var window = new MainWindow(workspace);
        Model.StartupMark.Note("window constructed");
        window.Show();
        Model.StartupMark.Note("window shown");

        // Now await the session snapshot and populate the workspace.
        var snapshot = await _sessionTask.ConfigureAwait(true);
        Model.SessionStore.Populate(workspace, snapshot);
        Model.StartupMark.Note("session populated");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Terminal.Native.TerminalSessionPrewarmer.Dispose();
        base.OnExit(e);
    }
}
