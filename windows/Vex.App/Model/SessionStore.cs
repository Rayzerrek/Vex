using System.IO;
using System.Text.Json;
using System.Windows.Controls;

namespace Vex.App.Model;

public static class SessionStore
{
    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vex", "session.json");

    public static void Save(Workspace workspace)
    {
        try
        {
            var appSnapshot = new AppSnapshot();
            var sessionSnapshot = new SessionSnapshot();

            foreach (var project in workspace.Projects)
            {
                var projectSnapshot = new ProjectSnapshot
                {
                    Name = project.Name,
                    WorkingDirectory = project.WorkingDirectory,
                    SelectedTabIndex = project.SelectedTab != null ? project.Tabs.IndexOf(project.SelectedTab) : null
                };

                foreach (var tab in project.Tabs)
                {
                    var tabSnapshot = new TabSnapshot
                    {
                        Title = tab.Title,
                        HasCustomTitle = tab.HasCustomTitle,
                        Root = CreateNodeSnapshot(tab.Root)
                    };
                    projectSnapshot.Tabs.Add(tabSnapshot);
                }
                sessionSnapshot.Projects.Add(projectSnapshot);
            }

            sessionSnapshot.SelectedProjectIndex = workspace.SelectedProject != null ? workspace.Projects.IndexOf(workspace.SelectedProject) : null;
            appSnapshot.Windows.Add(sessionSnapshot);

            var dir = Path.GetDirectoryName(SessionPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(appSnapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SessionPath, json);
        }
        catch
        {
            // Ignore errors
        }
    }

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
                        var project = new Project(projectSnapshot);
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

    private static PaneNodeSnapshot CreateNodeSnapshot(PaneNode node)
    {
        return node switch
        {
            SplitPane split => new SplitPaneSnapshot
            {
                Orientation = split.Orientation,
                First = CreateNodeSnapshot(split.First),
                Second = CreateNodeSnapshot(split.Second)
            },
            EditorPane editor => new EditorPaneSnapshot
            {
                FilePath = editor.FilePath,
                IsFocused = editor.IsFocused
            },
            TerminalPane terminal => new TerminalPaneSnapshot
            {
                Title = terminal.Title,
                IsFocused = terminal.IsFocused,
                WorkingDirectory = "Terminal Working Directory is inside backend, fallback not needed for now"
            },
            _ => throw new NotImplementedException()
        };
    }

    public static PaneNode RestoreNode(PaneNodeSnapshot snapshot)
    {
        return snapshot switch
        {
            SplitPaneSnapshot split => new SplitPane(split.Orientation, RestoreNode(split.First), RestoreNode(split.Second)),
            EditorPaneSnapshot editor => new EditorPane(editor.FilePath) { IsFocused = editor.IsFocused },
            TerminalPaneSnapshot terminal => new TerminalPane(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { Title = terminal.Title, IsFocused = terminal.IsFocused },
            _ => throw new NotImplementedException()
        };
    }
}
