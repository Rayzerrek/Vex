using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;

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
                        workspace.Projects.Add(RestoreProject(projectSnapshot));

                    if (sessionSnapshot.SelectedProjectIndex is { } projectIndex &&
                        projectIndex >= 0 && projectIndex < workspace.Projects.Count)
                    {
                        workspace.SelectedProject = workspace.Projects[projectIndex];
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
            // A corrupt or old-format session must not block startup; fall
            // through to a single fresh project.
        }

        if (workspace.Projects.Count == 0)
            workspace.NewProject(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        return workspace;
    }

    /// <summary>Writes the whole workspace (projects, tabs, split tree and
    /// divider ratios) to disk. Best-effort: persistence must never crash the
    /// app, so any I/O error is swallowed.</summary>
    public static void Save(Workspace workspace)
    {
        try
        {
            var appSnapshot = new AppSnapshot { Windows = { Capture(workspace) } };
            var json = JsonSerializer.Serialize(appSnapshot, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            File.WriteAllText(SessionPath, json);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static SessionSnapshot Capture(Workspace workspace)
    {
        var session = new SessionSnapshot
        {
            SelectedProjectIndex = SafeIndex(workspace.Projects, workspace.SelectedProject),
        };

        foreach (var project in workspace.Projects)
        {
            var projectSnapshot = new ProjectSnapshot
            {
                Name = project.Name,
                WorkingDirectory = project.WorkingDirectory,
                SelectedTabIndex = SafeIndex(project.Tabs, project.SelectedTab),
            };

            foreach (var tab in project.Tabs)
            {
                projectSnapshot.Tabs.Add(new TabSnapshot
                {
                    Title = tab.Title,
                    HasCustomTitle = tab.HasCustomTitle,
                    Root = CapturePane(tab.Root),
                });
            }

            session.Projects.Add(projectSnapshot);
        }

        return session;
    }

    private static int? SafeIndex<T>(ObservableCollection<T> items, T? item)
    {
        if (item is null)
            return null;
        var index = items.IndexOf(item);
        return index < 0 ? null : index;
    }

    private static PaneSnapshot CapturePane(PaneNode node) => node switch
    {
        TerminalPane leaf => new TerminalPaneSnapshot { IsFocused = leaf.IsFocused },
        EditorPane leaf => new EditorPaneSnapshot { FilePath = leaf.FilePath, IsFocused = leaf.IsFocused },
        SplitPane split => new SplitPaneSnapshot
        {
            Orientation = split.Orientation == Orientation.Horizontal ? "Horizontal" : "Vertical",
            Ratio = split.Ratio,
            First = CapturePane(split.First),
            Second = CapturePane(split.Second),
        },
        _ => new TerminalPaneSnapshot(),
    };

    private static Project RestoreProject(ProjectSnapshot snapshot)
    {
        var tabs = new List<WorkspaceTab>();
        foreach (var tabSnapshot in snapshot.Tabs)
        {
            tabs.Add(new WorkspaceTab(
                tabSnapshot.Title,
                snapshot.WorkingDirectory,
                RestorePane(tabSnapshot.Root, snapshot.WorkingDirectory),
                tabSnapshot.HasCustomTitle));
        }

        if (tabs.Count == 0)
            tabs.Add(new WorkspaceTab("Terminal 1", snapshot.WorkingDirectory));

        var project = new Project(snapshot.Name, snapshot.WorkingDirectory, tabs);
        if (snapshot.SelectedTabIndex is { } tabIndex && tabIndex >= 0 && tabIndex < project.Tabs.Count)
            project.SelectedTab = project.Tabs[tabIndex];
        return project;
    }

    private static PaneNode RestorePane(PaneSnapshot snapshot, string workingDirectory) => snapshot switch
    {
        SplitPaneSnapshot split => new SplitPane(
            split.Orientation == "Vertical" ? Orientation.Vertical : Orientation.Horizontal,
            RestorePane(split.First, workingDirectory),
            RestorePane(split.Second, workingDirectory))
        {
            Ratio = split.Ratio,
        },
        EditorPaneSnapshot editor => new EditorPane(editor.FilePath) { IsFocused = editor.IsFocused },
        TerminalPaneSnapshot terminal => new TerminalPane(workingDirectory) { IsFocused = terminal.IsFocused },
        _ => new TerminalPane(workingDirectory),
    };
}
