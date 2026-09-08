using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Vex.App.Terminal;
using Vex.Terminal;

namespace Vex.App.Model;

/// <summary>
/// Operational state of a pane, surfaced in the title bar so a user (or a
/// teammate reviewing an agent session) can tell at a glance whether a pane
/// is waiting for input, running a full-screen TUI, or has exited.
/// </summary>
public enum PaneState
{
    /// <summary>Shell prompt visible; the pane is waiting for input.</summary>
    Idle,
    /// <summary>A full-screen TUI (vim, htop, an agent) is running on the
    /// alternate screen buffer.</summary>
    Busy,
    /// <summary>The underlying process has exited.</summary>
    Exited,
}

/// <summary>
/// One node of a tab's binary split tree. Mirrors upstream's pane model:
/// every split replaces a leaf with a <see cref="SplitPane"/> holding the
/// original leaf and the new one.
/// </summary>
public abstract class PaneNode : ObservableObject
{
}

public abstract class LeafPane : PaneNode, IDisposable
{
    private string _title = "Pane";
    private bool _isFocused;
    private bool _isDirty;
    private PaneState _state = PaneState.Idle;
    private object? _view;

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        set => Set(ref _isDirty, value);
    }

    /// <summary>Operational state shown as a coloured dot in the pane title
    /// bar. Idle = dim, Busy (TUI) = accent, Exited = red.</summary>
    public PaneState State
    {
        get => _state;
        set => Set(ref _state, value);
    }

    public bool IsFocused
    {
        get => _isFocused;
        set => Set(ref _isFocused, value);
    }

    public object View
    {
        get
        {
            if (_view is null)
            {
                _view = CreateView();
                PrepareEntrance(_view);
            }
            return _view;
        }
    }

    /// <summary>
    /// Fades a pane's view in the first time it is realized, so a split or a
    /// new tab reads as a soft reveal instead of a hard pop. Later re-shows
    /// (switching back to a tab) skip the fade: the view has already been
    /// revealed once and the terminal stays live in the tree. Panes realized
    /// in the first seconds of the process (session restore) also skip it:
    /// fading every restored pane forces extra composition frames exactly
    /// when the first frame is most expensive.
    /// </summary>
    private static void PrepareEntrance(object view)
    {
        if (view is not FrameworkElement element)
            return;
        if (TimeSinceProcessStart() < TimeSpan.FromSeconds(3))
            return;
        element.Opacity = 0;
        RoutedEventHandler onLoaded = null!;
        onLoaded = (_, _) =>
        {
            element.Loaded -= onLoaded;
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        };
        element.Loaded += onLoaded;
    }

    private static TimeSpan TimeSinceProcessStart()
    {
        try { return DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return TimeSpan.MaxValue; }
    }

    /// <summary>The view when it already exists; unlike <see cref="View"/>,
    /// never forces creation of a pane that was never shown.</summary>
    protected object? ViewIfCreated => _view;

    private AppIcon? _appIcon;

    /// <summary>Icon for the app running in this pane (null until resolved).</summary>
    public AppIcon? AppIcon
    {
        get => _appIcon;
        set => Set(ref _appIcon, value);
    }

    protected abstract object CreateView();

    public event Action? FocusRequested;
    public event Action<Orientation>? SplitRequested;
    public event Action? NewTabRequested;
    public event Action<LeafPane>? ProcessExited;

    protected void RequestFocus() => FocusRequested?.Invoke();
    protected void RequestSplit(Orientation orientation) => SplitRequested?.Invoke(orientation);
    protected void RequestNewTab() => NewTabRequested?.Invoke();
    protected void RequestClose() => ProcessExited?.Invoke(this);

    /// <summary>Pane title-bar actions; the owning tab re-raises the same
    /// events the keyboard shortcuts use.</summary>
    public void Split(Orientation orientation) => RequestSplit(orientation);
    public void Close() => RequestClose();

    public abstract void Focus();

    public virtual void Dispose()
    {
        if (_view is IDisposable d) d.Dispose();
    }
}

public sealed class TerminalPane : LeafPane
{
    private readonly string _workingDirectory;
    private string _lastTitle = "";
    private Action<string>? _titleRawHandler;

    public TerminalPane(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        Title = "Terminal";
        AppIconTracker.Register(this);
    }

