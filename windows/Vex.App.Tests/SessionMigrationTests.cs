using System.Text.Json;
using System.Windows.Controls;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class SessionMigrationTests
{
    [Fact]
    public void LegacyEditorLeaves_RestoreAsTerminalsAndSaveWithoutEditorMetadata()
    {
        const string json = """
            {
              "Windows": [{
                "SelectedProjectIndex": 0,
                "Projects": [{
                  "Name": "legacy",
                  "WorkingDirectory": "C:\\work",
                  "SelectedTabIndex": 1,
                  "Tabs": [
                    {
                      "Title": "notes.txt",
                      "Root": { "type": "editor", "FilePath": "C:\\missing\\notes.txt", "IsFocused": true }
                    },
                    {
                      "Title": "Mixed",
                      "HasCustomTitle": true,
                      "Root": {
                        "type": "split", "Orientation": "Vertical", "Ratio": 0.3,
                        "First": { "type": "terminal", "IsFocused": false },
                        "Second": { "type": "editor", "FilePath": "C:\\missing\\code.cs", "IsFocused": true }
                      }
                    }
                  ]
                }]
              }]
            }
            """;
        var snapshot = JsonSerializer.Deserialize(json, VexJsonContext.Default.AppSnapshot);
        Assert.NotNull(snapshot);
        var previous = SessionStore.Current;
        var workspace = new Workspace();
        try
        {
            SessionStore.Populate(workspace, Assert.Single(snapshot.Windows));
            var project = Assert.Single(workspace.Projects);
            Assert.Same(project, workspace.SelectedProject);
            Assert.Equal(2, project.Tabs.Count);
            Assert.IsType<TerminalPane>(project.Tabs[0].Root);
            Assert.True(project.Tabs[0].ActiveLeaf?.IsFocused);
            var selected = project.Tabs[1];
            Assert.Same(selected, project.SelectedTab);
            Assert.Equal(@"C:\work", selected.WorkingDirectory);
            Assert.True(selected.HasCustomTitle);
            Assert.Equal("Mixed", selected.Title);
            var split = Assert.IsType<SplitPane>(selected.Root);
            Assert.Equal(Orientation.Vertical, split.Orientation);
            Assert.Equal(0.3, split.Ratio);
            var first = Assert.IsType<TerminalPane>(split.First);
            var second = Assert.IsType<TerminalPane>(split.Second);
            Assert.False(first.IsFocused);
            Assert.True(second.IsFocused);
            Assert.Same(second, selected.ActiveLeaf);

            var saved = new AppSnapshot { Windows = { SessionStore.Capture(workspace) } };
            var savedJson = JsonSerializer.Serialize(saved, VexJsonContext.Default.AppSnapshot);
            Assert.DoesNotContain("\"editor\"", savedJson);
            Assert.DoesNotContain("FilePath", savedJson);
            var restoredSnapshot = JsonSerializer.Deserialize(savedJson, VexJsonContext.Default.AppSnapshot);
            Assert.NotNull(restoredSnapshot);
            var savedProject = Assert.Single(Assert.Single(restoredSnapshot.Windows).Projects);
            Assert.Equal(1, savedProject.SelectedTabIndex);
            Assert.IsType<TerminalPaneSnapshot>(savedProject.Tabs[0].Root);
            var savedSplit = Assert.IsType<SplitPaneSnapshot>(savedProject.Tabs[1].Root);
            Assert.Equal(0.3, savedSplit.Ratio);
            Assert.True(Assert.IsType<TerminalPaneSnapshot>(savedSplit.Second).IsFocused);
        }
        finally
        {
            foreach (var project in workspace.Projects)
                project.Dispose();
            SessionStore.Current = previous;
        }
    }
}