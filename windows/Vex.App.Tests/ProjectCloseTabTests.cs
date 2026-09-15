using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class ProjectCloseTabTests
{
    [Fact]
    public void CloseSelectedFirstTab_SelectsNextTab()
    {
        var project = new Project("P", "C:\\work");
        project.NewTab();
        var first = project.Tabs[0];
        var second = project.Tabs[1];
        project.SelectedTab = first;

        project.CloseTab(first);

        Assert.DoesNotContain(first, project.Tabs);
        Assert.Same(second, project.SelectedTab);
    }

    [Fact]
    public void CloseSelectedMiddleTab_SelectsLeftNeighbour()
    {
        var project = new Project("P", "C:\\work");
        project.NewTab();
        project.NewTab();
        var middle = project.Tabs[1];
        var left = project.Tabs[0];
        project.SelectedTab = middle;

        project.CloseTab(middle);

        Assert.DoesNotContain(middle, project.Tabs);
        Assert.Same(left, project.SelectedTab);
    }

    [Fact]
    public void CloseBackgroundTab_KeepsSelection()
    {
        var project = new Project("P", "C:\\work");
        project.NewTab();
        var first = project.Tabs[0];
        var second = project.Tabs[1];
        project.SelectedTab = second;

        project.CloseTab(first);

        Assert.DoesNotContain(first, project.Tabs);
        Assert.Same(second, project.SelectedTab);
    }
}
