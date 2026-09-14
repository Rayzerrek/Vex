using System.Windows.Controls;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class WorkspaceTabTests
{
    [Fact]
    public void Split_InFocusMode_RevealsTheNewSplitTree()
    {
        using var tab = new WorkspaceTab("Terminal", "C:\\work");
        tab.ToggleFocusMode();

        tab.Split(Orientation.Horizontal);

        Assert.False(tab.IsFocusMode);
        Assert.Same(tab.Root, tab.DisplayRoot);
        var split = Assert.IsType<SplitPane>(tab.DisplayRoot);
        Assert.Equal(Orientation.Horizontal, split.Orientation);
        Assert.Equal(2, tab.PaneCount);
        Assert.Same(split.Second, tab.ActiveLeaf);
    }

    [Fact]
    public void NestedSplit_NotifiesThatTheRootLayoutChanged()
    {
        using var tab = new WorkspaceTab("Terminal", "C:\\work");
        tab.Split(Orientation.Horizontal);
        var root = tab.Root;
        var changedProperties = new List<string?>();
        var layoutChanged = 0;
        tab.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);
        tab.LayoutChanged += () => layoutChanged++;

        tab.Split(Orientation.Vertical);

        Assert.Same(root, tab.Root);
        Assert.Equal(3, tab.PaneCount);
        Assert.Contains(nameof(WorkspaceTab.Root), changedProperties);
        Assert.Contains(nameof(WorkspaceTab.DisplayRoot), changedProperties);
        Assert.Equal(1, layoutChanged);
    }
}
