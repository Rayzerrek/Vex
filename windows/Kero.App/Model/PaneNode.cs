using System.Windows.Controls;

namespace Kero.App.Model;

/// <summary>
/// One node of a tab's binary split tree. Mirrors upstream's pane model:
/// every split replaces a leaf with a <see cref="SplitPane"/> holding the
/// original leaf and the new one.
/// </summary>
public abstract class PaneNode : ObservableObject
{
}

/// <summary>A leaf holding a single terminal.</summary>
public sealed class LeafPane : PaneNode
{
    private string _title = "Terminal";

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }
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
