using System.Windows;

namespace Vex.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public sealed partial class App : Application
{
    private readonly Task<System.Windows.Threading.Dispatcher> _compositionPrewarm = StartCompositionPrewarm();
    private int _compositionPrewarmStopped;
    // Instance field initializers run before Application's base constructor,
    // letting profile I/O and shell creation overlap WPF initialization too.
    private readonly (Task Settings, Task<Model.SessionSnapshot?> Session) _startupTasks = StartStartupTasks();

    public App()
    {
        Model.AppCrashLog.RegisterFatalExceptionHandlers(this);
        try
        {
            var profileDir = Model.AppProfile.DirectoryPath;
            System.IO.Directory.CreateDirectory(profileDir);
            System.Runtime.ProfileOptimization.SetProfileRoot(profileDir);
            System.Runtime.ProfileOptimization.StartProfile("startup.profile");
        }
        catch { }
        Model.StartupMark.Note("app constructed");

        if (Model.StartupMark.IsEnabled)
        {
            _ = _startupTasks.Session.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                    Model.StartupMark.Note("session task done");
                else
                    Model.StartupMark.Note("session task faulted");
            }, TaskScheduler.Default);
        }
    }

    private static (Task Settings, Task<Model.SessionSnapshot?> Session) StartStartupTasks()
    {
        Model.StartupMark.Note("startup tasks begin");
        // Shell creation now overlaps the base constructor too, so establish
        // terminal capabilities before scheduling any prewarm work.
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        var settingsTask = Task.Run(Model.AppSettings.Preload);
        var sessionTask = Model.SessionStore.ReadSnapshotAsync();

        _ = Task.WhenAll(settingsTask, sessionTask).ContinueWith(_ =>
        {
            var snapshot = sessionTask.Result;
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

        _ = settingsTask.ContinueWith(_ =>
        {
            Model.StartupMark.Note("terminal prewarm font begin");
            var settings = Model.AppSettings.Instance;
            Terminal.Native.NativeTerminalControl.Prewarm(settings.ThemeName, settings.FontFamily);
            Model.StartupMark.Note("terminal prewarm font ready");
            PrewarmWindowText();
        }, TaskScheduler.Default);

        return (settingsTask, sessionTask);
    }

    private static void PrewarmWindowText()
    {
        try
        {
            // Terminal glyph lookup does not initialize WPF text layout.
            // Prime the shared chrome font/layout caches while XAML loads.
            Model.StartupMark.Note("window text prewarm begin");
            foreach (var (family, text) in new[]
                { ("Segoe UI", "Vex Terminal Projects"), ("Segoe MDL2 Assets", "\uE710\uE8BB\uE921\uE922\uE923") })
            {
                var formatted = new System.Windows.Media.FormattedText(text,
                    System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface(family), 13, System.Windows.Media.Brushes.White, 1);
                _ = formatted.Width;
            }
            Model.StartupMark.Note("window text prewarm ready");
        }
        catch { /* Font prewarm is optional; WPF can initialize on first use. */ }
    }

    private static Task<System.Windows.Threading.Dispatcher> StartCompositionPrewarm()
    {
        var ready = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            try
            {
                Model.StartupMark.Note("composition prewarm begin");
                // Adding the first visual connects WPF's process render engine.
                // Keep this STA context alive until the window owns its channel.
                var visual = new System.Windows.Media.DrawingVisual();
                visual.Children.Add(new System.Windows.Media.DrawingVisual());
                Model.StartupMark.Note("composition prewarm ready");
                ready.SetResult(dispatcher);
                System.Windows.Threading.Dispatcher.Run();
                GC.KeepAlive(visual);
            }
            catch (Exception exception)
            {
                Model.StartupMark.Note($"composition prewarm failed: {exception}");
                ready.TrySetException(exception);
            }
            finally
            {
                dispatcher.InvokeShutdown();
            }
        }) { IsBackground = true, Name = "Vex composition prewarm" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    private void StopCompositionPrewarm()
    {
        if (Interlocked.Exchange(ref _compositionPrewarmStopped, 1) != 0)
            return;
        _ = _compositionPrewarm.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion)
                task.Result.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
            else
                Model.StartupMark.Note($"composition prewarm failed: {task.Exception}");
        }, TaskScheduler.Default);
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

        await _startupTasks.Settings.ConfigureAwait(true);
        Model.StartupMark.Note("settings ready");

        // Chrome colors follow the active terminal theme (tab strip,
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

            Vex.App.Model.AppIconTracker.OnThemeChanged();
        };

        var workspace = new Model.Workspace();
        var sessionReady = _startupTasks.Session.IsCompletedSuccessfully;
        if (sessionReady)
        {
            Model.SessionStore.Populate(workspace, _startupTasks.Session.Result);
            Model.StartupMark.Note("session populated before window");
        }

        Model.StartupMark.Note("window construction begin");
        var window = new MainWindow(workspace);
        Model.StartupMark.Note("window constructed");
        window.ContentRendered += (_, _) => StopCompositionPrewarm();
        window.Show();
        Model.StartupMark.Note("window shown");

        // Usually the background read wins the race and WPF builds the final
        // tree once. Slow storage must not delay the first visible frame;
        // retain the skeleton fallback for the uncommon unfinished read.
        if (!sessionReady)
        {
            var snapshot = await _startupTasks.Session.ConfigureAwait(true);
            Model.SessionStore.Populate(workspace, snapshot);
            Model.StartupMark.Note("session populated after window");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopCompositionPrewarm();
        Terminal.Native.TerminalSessionPrewarmer.Dispose();
        base.OnExit(e);
    }
}
