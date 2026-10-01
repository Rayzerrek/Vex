using System.Text.Json;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class SessionRecoveryTests
{
    [Theory]
    [InlineData("{\"Projects\":null}")]
    [InlineData("{\"Projects\":[null]}")]
    [InlineData("{\"Projects\":[{\"Tabs\":null}]}")]
    [InlineData("{\"Projects\":[{\"Tabs\":[null]}]}")]
    [InlineData("{\"Projects\":[{\"Name\":null,\"WorkingDirectory\":null,\"Tabs\":[{\"Title\":null,\"Root\":null}]}]}")]
    public void Populate_CorruptSnapshotRestoresUsableWorkspace(string json)
    {
        var snapshot = JsonSerializer.Deserialize(json, VexJsonContext.Default.SessionSnapshot);
        WithWorkspace(workspace =>
        {
            SessionStore.Populate(workspace, snapshot);
            var project = Assert.Single(workspace.Projects);
            Assert.Same(project, workspace.SelectedProject);
            Assert.False(string.IsNullOrWhiteSpace(project.WorkingDirectory));
            var tab = Assert.Single(project.Tabs);
            Assert.NotNull(tab.Title);
            Assert.IsType<TerminalPane>(tab.Root);
        });
    }

    [Fact]
    public void Populate_RemovingNullEntriesPreservesSelectedProjectAndTab()
    {
        const string json = """
            { "SelectedProjectIndex": 2, "Projects": [
              null,
              { "Name": "first", "WorkingDirectory": "C:\\first", "Tabs": [] },
              { "Name": "selected", "WorkingDirectory": "C:\\selected", "SelectedTabIndex": 2,
                "Tabs": [null, { "Title": "other" }, { "Title": "chosen", "HasCustomTitle": true }] }
            ] }
            """;
        var snapshot = JsonSerializer.Deserialize(json, VexJsonContext.Default.SessionSnapshot);
        WithWorkspace(workspace =>
        {
            SessionStore.Populate(workspace, snapshot);
            Assert.Equal(2, workspace.Projects.Count);
            var project = workspace.SelectedProject!;
            Assert.Equal("selected", project.Name);
            Assert.Equal("chosen", project.SelectedTab!.Title);
            Assert.Equal(2, project.Tabs.Count);
        });
    }

    [Theory]
    [InlineData("{\"Windows\":null}")]
    [InlineData("{\"Windows\":[null]}")]
    [InlineData("{\"Windows\":[{\"Projects\":null}]}")]
    public void DeserializeSessionSnapshot_NullCollectionsAreSafeBeforePrewarm(string json)
    {
        var snapshot = SessionStore.DeserializeSessionSnapshot(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.True(snapshot is null || snapshot.Projects.Count == 0);
    }

    [Fact]
    public void Populate_NullSplitChildrenAndInvalidRatioRecoverToTerminals()
    {
        var snapshot = new SessionSnapshot
        {
            Projects = { new ProjectSnapshot
            {
                WorkingDirectory = @"C:\work",
                Tabs = { new TabSnapshot { Root = new SplitPaneSnapshot { First = null!, Second = null!, Ratio = double.NaN } } },
            } },
        };
        WithWorkspace(workspace =>
        {
            SessionStore.Populate(workspace, snapshot);
            var split = Assert.IsType<SplitPane>(workspace.SelectedProject!.SelectedTab!.Root);
            Assert.IsType<TerminalPane>(split.First);
            Assert.IsType<TerminalPane>(split.Second);
            Assert.Equal(0.5, split.Ratio);
        });
    }

    private static void WithWorkspace(Action<Workspace> test)
    {
        var previous = SessionStore.Current;
        var workspace = new Workspace();
        try { test(workspace); }
        finally
        {
            foreach (var project in workspace.Projects)
                project.Dispose();
            SessionStore.Current = previous;
        }
    }
}
