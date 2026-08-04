using System.Windows.Controls;
using System.Windows.Threading;
using Vex.App.Terminal;
using Vex.Terminal;

namespace Vex.App.Model;

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

    public bool IsFocused
    {
        get => _isFocused;
        set => Set(ref _isFocused, value);
    }

    public object View => _view ??= CreateView();

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
    /// Re-resolves the tab icon from the shared process snapshot, the shim's
    /// command line and the last raw OSC title; called by the tracker timer
    /// on the UI thread.
    /// </summary>
    public void RefreshAppIcon(IReadOnlyList<(uint Pid, uint ParentPid, string Name)> entries)
    {
        if (View is not ITerminalView terminal)
            return;
        if (terminal.ProcessId is not { } pid)
            return;
        var process = ProcessTree.DeepestDescendant(entries, (uint)pid, AppIconCatalog.ExcludedShells);
        if (process is not { } deepest)
            return;
        var commandLine = AppIconCatalog.IsShimHost(deepest.Name)
            ? ProcessCommandLine.Get(deepest.Pid)
            : null;
        if (AppIconCatalog.Resolve(deepest.Name, commandLine, _lastTitle) is { } icon)
            AppIcon = icon;
    }

    public override void Focus()
    {
        if (View is ITerminalView tv)
        {
            if (View is System.Windows.FrameworkElement { IsLoaded: true })
                tv.FocusTerminal();
            else
                // The view may not be in the tree yet (e.g. a brand-new tab);
                // focus once it is loaded so typing works immediately.
                Dispatcher.CurrentDispatcher.BeginInvoke(tv.FocusTerminal);
        }
    }

    protected override object CreateView()
    {
        var view = new Terminal.Native.NativeTerminalControl(_workingDirectory);
        _titleRawHandler = rawTitle => _lastTitle = rawTitle;
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
        view.ProcessExited += exitCode => RequestClose();
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
        if (_titleRawHandler is { } handler && View is Terminal.Native.NativeTerminalControl control)
            control.TitleRawChanged -= handler;
        base.Dispose();
    }
}

/// <summary>Two panes separated by a draggable splitter.</summary>
public sealed class SplitPane : PaneNode
{
    private PaneNode _first;
    private PaneNode _second;

    public SplitPane(Orientation orientation, PaneNode first, PaneNode second)
    {
        Orientation = orientation;
        _first = first;
        _second = second;
    }

    public Orientation Orientation { get; }

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
