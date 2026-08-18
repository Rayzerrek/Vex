using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows.Threading;

namespace Vex.App.Model;

/// <summary>
/// A project groups tabs and appears as one row in the left sidebar, exactly
/// like upstream's <c>Project</c>. Each tab is a "deck" — a split layout of
/// terminal panes — so splitting always happens inside a tab, never between
/// tabs. The working directory anchors new terminals, the file tree, and the
/// git panel.
/// </summary>
public sealed class Project : ObservableObject
{
    public const int MaxTabs = 8;
    private string _name;
    private string? _gitBranch;
    private WorkspaceTab? _selectedTab;

    private FileTreeNode[] _treeRoots = Array.Empty<FileTreeNode>();
    private FileTreeNode? _rootTree;

    public bool CanCreateTab => Tabs.Count < MaxTabs;

    /// <summary>The active git branch in WorkingDirectory, or null if not a repository.</summary>
    public string? GitBranch
    {
        get => _gitBranch;
        private set => Set(ref _gitBranch, value);
    }

    public Project(string name, string workingDirectory)
    {
        _name = name;
        WorkingDirectory = workingDirectory;
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanCreateTab));
        var tab = CreateTab("Terminal 1");
        _selectedTab = tab;
    }

    /// <summary>Restores a project from a saved session; tabs are wired up
    /// like freshly created ones.</summary>
    internal Project(string name, string workingDirectory, IEnumerable<WorkspaceTab> tabs)
    {
        _name = name;
        WorkingDirectory = workingDirectory;
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanCreateTab));
        foreach (var tab in tabs)
        {
            WireTabEvents(tab);
            Tabs.Add(tab);
        }
        _selectedTab = Tabs.FirstOrDefault();
    }

    private void WireTabEvents(WorkspaceTab tab)
    {
        tab.NewTabRequested += () => NewTab();
        tab.TabClosedRequested += t => CloseTab(t);
    }

    private int _gitBranchVersion;

    public void RefreshGitBranch() => _ = RefreshGitBranchAsync();

    public async Task RefreshGitBranchAsync()
    {
        var dir = WorkingDirectory;
        if (string.IsNullOrEmpty(dir))
        {
            GitBranch = null;
            return;
        }

        // Drop stale completions: a newer refresh supersedes an older one,
        // so an in-flight read must not overwrite the freshest result.
        var version = ++_gitBranchVersion;
        var branch = await Task.Run(() => ResolveGitBranch(dir));
        if (version == _gitBranchVersion)
            GitBranch = branch;
    }

    private static string? ResolveGitBranch(string workingDirectory)
    {
        try
        {
            if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
                return null;

            var gitPath = Path.Combine(workingDirectory, ".git");
            string headPath;
            if (File.Exists(gitPath))
            {
                // Submodule or git worktree file: "gitdir: /path/to/real/gitdir"
                var line = File.ReadAllLines(gitPath).FirstOrDefault()?.Trim();
                if (line != null && line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                {
                    var target = line.Substring(7).Trim();
                    var realGitDir = Path.IsPathRooted(target)
                        ? target
                        : Path.GetFullPath(Path.Combine(workingDirectory, target));
                    headPath = Path.Combine(realGitDir, "HEAD");
                }
                else
                {
                    return null;
                }
            }
            else if (Directory.Exists(gitPath))
            {
                headPath = Path.Combine(gitPath, "HEAD");
            }
            else
            {
                return null;
            }

            if (!File.Exists(headPath))
                return null;

            var headContent = File.ReadAllText(headPath).Trim();
            if (headContent.StartsWith("ref: refs/heads/", StringComparison.OrdinalIgnoreCase))
                return headContent.Substring("ref: refs/heads/".Length).Trim();

            // Detached HEAD: short commit SHA
            return headContent.Length > 7 ? headContent.Substring(0, 7) : headContent;
        }
        catch
        {
            return null;
        }
    }

    public FileTreeNode Root
    {
        get
        {
            if (_rootTree is null && !string.IsNullOrEmpty(WorkingDirectory))
                RefreshFileTree();
            return _rootTree!;
        }
        private set
        {
            if (Set(ref _rootTree, value))
            {
                _treeRoots = new[] { value };
                OnPropertyChanged(nameof(TreeRoots));
            }
        }
    }

    public FileTreeNode[] TreeRoots
        => _treeRoots;

    /// <summary>Creates the root only after the file tree becomes visible.
    /// Enumerating a large working directory must not delay the first frame.</summary>
    public void EnsureFileTree()
    {
        if (_rootTree is null && !string.IsNullOrEmpty(WorkingDirectory))
            RefreshFileTree();
    }

    public void RefreshFileTree()
    {
        Root = new FileTreeNode(WorkingDirectory, true, Name);
        Root.IsExpanded = true;
        RefreshGitBranch();
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string WorkingDirectory { get; }

    public ObservableCollection<WorkspaceTab> Tabs { get; } = new();

    public WorkspaceTab? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (Set(ref _selectedTab, value))
            {
                // A new window/tab should be ready to type into immediately;
                // focus its active pane once it's rendered.
                if (value is not null)
                    Dispatcher.CurrentDispatcher.BeginInvoke(() => value.ActiveLeaf?.Focus());
            }
        }
    }

    public WorkspaceTab? NewTab()
    {
        if (!CanCreateTab)
            return null;

        var tab = CreateTab($"Terminal {Tabs.Count + 1}");
        if (tab != null)
            SelectedTab = tab;
        return tab;
    }

    private WorkspaceTab? CreateTab(string title)
    {
        if (!CanCreateTab)
            return null;

        var tab = new WorkspaceTab(title, WorkingDirectory);
        WireTabEvents(tab);
        Tabs.Add(tab);
        return tab;
    }

    public void OpenFile(string filePath)
    {
        var existingTab = Tabs.FirstOrDefault(t => t.ActiveLeaf is EditorPane ep && ep.FilePath == filePath);
        if (existingTab != null)
        {
            SelectedTab = existingTab;
            return;
        }

        if (!CanCreateTab)
            return;

        var fileName = System.IO.Path.GetFileName(filePath);
        var editorPane = new EditorPane(filePath);
        var tab = new WorkspaceTab(fileName, WorkingDirectory, editorPane, hasCustomTitle: false);
        WireTabEvents(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    /// <summary>Moves a tab to a new position in the tab bar.</summary>
    public void MoveTab(WorkspaceTab tab, int newIndex)
    {
        var oldIndex = Tabs.IndexOf(tab);
        if (oldIndex < 0)
            return;
        newIndex = Math.Clamp(newIndex, 0, Tabs.Count - 1);
        if (newIndex == oldIndex)
            return;
        Tabs.Move(oldIndex, newIndex);
    }

    public void CloseTab(WorkspaceTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        // Always keep at least one tab (Ghostty behaviour): the last tab
        // cannot be closed, it just stays.
        if (Tabs.Count <= 1)
            return;

        bool wasSelected = (SelectedTab == tab);
        WorkspaceTab? nextTab = null;

        if (wasSelected)
            nextTab = Tabs[Math.Max(0, index - 1)];

        Tabs.Remove(tab);
        tab.Dispose();

        if (wasSelected)
            SelectedTab = nextTab;
    }
}
