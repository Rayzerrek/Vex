using System.IO;
using System.Text.Json;

namespace Vex.App.Model;

public static class SessionStore
{
    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vex", "session.json");

    public static Workspace Load()
    {
        var workspace = new Workspace();
        workspace.Projects.Clear();

        try
        {
            if (File.Exists(SessionPath))
            {
                var json = File.ReadAllText(SessionPath);
                var appSnapshot = JsonSerializer.Deserialize<AppSnapshot>(json);
                if (appSnapshot?.Windows.Count > 0)
                {
                    var sessionSnapshot = appSnapshot.Windows[0];
                    foreach (var projectSnapshot in sessionSnapshot.Projects)
                    {
                        // Tabs are never restored: every project opens with a
                        // single fresh terminal. Users add more themselves.
                        var project = new Project(projectSnapshot.Name, projectSnapshot.WorkingDirectory);
                        workspace.Projects.Add(project);
                    }
                    if (sessionSnapshot.SelectedProjectIndex.HasValue && sessionSnapshot.SelectedProjectIndex.Value >= 0 && sessionSnapshot.SelectedProjectIndex.Value < workspace.Projects.Count)
                    {
                        workspace.SelectedProject = workspace.Projects[sessionSnapshot.SelectedProjectIndex.Value];
                    }
                    else if (workspace.Projects.Count > 0)
                    {
                        workspace.SelectedProject = workspace.Projects[0];
                    }
                }
            }
        }
        catch
        {
            // Ignore errors, return empty
        }
        
        if (workspace.Projects.Count == 0)
        {
            workspace.NewProject(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        return workspace;
    }
}
