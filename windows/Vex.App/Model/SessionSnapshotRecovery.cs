namespace Vex.App.Model;

/// <summary>Repairs nullable session data before prewarm or UI restoration uses it.</summary>
internal static class SessionSnapshotRecovery
{
    internal static SessionSnapshot? NormalizeSessionSnapshot(SessionSnapshot? snapshot)
    {
        if (snapshot is null)
            return null;

        snapshot.Projects ??= new();
        snapshot.SelectedProjectIndex = RemoveNullSnapshots(snapshot.Projects, snapshot.SelectedProjectIndex);
        foreach (var project in snapshot.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.WorkingDirectory))
                project.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            project.Name ??= "Project";
            project.Tabs ??= new();
            project.SelectedTabIndex = RemoveNullSnapshots(project.Tabs, project.SelectedTabIndex);
            foreach (var tab in project.Tabs)
                tab.Title ??= "Terminal";
        }
        return snapshot;
    }

    private static int? RemoveNullSnapshots<T>(List<T> snapshots, int? selectedIndex) where T : class
    {
        var selected = selectedIndex is { } index && index >= 0 && index < snapshots.Count
            ? snapshots[index]
            : null;
        var removed = snapshots.RemoveAll(static snapshot => snapshot is null);
        if (selected is null)
            return null;
        // Valid snapshots keep their lists and indices; allocate only when
        // damaged data actually needs a replacement collection.
        return removed == 0 ? selectedIndex : snapshots.IndexOf(selected);
    }
}
