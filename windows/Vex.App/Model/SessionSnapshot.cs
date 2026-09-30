using System.Text.Json.Serialization;

namespace Vex.App.Model;

public sealed class AppSnapshot
{
    public List<SessionSnapshot> Windows { get; set; } = new();
}

public sealed class SessionSnapshot
{
    public List<ProjectSnapshot> Projects { get; set; } = new();
    public int? SelectedProjectIndex { get; set; }
}

public sealed class ProjectSnapshot
{
    public string Name { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public List<TabSnapshot> Tabs { get; set; } = new();
    public int? SelectedTabIndex { get; set; }
}

public sealed class TabSnapshot
{
    public string Title { get; set; } = "";
    public bool HasCustomTitle { get; set; }
    public PaneSnapshot Root { get; set; } = new TerminalPaneSnapshot();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TerminalPaneSnapshot), "terminal")]
[JsonDerivedType(typeof(LegacyEditorPaneSnapshot), "editor")]
[JsonDerivedType(typeof(SplitPaneSnapshot), "split")]
public class PaneSnapshot
{
}

public sealed class TerminalPaneSnapshot : PaneSnapshot
{
    public bool IsFocused { get; set; }
}

/// <summary>Reads old editor leaves so their layout and focus can migrate to terminal panes.</summary>
public sealed class LegacyEditorPaneSnapshot : PaneSnapshot
{
    public bool IsFocused { get; set; }
}

public sealed class SplitPaneSnapshot : PaneSnapshot
{
    public string Orientation { get; set; } = "Horizontal";
    public double Ratio { get; set; } = 0.5;
    public PaneSnapshot First { get; set; } = new TerminalPaneSnapshot();
    public PaneSnapshot Second { get; set; } = new TerminalPaneSnapshot();
}
