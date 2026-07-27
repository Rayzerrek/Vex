namespace Kero.App.Model;

/// <summary>
/// One tab inside a project: a split tree of terminal panes, same role as
/// upstream's <c>PaneTab</c>.
/// </summary>
public sealed class WorkspaceTab : ObservableObject
{
    private string _title;
    private PaneNode _root;

    public WorkspaceTab(string title)
    {
        _title = title;
        _root = new LeafPane();
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public PaneNode Root
    {
        get => _root;
        set => Set(ref _root, value);
    }
}
