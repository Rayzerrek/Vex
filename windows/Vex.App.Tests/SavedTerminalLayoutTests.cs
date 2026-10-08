using System.Text;
using System.Windows.Controls;
using Vex.App.Model;
using Vex.Libghostty;
using Xunit;

namespace Vex.App.Tests;

public sealed class SavedTerminalLayoutTests
{
    [Fact]
    public void SavedLayout_AcceptsAnUnbalancedTreeOf32PanesAndRejects33()
    {
        using var tab = new WorkspaceTab("Development", Environment.CurrentDirectory);
        for (var split = 0; split < 31; split++) tab.Split(Orientation.Horizontal);
        var layout = SavedTerminalLayouts.CaptureLayout(tab, Environment.CurrentDirectory);
        Assert.True(SavedTerminalLayouts.ValidateLayout(layout));
        Assert.Equal(32, SavedTerminalLayouts.EnumerateLayoutPanes(layout.Root).Count());
        tab.Split(Orientation.Horizontal);
        Assert.False(SavedTerminalLayouts.ValidateLayout(SavedTerminalLayouts.CaptureLayout(tab, Environment.CurrentDirectory)));
    }

    [Fact]
    public void AggregateStatus_IdentifiesPaneAndUpdatesAfterTabReorder()
    {
        using var project = new Project("Project", Environment.CurrentDirectory);
        var background = project.SelectedTab!;
        background.Split(Orientation.Horizontal);
        var blocked = background.ActiveLeaf!;
        blocked.ProgramStatus = new(ProgramStatusState.Blocked, Message: "Approve this operation");
        project.NewTab();
        Assert.Equal("Pane 2", background.ProgramStatusOrigin);
        Assert.Equal("Tab 1 · Pane 2", project.ProgramStatusOrigin);
        project.MoveTab(background, 1);
        Assert.Equal("Tab 2 · Pane 2", project.ProgramStatusOrigin);
        background.Leaves.First().ProgramStatus = blocked.ProgramStatus;
        Assert.Equal("Pane 1", background.ProgramStatusOrigin);
        Assert.Equal("Tab 2 · Pane 1", project.ProgramStatusOrigin);
    }

    [Fact]
    public void SavedLayout_PreservesSplitsFocusCommandsAndRelativeDirectories()
    {
        var directory = Environment.CurrentDirectory;
        var first = new TerminalPane(directory, "echo first");
        var second = new TerminalPane(directory, "echo second") { IsFocused = true };
        using var tab = new WorkspaceTab("Development", directory,
            new SplitPane(Orientation.Vertical, first, second) { Ratio = 0.3 }, hasCustomTitle: true);
        var layout = SavedTerminalLayouts.CaptureLayout(tab, directory);
        Assert.True(SavedTerminalLayouts.ValidateLayout(layout));
        Assert.Equal(".", layout.Root.First!.Directory);
        using var project = new Project("Project", directory);
        var opened = project.OpenSavedLayout(layout)!;
        var root = Assert.IsType<SplitPane>(opened.Root);
        Assert.Equal(Orientation.Vertical, root.Orientation);
        Assert.Equal(0.3, root.Ratio);
        Assert.Equal("echo second", Assert.IsType<TerminalPane>(opened.ActiveLeaf).InitialCommand);
        Assert.All(opened.Leaves, leaf => Assert.Null(Assert.IsType<TerminalPane>(leaf).ProcessId));
    }

    [Fact]
    public void SessionRestore_DoesNotReplayLayoutCommands()
    {
        var directory = Environment.CurrentDirectory;
        var workspace = new Workspace();
        var project = workspace.NewProject(directory);
        try
        {
            project.OpenSavedLayout(new SavedTerminalLayout { Name = "Build", Root = new() { Command = "echo must-not-replay" } });
            var snapshot = SessionStore.Capture(workspace);
            var restored = new Workspace();
            SessionStore.Populate(restored, snapshot);
            try
            {
                Assert.All(restored.Projects.SelectMany(p => p.Tabs).SelectMany(t => t.Leaves),
                    leaf => Assert.Null(Assert.IsType<TerminalPane>(leaf).InitialCommand));
            }
            finally { foreach (var restoredProject in restored.Projects) restoredProject.Dispose(); }
        }
        finally { project.Dispose(); }
    }

    [Fact]
    public void InvalidSavedLayouts_AreRemovedWithoutDiscardingOtherSettings()
    {
        var settings = AppSettings.DeserializeSettings(Encoding.UTF8.GetBytes("""
            { "FontSize": 16, "SavedLayouts": [null,
              { "Name": "Invalid", "Root": { "Orientation": "Diagonal" } },
              { "Name": "Valid", "Root": { "Command": "echo ok" } },
              { "Name": "valid", "Root": {} }] }
            """));
        Assert.Equal(16, settings.FontSize);
        Assert.Equal("Valid", Assert.Single(settings.SavedLayouts).Name);
    }

    [Fact]
    public void MissingDirectory_DoesNotAddPartiallyStartedLayout()
    {
        using var project = new Project("Project", Environment.CurrentDirectory);
        var layout = new SavedTerminalLayout { Name = "Missing", Root = new()
        {
            Orientation = "Horizontal", First = new() { Command = "echo first" },
            Second = new() { Directory = Guid.NewGuid().ToString() },
        }};
        Assert.Throws<System.IO.DirectoryNotFoundException>(() => project.OpenSavedLayout(layout));
        Assert.Single(project.Tabs);
    }

    [Fact]
    public void TabStatus_AggregatesAnUnfocusedPaneAndSurvivesFocusMode()
    {
        using var tab = new WorkspaceTab("Terminal", Environment.CurrentDirectory);
        var first = tab.ActiveLeaf!;
        tab.Split(Orientation.Horizontal);
        first.ProgramStatus = new(ProgramStatusState.Blocked, Kind: "question");
        tab.ActiveLeaf!.ProgramStatus = new(ProgramStatusState.Working);
        tab.ToggleFocusMode();
        Assert.Equal(ProgramStatusState.Blocked, tab.ProgramStatus.State);
        Assert.False(tab.ShowAttentionDot);
        first.ProgramStatus = ProgramStatusSummary.Empty;
        Assert.Equal(ProgramStatusState.Working, tab.ProgramStatus.State);
    }

    [Fact]
    public void ProjectSwitch_MarksSelectedTabInBackgroundProjectInactive()
    {
        var workspace = new Workspace();
        var first = workspace.NewProject(Environment.CurrentDirectory);
        var second = workspace.NewProject(Environment.CurrentDirectory);
        try
        {
            Assert.False(first.SelectedTab!.IsActive);
            Assert.True(second.SelectedTab!.IsActive);
            workspace.SelectedProject = first;
            Assert.True(first.SelectedTab.IsActive);
            Assert.False(second.SelectedTab.IsActive);
        }
        finally { first.Dispose(); second.Dispose(); }
    }
}
