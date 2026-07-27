using System.ComponentModel;
using System.Windows.Controls;

namespace Kero.App.Model;

/// <summary>
/// One tab inside a project: a split tree of terminal panes, same role as
/// upstream's <c>PaneTab</c>. Splitting replaces the focused leaf with a
/// <see cref="SplitPane"/> holding the original leaf and a fresh one.
/// </summary>
public sealed class WorkspaceTab : ObservableObject, IDisposable
{
    private string _title;
    private PaneNode _root;
    private LeafPane? _activeLeaf;

    public WorkspaceTab(string title, string workingDirectory)
    {
        _title = title;
        WorkingDirectory = workingDirectory;
        _root = NewLeaf();
        _activeLeaf = (LeafPane)_root;
        _activeLeaf.IsFocused = true;
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Directory new panes in this tab start in.</summary>
    public string WorkingDirectory { get; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public bool HasCustomTitle { get; set; }

    public PaneNode Root
    {
        get => _root;
        private set => Set(ref _root, value);
    }

    /// <summary>The pane that last had focus; split and title derive from it.</summary>
    public LeafPane? ActiveLeaf
    {
        get => _activeLeaf;
        private set
        {
            if (_activeLeaf == value)
                return;
            if (_activeLeaf is not null)
                _activeLeaf.IsFocused = false;
            _activeLeaf = value;
            if (value is not null)
            {
                value.IsFocused = true;
                if (!HasCustomTitle)
                    Title = value.Title;
            }
        }
    }

    /// <summary>Raised when a pane in this tab asks for a new tab.</summary>
    public event Action? NewTabRequested;
    
    /// <summary>Raised when the last pane in this tab closes.</summary>
    public event Action<WorkspaceTab>? TabClosedRequested;

    public void Split(Orientation orientation)
    {
        var target = ActiveLeaf ?? FirstLeaf();
        if (target is null)
            return;
        var fresh = NewLeaf();
        Root = ReplaceNode(Root, target, new SplitPane(orientation, target, fresh));
        ActiveLeaf = fresh;
        fresh.View.FocusTerminal();
    }

    private LeafPane NewLeaf()
    {
        var leaf = new LeafPane(WorkingDirectory);
        leaf.FocusRequested += () => ActiveLeaf = leaf;
        leaf.SplitRequested += orientation =>
        {
            ActiveLeaf = leaf;
            Split(orientation);
        };
        leaf.NewTabRequested += () => NewTabRequested?.Invoke();
        leaf.PropertyChanged += OnLeafPropertyChanged;
        leaf.ProcessExited += OnLeafExited;
        return leaf;
    }

    private void OnLeafExited(LeafPane leaf)
    {
        if (ReferenceEquals(Root, leaf))
        {
            TabClosedRequested?.Invoke(this);
            return;
        }

        Root = RemoveNode(Root, leaf);
        if (ReferenceEquals(ActiveLeaf, leaf))
        {
            ActiveLeaf = FirstLeaf();
            ActiveLeaf?.View.FocusTerminal();
        }

        leaf.PropertyChanged -= OnLeafPropertyChanged;
        leaf.ProcessExited -= OnLeafExited;
        leaf.Dispose();
    }

    private void OnLeafPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeafPane.Title) && _activeLeaf is { } active && ReferenceEquals(sender, active) && !HasCustomTitle)
            Title = active.Title;
    }

    private LeafPane? FirstLeaf() => Root switch
    {
        LeafPane leaf => leaf,
        SplitPane split => FirstLeafOf(split),
        _ => null,
    };

    private static LeafPane FirstLeafOf(SplitPane split) => split.First switch
    {
        LeafPane leaf => leaf,
        SplitPane nested => FirstLeafOf(nested),
        _ => FirstLeafOf((SplitPane)split.Second),
    };

    private static PaneNode ReplaceNode(PaneNode node, PaneNode old, PaneNode replacement)
    {
        if (ReferenceEquals(node, old))
            return replacement;
        if (node is SplitPane split)
        {
            split.First = ReplaceNode(split.First, old, replacement);
            split.Second = ReplaceNode(split.Second, old, replacement);
        }
        return node;
    }

    private static PaneNode RemoveNode(PaneNode node, PaneNode toRemove)
    {
        if (node is SplitPane split)
        {
            if (ReferenceEquals(split.First, toRemove))
                return split.Second;
            if (ReferenceEquals(split.Second, toRemove))
                return split.First;

            split.First = RemoveNode(split.First, toRemove);
            split.Second = RemoveNode(split.Second, toRemove);
        }
        return node;
    }

    public void Dispose()
    {
        foreach (var leaf in Leaves(Root))
        {
            leaf.PropertyChanged -= OnLeafPropertyChanged;
            leaf.Dispose();
        }
    }

    private static IEnumerable<LeafPane> Leaves(PaneNode node)
    {
        switch (node)
        {
            case LeafPane leaf:
                yield return leaf;
                break;
            case SplitPane split:
                foreach (var leaf in Leaves(split.First))
                    yield return leaf;
                foreach (var leaf in Leaves(split.Second))
                    yield return leaf;
                break;
        }
    }
}
