using System.Linq;
using System.Windows.Input;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class WorkspaceProjectSwitchTests
{
    [Fact]
    public void SelectProjectByIndex_ValidIndex_SelectsProjectAndReturnsTrue()
    {
        var workspace = new Workspace();
        var p1 = workspace.NewProject("C:\\proj1");
        var p2 = workspace.NewProject("C:\\proj2");
        var p3 = workspace.NewProject("C:\\proj3");

        Assert.Same(p3, workspace.SelectedProject);

        Assert.True(workspace.SelectProjectByIndex(0));
        Assert.Same(p1, workspace.SelectedProject);

        Assert.True(workspace.SelectProjectByIndex(1));
        Assert.Same(p2, workspace.SelectedProject);

        Assert.True(workspace.SelectProjectByIndex(2));
        Assert.Same(p3, workspace.SelectedProject);
    }

    [Fact]
    public void SelectProjectByIndex_AlreadySelected_ReturnsTrue()
    {
        var workspace = new Workspace();
        var p1 = workspace.NewProject("C:\\proj1");
        var p2 = workspace.NewProject("C:\\proj2");

        workspace.SelectedProject = p1;
        Assert.True(workspace.SelectProjectByIndex(0));
        Assert.Same(p1, workspace.SelectedProject);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-10)]
    [InlineData(2)]
    [InlineData(5)]
    public void SelectProjectByIndex_InvalidIndex_ReturnsFalseAndPreservesSelection(int invalidIndex)
    {
        var workspace = new Workspace();
        var p1 = workspace.NewProject("C:\\proj1");
        var p2 = workspace.NewProject("C:\\proj2");
        workspace.SelectedProject = p1;

        Assert.False(workspace.SelectProjectByIndex(invalidIndex));
        Assert.Same(p1, workspace.SelectedProject);
    }

    [Fact]
    public void CycleProject_SingleOrNoProject_ReturnsFalse()
    {
        var workspace = new Workspace();
        Assert.False(workspace.CycleProject(1));

        workspace.NewProject("C:\\proj1");
        Assert.False(workspace.CycleProject(1));
        Assert.False(workspace.CycleProject(-1));
    }

    [Fact]
    public void CycleProject_Forward_CyclesAndWraps()
    {
        var workspace = new Workspace();
        var p1 = workspace.NewProject("C:\\proj1");
        var p2 = workspace.NewProject("C:\\proj2");
        var p3 = workspace.NewProject("C:\\proj3");

        workspace.SelectedProject = p1; // index 0

        Assert.True(workspace.CycleProject(1));
        Assert.Same(p2, workspace.SelectedProject); // index 1

        Assert.True(workspace.CycleProject(1));
        Assert.Same(p3, workspace.SelectedProject); // index 2

        Assert.True(workspace.CycleProject(1));
        Assert.Same(p1, workspace.SelectedProject); // wrap to index 0
    }

    [Fact]
    public void CycleProject_Backward_CyclesAndWraps()
    {
        var workspace = new Workspace();
        var p1 = workspace.NewProject("C:\\proj1");
        var p2 = workspace.NewProject("C:\\proj2");
        var p3 = workspace.NewProject("C:\\proj3");

        workspace.SelectedProject = p1; // index 0

        Assert.True(workspace.CycleProject(-1));
        Assert.Same(p3, workspace.SelectedProject); // wrap to index 2

        Assert.True(workspace.CycleProject(-1));
        Assert.Same(p2, workspace.SelectedProject); // index 1

        Assert.True(workspace.CycleProject(-1));
        Assert.Same(p1, workspace.SelectedProject); // index 0
    }

    [Fact]
    public void CycleProject_NoSelection_SelectsFirstOrLast()
    {
        var workspace = new Workspace();
        var p1 = workspace.NewProject("C:\\proj1");
        var p2 = workspace.NewProject("C:\\proj2");

        workspace.SelectedProject = null;
        Assert.True(workspace.CycleProject(1));
        Assert.Same(p1, workspace.SelectedProject);

        workspace.SelectedProject = null;
        Assert.True(workspace.CycleProject(-1));
        Assert.Same(p2, workspace.SelectedProject);
    }

    [Theory]
    [InlineData(Key.D1, 0)]
    [InlineData(Key.D2, 1)]
    [InlineData(Key.D3, 2)]
    [InlineData(Key.D4, 3)]
    [InlineData(Key.D5, 4)]
    [InlineData(Key.D6, 5)]
    [InlineData(Key.D7, 6)]
    [InlineData(Key.D8, 7)]
    [InlineData(Key.D9, 8)]
    [InlineData(Key.D0, 9)]
    [InlineData(Key.NumPad1, 0)]
    [InlineData(Key.NumPad2, 1)]
    [InlineData(Key.NumPad3, 2)]
    [InlineData(Key.NumPad4, 3)]
    [InlineData(Key.NumPad5, 4)]
    [InlineData(Key.NumPad6, 5)]
    [InlineData(Key.NumPad7, 6)]
    [InlineData(Key.NumPad8, 7)]
    [InlineData(Key.NumPad9, 8)]
    [InlineData(Key.NumPad0, 9)]
    public void TryGetProjectIndex_DigitKeys_MapsToExpectedIndex(Key key, int expectedIndex)
    {
        Assert.True(MainWindow.TryGetProjectIndex(key, out var index));
        Assert.Equal(expectedIndex, index);
    }

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.Z)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Tab)]
    [InlineData(Key.PageDown)]
    [InlineData(Key.PageUp)]
    [InlineData(Key.O)]
    [InlineData(Key.P)]
    [InlineData(Key.Space)]
    public void TryGetProjectIndex_NonDigitKeys_ReturnsFalse(Key key)
    {
        Assert.False(MainWindow.TryGetProjectIndex(key, out var index));
        Assert.Equal(-1, index);
    }

    [Fact]
    public void PaletteProvider_MultipleProjects_ProvidesNextPrevAndShortcutHints()
    {
        var workspace = new Workspace();
        var p1 = workspace.NewProject("C:\\alpha");
        var p2 = workspace.NewProject("C:\\beta");
        var p3 = workspace.NewProject("C:\\gamma");
        workspace.SelectedProject = p1;

        string? triggeredAction = null;
        var items = PaletteProvider.GetItems(workspace, action => triggeredAction = action).ToList();

        var nextItem = items.FirstOrDefault(i => i.Title == "Next Project");
        Assert.NotNull(nextItem);
        Assert.Contains("Ctrl+Shift+PageDown", nextItem.Subtitle);
        nextItem.Action();
        Assert.Equal("NextProject", triggeredAction);

        var prevItem = items.FirstOrDefault(i => i.Title == "Previous Project");
        Assert.NotNull(prevItem);
        Assert.Contains("Ctrl+Shift+PageUp", prevItem.Subtitle);
        prevItem.Action();
        Assert.Equal("PrevProject", triggeredAction);

        var pickerItem = items.FirstOrDefault(i => i.Title == "Switch Project...");
        Assert.NotNull(pickerItem);
        Assert.Contains("Ctrl+Shift+O", pickerItem.Subtitle);
        pickerItem.Action();
        Assert.Equal("ProjectPicker", triggeredAction);

        var p2Item = items.FirstOrDefault(i => i.Title == $"Switch Project: {p2.Name}");
        Assert.NotNull(p2Item);
        Assert.StartsWith("Ctrl+Shift+2 ·", p2Item.Subtitle);

        var p3Item = items.FirstOrDefault(i => i.Title == $"Switch Project: {p3.Name}");
        Assert.NotNull(p3Item);
        Assert.StartsWith("Ctrl+Shift+3 ·", p3Item.Subtitle);
    }
}
