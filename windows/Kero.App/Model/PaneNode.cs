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
    private TerminalControl? _view;
    private string _title = "Terminal";

    public LeafPane(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public TerminalControl View => _view ??= CreateView();

    private TerminalControl CreateView()
    {
        var view = new TerminalControl(_workingDirectory);
        view.TitleChanged += title =>
        {
            if (!string.IsNullOrWhiteSpace(title))
                Title = title;
        };
        return view;
    }

    public void Dispose() => _view?.Dispose();
}

/// <summary>Two panes separated by a draggable splitter.</summary>
public sealed class SplitPane : PaneNode
{
    public SplitPane(Orientation orientation, PaneNode first, PaneNode second)
    {
        Orientation = orientation;
        First = first;
        Second = second;
    }

    public Orientation Orientation { get; }

    public PaneNode First { get; }

    public PaneNode Second { get; }
}
