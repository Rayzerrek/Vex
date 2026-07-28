using System.Text.Json.Serialization;
using System.Windows.Controls;

namespace Kero.App.Model;

public class AppSnapshot
{
    public List<SessionSnapshot> Windows { get; set; } = new();
}

public class SessionSnapshot
{
    public List<ProjectSnapshot> Projects { get; set; } = new();
    public int? SelectedProjectIndex { get; set; }
}

public class ProjectSnapshot
{
    public string Name { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public List<TabSnapshot> Tabs { get; set; } = new();
    public int? SelectedTabIndex { get; set; }
}

public class TabSnapshot
{
    public string Title { get; set; } = "";
    public bool HasCustomTitle { get; set; }
    public PaneNodeSnapshot Root { get; set; } = default!;
}

[JsonPolymorphic]
[JsonDerivedType(typeof(SplitPaneSnapshot), typeDiscriminator: "split")]
[JsonDerivedType(typeof(TerminalPaneSnapshot), typeDiscriminator: "terminal")]
[JsonDerivedType(typeof(EditorPaneSnapshot), typeDiscriminator: "editor")]
public abstract class PaneNodeSnapshot { }

public class SplitPaneSnapshot : PaneNodeSnapshot
{
    public Orientation Orientation { get; set; }
    public PaneNodeSnapshot First { get; set; } = default!;
    public PaneNodeSnapshot Second { get; set; } = default!;
}

public class TerminalPaneSnapshot : PaneNodeSnapshot
{
    public string WorkingDirectory { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsFocused { get; set; }
}

public class EditorPaneSnapshot : PaneNodeSnapshot
{
    public string FilePath { get; set; } = "";
    public bool IsFocused { get; set; }
}
