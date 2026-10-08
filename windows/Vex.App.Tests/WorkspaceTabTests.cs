using System.Windows.Controls;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class WorkspaceTabTests
{
    [Theory]
    [InlineData("pwsh")]
    [InlineData("cmd")]
    [InlineData("bash")]
    [InlineData("fish")]
    [InlineData("nu")]
    [InlineData("wsl")]
    public void TabIcons_ShellAppearsOnlyWhenAlone(string shell)
    {
        using var tab = new WorkspaceTab("Terminal", @"C:\work");
        var pane = Assert.IsType<TerminalPane>(tab.ActiveLeaf);
        var shellIcon = Assert.IsType<AppIcon>(AppIconCatalog.ResolveIcon(shell));
        pane.AppIcon = shellIcon;
        Assert.Same(shellIcon, Assert.Single(tab.TabIcons));

        tab.Split(Orientation.Horizontal);
        Assert.Empty(tab.TabIcons);

        var agent = Assert.IsType<TerminalPane>(tab.ActiveLeaf);
        var agentIcon = Assert.IsType<AppIcon>(AppIconCatalog.ResolveIcon("codex"));
        agent.AppIcon = agentIcon;
        Assert.Same(agentIcon, Assert.Single(tab.TabIcons));
        tab.ActiveLeaf = pane;
        Assert.Same(agentIcon, Assert.Single(tab.TabIcons));

        agent.Close();
        Assert.Same(shellIcon, Assert.Single(tab.TabIcons));
    }

    [Fact]
    public void TabIcons_TracksAllPanesIncludingRepeatedAppsAndFocusMode()
    {
        using var tab = new WorkspaceTab("Terminal", @"C:\work");
        var first = Assert.IsType<TerminalPane>(tab.ActiveLeaf);
        var codex = Assert.IsType<AppIcon>(AppIconCatalog.ResolveIcon("codex"));
        var claude = Assert.IsType<AppIcon>(AppIconCatalog.ResolveIcon("claude"));
        first.AppIcon = codex;
        tab.Split(Orientation.Horizontal);
        var second = Assert.IsType<TerminalPane>(tab.ActiveLeaf);
        second.AppIcon = codex;
        tab.Split(Orientation.Vertical);
        var third = Assert.IsType<TerminalPane>(tab.ActiveLeaf);
        third.AppIcon = claude;
        tab.ToggleFocusMode();
        Assert.Equal(new[] { codex, codex, claude }, tab.TabIcons);

        var changedProperties = new List<string?>();
        tab.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);
        second.AppIcon = null;
        Assert.Contains(nameof(WorkspaceTab.TabIcons), changedProperties);
        Assert.Equal(new[] { codex, claude }, tab.TabIcons);

        third.Close();
        Assert.Same(codex, Assert.Single(tab.TabIcons));
    }

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
