using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;

namespace Vex.App.Model;

public static class SessionStore
{
    private static readonly string SessionPath = Path.Combine(AppProfile.DirectoryPath, "session.json");

    /// <summary>The workspace this process loaded at startup; app-wide
    /// services (e.g. icon refreshes) walk live
    /// panes through it instead of plumb­ing references through every view.</summary>
    public static Workspace Current { get; internal set; } = new();

    public static Workspace Load()
    {
        var workspace = new Workspace();
        Populate(workspace, ReadSnapshot());
        return workspace;
    }

    /// <summary>Reads the session file on a background thread and returns
    /// the snapshot data. Does not instantiate UI-bound model objects.</summary>
    public static Task<SessionSnapshot?> ReadSnapshotAsync()
    {
        return Task.Run(ReadSnapshot);
    }

    private static SessionSnapshot? ReadSnapshot()
    {
        try
        {
            return File.Exists(SessionPath) ? DeserializeSessionSnapshot(File.ReadAllBytes(SessionPath)) : null;
        }
        catch
        {
            // A corrupt or old-format session must not block startup.
            return null;
        }
    }

    /// <summary>Validates session JSON on the reader thread, before shell prewarm inspects projects.</summary>
    internal static SessionSnapshot? DeserializeSessionSnapshot(byte[] bytes)
    {
        var appSnapshot = JsonSerializer.Deserialize(bytes, VexJsonContext.Default.AppSnapshot);
        return SessionSnapshotRecovery.NormalizeSessionSnapshot(appSnapshot?.Windows?.FirstOrDefault(static window => window is not null));
    }

    /// <summary>Populates an existing workspace with the given snapshot on the UI thread.</summary>
    public static void Populate(Workspace workspace, SessionSnapshot? sessionSnapshot)
    {
        // Also accept callers that construct snapshots without the JSON reader.
        sessionSnapshot = SessionSnapshotRecovery.NormalizeSessionSnapshot(sessionSnapshot);
        if (sessionSnapshot != null)
        {
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
        
        // Linux Home is resolved after the window is shown, without briefly
        // starting a WSL terminal in the Windows user directory.
        if (workspace.Projects.Count == 0 && !ShellRegistry.IsWsl(AppSettings.Instance.ShellId))
            workspace.NewProject(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            
        Current = workspace;
    }

    /// <summary>Writes the whole workspace (projects, tabs, split tree and
    /// divider ratios) to disk. Best-effort: persistence must never crash the
    /// app, so any I/O error is swallowed.</summary>
    public static void Save(Workspace workspace)
    {
        try
        {
            var appSnapshot = new AppSnapshot { Windows = { Capture(workspace) } };
            var json = JsonSerializer.Serialize(appSnapshot, VexJsonContext.Default.AppSnapshot);
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            File.WriteAllText(SessionPath, json);
        }
        catch
        {
            // Best-effort only.
        }
    }

    internal static SessionSnapshot Capture(Workspace workspace)
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
                DefaultShellId = project.DefaultShellId,
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

    /// <summary>Prewarm must use the restored focused pane's own shell and directory, not the global preference.</summary>
    internal static (string Directory, string ShellId)? StartupTerminalLaunch(SessionSnapshot? snapshot, string globalShellId)
    {
        if (snapshot?.Projects is not { Count: > 0 } projects)
            return ShellRegistry.IsWsl(globalShellId) ? null : (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), globalShellId);
        var projectIndex = snapshot.SelectedProjectIndex is { } selected && selected >= 0 && selected < projects.Count ? selected : 0;
        var project = projects[projectIndex];
        var defaultShell = WslShellPaths.ShellIdForDirectory(ShellRegistry.ValidShellId(project.DefaultShellId) ?? globalShellId, project.WorkingDirectory);
        if (project.Tabs.Count == 0) return (project.WorkingDirectory, defaultShell);
        var tabIndex = project.SelectedTabIndex is { } tab && tab >= 0 && tab < project.Tabs.Count ? tab : 0;
        var panes = EnumerateSnapshotTerminals(project.Tabs[tabIndex].Root).ToArray();
        var pane = panes.FirstOrDefault(pane => pane.IsFocused) ?? panes.FirstOrDefault();
        var directory = pane?.WorkingDirectory is { } path && Path.IsPathFullyQualified(path) && !path.Any(char.IsControl) ? path : project.WorkingDirectory;
        return (directory, ShellRegistry.ValidShellId(pane?.ShellId) ?? defaultShell);
    }

    private static IEnumerable<TerminalPaneSnapshot> EnumerateSnapshotTerminals(PaneSnapshot? pane)
    {
        if (pane is TerminalPaneSnapshot terminal) yield return terminal;
        else if (pane is SplitPaneSnapshot split)
        {
            foreach (var first in EnumerateSnapshotTerminals(split.First)) yield return first;
            foreach (var second in EnumerateSnapshotTerminals(split.Second)) yield return second;
        }
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
        TerminalPane leaf => new TerminalPaneSnapshot { IsFocused = leaf.IsFocused, ShellId = leaf.ShellId, WorkingDirectory = leaf.WorkingDirectory },
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
        var defaultShellId = WslShellPaths.ShellIdForDirectory(
            ShellRegistry.ValidShellId(snapshot.DefaultShellId) ?? AppSettings.Instance.ShellId, snapshot.WorkingDirectory);
        var tabs = new List<WorkspaceTab>();
        foreach (var tabSnapshot in snapshot.Tabs)
        {
            tabs.Add(new WorkspaceTab(
                tabSnapshot.Title,
                snapshot.WorkingDirectory,
                RestorePane(tabSnapshot.Root, snapshot.WorkingDirectory, defaultShellId),
                tabSnapshot.HasCustomTitle, defaultShellId));
        }

        if (tabs.Count == 0)
            tabs.Add(new WorkspaceTab("Terminal 1", snapshot.WorkingDirectory, defaultShellId));

        var project = new Project(snapshot.Name, snapshot.WorkingDirectory, tabs, defaultShellId);
        if (snapshot.SelectedTabIndex is { } tabIndex && tabIndex >= 0 && tabIndex < project.Tabs.Count)
            project.SelectedTab = project.Tabs[tabIndex];
        return project;
    }

    private static PaneNode RestorePane(PaneSnapshot? snapshot, string workingDirectory, string defaultShellId) => snapshot switch
    {
        SplitPaneSnapshot split => new SplitPane(
            split.Orientation == "Vertical" ? Orientation.Vertical : Orientation.Horizontal,
            RestorePane(split.First, workingDirectory, defaultShellId),
            RestorePane(split.Second, workingDirectory, defaultShellId))
        {
            Ratio = double.IsFinite(split.Ratio) ? split.Ratio : 0.5,
        },
        LegacyEditorPaneSnapshot editor => new TerminalPane(workingDirectory, shellId: defaultShellId) { IsFocused = editor.IsFocused },
        TerminalPaneSnapshot terminal => new TerminalPane(
            terminal.WorkingDirectory is { } directory && Path.IsPathFullyQualified(directory) && !directory.Any(char.IsControl) ? directory : workingDirectory,
            shellId: ShellRegistry.ValidShellId(terminal.ShellId) ?? defaultShellId) { IsFocused = terminal.IsFocused },
        _ => new TerminalPane(workingDirectory, shellId: defaultShellId),
    };
}
