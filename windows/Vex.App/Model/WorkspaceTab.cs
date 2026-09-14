using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vex.App.Model;

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
    private bool _isFocusMode;
    private ImageSource? _preview;
    private bool _needsAttention;
    private bool _isActive;

    public WorkspaceTab(string title, string workingDirectory)
    {
        _title = title;
        WorkingDirectory = workingDirectory;
        _root = NewLeaf();
        _activeLeaf = (LeafPane)_root;
        _activeLeaf.IsFocused = true;
    }

    internal WorkspaceTab(string title, string workingDirectory, PaneNode root, bool hasCustomTitle)
    {
        _title = title;
        WorkingDirectory = workingDirectory;
        HasCustomTitle = hasCustomTitle;
        _root = root;
        
        foreach (var leaf in EnumerateLeaves(_root))
        {
            AttachLeafEvents(leaf);
            if (leaf.IsFocused)
                _activeLeaf = leaf;
        }

        if (_activeLeaf == null)
        {
            _activeLeaf = FirstLeaf();
            if (_activeLeaf != null)
                _activeLeaf.IsFocused = true;
        }
    }

    private void AttachLeafEvents(LeafPane leaf)
    {
        leaf.FocusRequested += () =>
        {
            if (_activeLeaf != leaf)
                ActiveLeaf = leaf;
        };
        leaf.SplitRequested += orientation =>
        {
            ActiveLeaf = leaf;
            Split(orientation);
        };
        leaf.NewTabRequested += () => NewTabRequested?.Invoke();
        leaf.PropertyChanged += OnLeafPropertyChanged;
        leaf.ProcessExited += OnLeafExited;
        leaf.BellRang += OnLeafBell;
    }

    /// <summary>Directory new panes in this tab start in.</summary>
    public string WorkingDirectory { get; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public bool HasCustomTitle { get; set; }

    /// <summary>Last rendered view of this tab, captured as it is left or
    /// immediately before Tab Peek opens. Null until the tab has been shown.</summary>
    public ImageSource? Preview
    {
        get => _preview;
        internal set => Set(ref _preview, value);
    }

    /// <summary>True when a pane in this tab asked for attention since the tab
    /// was last visible. Cleared automatically when the tab becomes active, so
    /// the marker survives switching away and never needs a manual reset.</summary>
    public bool NeedsAttention
    {
        get => _needsAttention;
        private set
        {
            if (Set(ref _needsAttention, value))
                OnPropertyChanged(nameof(ShowAttentionDot));
        }
    }

    /// <summary>Whether the tab strip draws the attention dot: a background tab
    /// that rang, or one whose process exited while the user was elsewhere.
    /// The active tab never shows it — the user is already looking there.</summary>
    public bool ShowAttentionDot => !IsActive && (NeedsAttention || ActiveLeaf?.State == PaneState.Exited);

    /// <summary>True while this tab is the active one in its project. Set by
    /// <see cref="Project.SelectedTab"/>; the tab strip uses it to render the
    /// attention marker as a dot only for background tabs.</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (!Set(ref _isActive, value))
                return;
            if (value)
                NeedsAttention = false;
            OnPropertyChanged(nameof(ShowAttentionDot));
        }
    }

    public PaneNode Root
    {
        get => _root;
        private set
        {
            if (Set(ref _root, value))
            {
                OnPropertyChanged(nameof(PaneCount));
                OnPropertyChanged(nameof(Leaves));
                OnPropertyChanged(nameof(DisplayRoot));
                LayoutChanged?.Invoke();
            }
        }
    }

    /// <summary>Number of active leaf panes in this tab.</summary>
    public int PaneCount => EnumerateLeaves(Root).Count();

    /// <summary>All leaf panes in the split tree of this tab.</summary>
    public IEnumerable<LeafPane> Leaves => EnumerateLeaves(Root);

    /// <summary>The tree currently rendered in the workspace. Focus mode
    /// displays only the active pane without changing the saved split layout.</summary>
    public PaneNode DisplayRoot => IsFocusMode && ActiveLeaf is { } active ? active : Root;

    public bool IsFocusMode
    {
        get => _isFocusMode;
        private set
        {
            if (Set(ref _isFocusMode, value))
                OnPropertyChanged(nameof(DisplayRoot));
        }
    }

    public void ToggleFocusMode()
    {
        IsFocusMode = !IsFocusMode;
        ActiveLeaf?.Focus();
    }

    /// <summary>Raised when panes are split or closed inside this tab.</summary>
    public event Action? LayoutChanged;

    /// <summary>The pane that last had focus; split and title derive from it.</summary>
    public LeafPane? ActiveLeaf
    {
        get => _activeLeaf;
        set
        {
            if (_activeLeaf == value)
                return;
            if (_activeLeaf is not null)
                _activeLeaf.IsFocused = false;
            _activeLeaf = value;
            if (IsFocusMode)
                OnPropertyChanged(nameof(DisplayRoot));
            OnPropertyChanged(nameof(ShowAttentionDot));
            if (value is not null)
            {
                value.IsFocused = true;
                if (!HasCustomTitle)
                    Title = value.Title + (value.IsDirty ? "*" : "");
                value.Focus();
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
    }

    private LeafPane NewLeaf()
    {
        var leaf = new TerminalPane(WorkingDirectory);
        AttachLeafEvents(leaf);
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
        }

        leaf.PropertyChanged -= OnLeafPropertyChanged;
        leaf.ProcessExited -= OnLeafExited;
        leaf.BellRang -= OnLeafBell;
        leaf.Dispose();
    }

    /// <summary>A pane rang its bell: mark the tab as wanting attention. The
    /// flag is only raised for background tabs; a visible tab has the user's
    /// eyes on it already, and the marker clears when it next activates.</summary>
    private void OnLeafBell(LeafPane leaf)
    {
        if (!IsActive)
            NeedsAttention = true;
    }

    private void OnLeafPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeafPane.State) && ReferenceEquals(sender, ActiveLeaf))
            OnPropertyChanged(nameof(ShowAttentionDot));

        if ((e.PropertyName == nameof(LeafPane.Title) || e.PropertyName == nameof(LeafPane.IsDirty)) 
            && _activeLeaf is { } active && ReferenceEquals(sender, active) && !HasCustomTitle)
            Title = active.Title + (active.IsDirty ? "*" : "");
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
        foreach (var leaf in EnumerateLeaves(Root))
        {
            leaf.PropertyChanged -= OnLeafPropertyChanged;
            leaf.BellRang -= OnLeafBell;
            leaf.Dispose();
        }
    }

    private static IEnumerable<LeafPane> EnumerateLeaves(PaneNode node)
    {
        switch (node)
        {
            case LeafPane leaf:
                yield return leaf;
                break;
            case SplitPane split:
                foreach (var leaf in EnumerateLeaves(split.First))
                    yield return leaf;
                foreach (var leaf in EnumerateLeaves(split.Second))
                    yield return leaf;
                break;
        }
    }
}
