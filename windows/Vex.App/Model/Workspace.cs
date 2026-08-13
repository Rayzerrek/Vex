using System.Collections.ObjectModel;
using System.IO;

namespace Vex.App.Model;

/// <summary>
/// Root of one window's state: the project list and the selection. The
/// Windows analogue of upstream's <c>TerminalManager</c>.
/// </summary>
public sealed class Workspace : ObservableObject
{
    private Project? _selectedProject;
    private int _projectCounter;

    public Workspace()
    {
        // Projects are added by the caller (SessionStore.Load falls back to a
        // fresh project). Not creating one here avoids building a throwaway
        // terminal pane that would linger in AppIconTracker.
    }

    public ObservableCollection<Project> Projects { get; } = new();

    public Project? SelectedProject
    {
        get => _selectedProject;
        set => Set(ref _selectedProject, value);
    }

    public Project NewProject(string workingDirectory)
    {
        _projectCounter++;
        var name = DirectoryName(workingDirectory) ?? $"Project {_projectCounter}";
        var project = new Project(name, workingDirectory);
        Projects.Add(project);
        SelectedProject = project;
        return project;
    }

    public void CloseProject(Project project)
    {
        var index = Projects.IndexOf(project);
        if (!Projects.Remove(project))
            return;
        if (SelectedProject == project)
            SelectedProject = Projects.Count > 0 ? Projects[Math.Max(0, index - 1)] : null;
    }

    private static string? DirectoryName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? null : name;
    }
}
