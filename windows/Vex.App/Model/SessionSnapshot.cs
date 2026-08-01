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
}