    /// <summary>
    /// Re-resolves the tab icon from the shared process-tree index, the shim's
    /// command line and the last raw OSC title; called by the tracker timer
    /// on the UI thread.
    /// </summary>
    public void RefreshAppIcon(ProcessTree.Index index)
    {
        if (ViewIfCreated is not ITerminalView terminal)
            return;
        if (terminal.ProcessId is not { } pid)
            return;
        var process = ProcessTree.DeepestDescendant(index, (uint)pid, AppIconCatalog.ExcludedShells);
        if (process is not { } deepest)
            return;
        var commandLine = AppIconCatalog.IsShimHost(deepest.Name)
            ? ProcessCommandLine.Get(deepest.Pid)
            : null;
        // Assign null as well as a resolved icon. Otherwise a shell icon (most
        // often Nushell) survives after a node-hosted app starts but its
        // command line is temporarily unreadable.
        AppIcon = AppIconCatalog.Resolve(deepest.Name, commandLine, _lastTitle);
    }

    private bool _focusPendingLoaded;

    public override void Focus()
    {
        if (View is not ITerminalView tv)
            return;
        var element = (System.Windows.FrameworkElement)View;

        if (element.IsKeyboardFocused)
            return;

        void FocusWhenVisible()
        {
            // Run after selection bindings, layout, and the click/key event
            // that created the tab. This prevents the tab strip or new-tab
            // button from taking focus back from a newly realized terminal.
            element.Dispatcher.BeginInvoke(() =>
            {
                if (IsFocused && element.IsVisible && !element.IsKeyboardFocused)
                    tv.FocusTerminal();
            }, DispatcherPriority.Input);
        }

        if (element.IsLoaded)
        {
            FocusWhenVisible();
            return;
        }

        if (_focusPendingLoaded)
            return;

        _focusPendingLoaded = true;
        // A brand-new tab has no visual tree yet. Its Loaded event is the
        // first point at which WPF can move keyboard focus to its terminal.
        System.Windows.RoutedEventHandler onLoaded = null!;
        onLoaded = (_, _) =>
        {
            _focusPendingLoaded = false;
            element.Loaded -= onLoaded;
            FocusWhenVisible();
        };
        element.Loaded += onLoaded;
    }

    protected override object CreateView()
    {
        var view = new Terminal.Native.NativeTerminalControl(_workingDirectory);
        _titleRawHandler = rawTitle =>
        {
            _lastTitle = rawTitle;
            // OSC titles arrive before the next process-tree poll and are the
            // only reliable signal for some WSL and Node launchers.
            if (AppIconCatalog.FromTitle(rawTitle) is { } icon)
                AppIcon = icon;
        };
        view.TitleRawChanged += _titleRawHandler;
        view.TitleChanged += title =>
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                if (title.Contains('\\') || title.Contains('/'))
                {
                    try { title = System.IO.Path.GetFileNameWithoutExtension(title); } catch {}
                }
                Title = title;
            }
        };
        view.FocusGained += RequestFocus;
        view.TuiModeChanged += isTui => State = isTui ? PaneState.Busy : PaneState.Idle;
        view.ProcessExited += exitCode =>
        {
            State = PaneState.Exited;
            RequestClose();
        };
        view.CommandRequested += command =>
        {
            switch (command)
            {
                case TerminalCommand.SplitRight:
                    RequestSplit(Orientation.Horizontal);
                    break;
                case TerminalCommand.SplitDown:
                    RequestSplit(Orientation.Vertical);
                    break;
                case TerminalCommand.NewTab:
                    RequestNewTab();
                    break;
                case TerminalCommand.ClosePane:
                    RequestClose();
                    break;
            }
        };
        return view;
    }

    public override void Dispose()
    {
        AppIconTracker.Unregister(this);
        if (_titleRawHandler is { } handler && ViewIfCreated is Terminal.Native.NativeTerminalControl control)
            control.TitleRawChanged -= handler;
        base.Dispose();
    }
}

/// <summary>Two panes separated by a draggable splitter.</summary>
public sealed class SplitPane : PaneNode
{
    private PaneNode _first;
    private PaneNode _second;
    private double _ratio = 0.5;

    public SplitPane(Orientation orientation, PaneNode first, PaneNode second)
    {
        Orientation = orientation;
        _first = first;
        _second = second;
    }

    public Orientation Orientation { get; }

    /// <summary>Divider position as a fraction (0..1) of the split's cross
    /// size. Persisted so a restored session keeps its layout; clamped to
    /// avoid a divider dragged flush against an edge.</summary>
    public double Ratio
    {
        get => _ratio;
        set => _ratio = Math.Clamp(value, 0.1, 0.9);
    }

    public PaneNode First
    {
        get => _first;
        set => Set(ref _first, value);
    }

    public PaneNode Second
    {
        get => _second;
        set => Set(ref _second, value);
    }
}
