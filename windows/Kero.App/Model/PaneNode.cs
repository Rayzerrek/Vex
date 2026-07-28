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

/// <summary>
/// A leaf holding a single terminal. Owns its <see cref="TerminalControl"/>
/// so the surface (and its PTY) survives the pane being reparented.
/// </summary>
public sealed class LeafPane : PaneNode, IDisposable
{
    private readonly string _workingDirectory;
    private ITerminalView? _view;
    private string _title = "Terminal";
    private bool _isFocused;

    public LeafPane(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    /// <summary>Raised when the user clicks into or focuses this pane's surface.</summary>
    public event Action? FocusRequested;

    /// <summary>Raised when this pane asks to be split in the given orientation.</summary>
    public event Action<Orientation>? SplitRequested;

    /// <summary>Raised when this pane asks for a new tab.</summary>
    public event Action? NewTabRequested;

    /// <summary>Raised when the process running in the terminal exits.</summary>
    public event Action<LeafPane>? ProcessExited;

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    /// <summary>Last-focused pane of its tab; drives the tab's accent border.</summary>
    public bool IsFocused
    {
        get => _isFocused;
        set => Set(ref _isFocused, value);
    }

    public ITerminalView View => _view ??= CreateView();

    private ITerminalView CreateView()
    {
        // Picked at pane creation; switching the setting does not rebuild
        // live terminals, matching how shell changes apply to new sessions.
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
        view.FocusGained += () => FocusRequested?.Invoke();
        view.ProcessExited += exitCode => ProcessExited?.Invoke(this);
        view.CommandRequested += command =>
        {
            switch (command)
            {
                case TerminalCommand.SplitRight:
                    SplitRequested?.Invoke(Orientation.Horizontal);
                    break;
                case TerminalCommand.SplitDown:
                    SplitRequested?.Invoke(Orientation.Vertical);
                    break;
                case TerminalCommand.NewTab:
                    NewTabRequested?.Invoke();
                    break;
                case TerminalCommand.ClosePane:
                    ProcessExited?.Invoke(this);
                    break;
            }
        };
        return view;
    }

    public void Dispose() => _view?.Dispose();
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
