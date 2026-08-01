using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace Vex.App.Model;

/// <summary>
/// A project groups tabs and appears as one row in the left sidebar, exactly
/// like upstream's <c>Project</c>. The working directory anchors new
/// terminals, the file tree, and the git panel.
/// </summary>
public sealed class Project : ObservableObject
{
    public const int MaxTabs = 30;
    private string _name;
    private WorkspaceTab? _selectedTab;

    private FileTreeNode? _root;

    public bool CanCreateTab => Tabs.Count < MaxTabs;

    public Project(string name, string workingDirectory)
    {
        _name = name;
        WorkingDirectory = workingDirectory;
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanCreateTab));
        var tab = CreateTab("Terminal 1");
        _selectedTab = tab;
        RefreshFileTree();
    }

    public FileTreeNode Root
    {
        get => _root!;
        private set
        {
            if (Set(ref _root, value))
            {
                OnPropertyChanged(nameof(TreeRoots));
            }
        }
    }

    public FileTreeNode[] TreeRoots => new[] { Root };

    public void RefreshFileTree()
    {
        Root = new FileTreeNode(WorkingDirectory, true, Name);
        Root.IsExpanded = true;
    }

    public Guid Id { get; } = Guid.NewGuid();

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
        tab.NewTabRequested += () => NewTab();
        tab.TabClosedRequested += t => CloseTab(t);
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
        tab.NewTabRequested += () => NewTab();
        tab.TabClosedRequested += t => CloseTab(t);
        Tabs.Add(tab);
        SelectedTab = tab;
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
