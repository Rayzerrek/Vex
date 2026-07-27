using System.Collections.ObjectModel;

namespace Kero.App.Model;

/// <summary>
/// A project groups tabs and appears as one row in the left sidebar, exactly
/// like upstream's <c>Project</c>. The working directory anchors new
/// terminals, the file tree, and the git panel.
/// </summary>
public sealed class Project : ObservableObject
{
    private string _name;
    private WorkspaceTab? _selectedTab;

    private FileTreeNode? _root;

    public Project(string name, string workingDirectory)
    {
        _name = name;
        WorkingDirectory = workingDirectory;
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
        set => Set(ref _selectedTab, value);
    }

    public WorkspaceTab NewTab()
    {
        var tab = CreateTab($"Terminal {Tabs.Count + 1}");
        SelectedTab = tab;
        return tab;
    }

    private WorkspaceTab CreateTab(string title)
    {
        var tab = new WorkspaceTab(title, WorkingDirectory);
        tab.NewTabRequested += () => NewTab();
        Tabs.Add(tab);
        return tab;
    }

    public void CloseTab(WorkspaceTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (!Tabs.Remove(tab))
            return;
        tab.Dispose();
        if (SelectedTab == tab)
            SelectedTab = Tabs.Count > 0 ? Tabs[Math.Max(0, index - 1)] : null;
    }
}
