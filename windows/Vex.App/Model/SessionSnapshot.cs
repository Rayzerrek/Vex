using System.Text.Json.Serialization;

namespace Vex.App.Model;

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
    public PaneSnapshot Root { get; set; } = new TerminalPaneSnapshot();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TerminalPaneSnapshot), "terminal")]
[JsonDerivedType(typeof(EditorPaneSnapshot), "editor")]
[JsonDerivedType(typeof(SplitPaneSnapshot), "split")]
public class PaneSnapshot
{
}

public class TerminalPaneSnapshot : PaneSnapshot
{
    public bool IsFocused { get; set; }
}

public class EditorPaneSnapshot : PaneSnapshot
{
    public string FilePath { get; set; } = "";
    public bool IsFocused { get; set; }
}

public class SplitPaneSnapshot : PaneSnapshot
{
    public string Orientation { get; set; } = "Horizontal";
    public double Ratio { get; set; } = 0.5;
    public PaneSnapshot First { get; set; } = new TerminalPaneSnapshot();
    public PaneSnapshot Second { get; set; } = new TerminalPaneSnapshot();
}
