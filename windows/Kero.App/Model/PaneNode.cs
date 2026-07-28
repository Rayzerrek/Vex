using System.Windows.Controls;
using Kero.App.Terminal;

namespace Kero.App.Model;

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
    private object? _view;

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public bool IsFocused
    {
        get => _isFocused;
        set => Set(ref _isFocused, value);
    }

    public object View => _view ??= CreateView();

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

    public TerminalPane(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        Title = "Terminal";
    }

    public override void Focus()
    {
        if (View is ITerminalView tv)
            tv.FocusTerminal();
    }

    protected override object CreateView()
    {
        var view = AppSettings.Instance.TerminalBackend == "xterm.js"
            ? new TerminalControl(_workingDirectory)
            : (ITerminalView)new Terminal.Native.NativeTerminalControl(_workingDirectory);
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
