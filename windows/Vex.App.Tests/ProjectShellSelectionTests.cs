using System.Text.Json;
using System.Windows.Controls;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class ProjectShellSelectionTests
{
    [Fact]
    public void ProjectDefaultAndPaneOverrides_AreIndependentOfGlobalSettings()
    {
        var original = AppSettings.Instance.ShellId;
        using var project = new Project("Work", @"C:\work", "nu");
        try
        {
            AppSettings.Instance.ShellId = "cmd";
            var first = Assert.IsType<TerminalPane>(Assert.Single(project.Tabs).Root);
            Assert.Equal("nu", first.ShellId);
            var overrideTab = Assert.IsType<WorkspaceTab>(project.NewTab("pwsh"));
            var powershell = Assert.IsType<TerminalPane>(overrideTab.Root);
            Assert.Equal("pwsh", powershell.ShellId);
            overrideTab.Split(Orientation.Horizontal);
            Assert.Equal(new[] { "pwsh", "nu" }, overrideTab.Leaves.OfType<TerminalPane>().Select(pane => pane.ShellId));
            project.DefaultShellId = "cmd";
            Assert.Equal("nu", first.ShellId);
            Assert.Equal("pwsh", powershell.ShellId);
            overrideTab.Split(Orientation.Vertical, "nu");
            Assert.Equal("nu", Assert.IsType<TerminalPane>(overrideTab.ActiveLeaf).ShellId);
            Assert.Equal("cmd", Assert.IsType<TerminalPane>(Assert.IsType<WorkspaceTab>(project.NewTab()).Root).ShellId);
        }
        finally { AppSettings.Instance.ShellId = original; }
    }

    [Fact]
    public void SessionRoundTrip_PreservesProjectDefaultPaneShellsAndDirectories()
    {
        var previous = SessionStore.Current;
        var workspace = new Workspace();
        var restored = new Workspace();
        try
        {
            var project = workspace.NewProject(@"\\wsl.localhost\Ubuntu\home\user", "wsl:/bin/bash");
            var tab = Assert.IsType<WorkspaceTab>(project.SelectedTab);
            tab.Split(Orientation.Horizontal, "pwsh");
            var windowsPane = Assert.IsType<TerminalPane>(tab.ActiveLeaf);
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), windowsPane.WorkingDirectory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(SessionStore.Capture(workspace), VexJsonContext.Default.SessionSnapshot);
            SessionStore.Populate(restored, JsonSerializer.Deserialize(bytes, VexJsonContext.Default.SessionSnapshot));
            var restoredProject = Assert.Single(restored.Projects);
            Assert.Equal("wsl:/bin/bash", restoredProject.DefaultShellId);
            var panes = Assert.Single(restoredProject.Tabs).Leaves.OfType<TerminalPane>().ToArray();
            Assert.Equal(new[] { "wsl:/bin/bash", "pwsh" }, panes.Select(pane => pane.ShellId));
            Assert.Equal(project.WorkingDirectory, panes[0].WorkingDirectory);
            Assert.Equal(windowsPane.WorkingDirectory, panes[1].WorkingDirectory);
        }
        finally
        {
            foreach (var project in workspace.Projects.Concat(restored.Projects)) project.Dispose();
            SessionStore.Current = previous;
        }
    }

    [Fact]
    public void LegacyWslSession_UsesLinuxShellWithoutChangingWindowsProjectDefaults()
    {
        var previous = SessionStore.Current;
        var original = AppSettings.Instance.ShellId;
        var workspace = new Workspace();
        try
        {
            AppSettings.Instance.ShellId = "nu";
            SessionStore.Populate(workspace, new SessionSnapshot { Projects =
            {
                new ProjectSnapshot { Name = "Linux", WorkingDirectory = @"\\wsl.localhost\Ubuntu\home\user" },
                new ProjectSnapshot { Name = "Windows", WorkingDirectory = @"C:\work" },
            } });
            Assert.Equal(new[] { "wsl", "nu" }, workspace.Projects.Select(project => project.DefaultShellId));
            Assert.Equal("wsl", Assert.IsType<TerminalPane>(Assert.Single(workspace.Projects[0].Tabs).Root).ShellId);
        }
        finally
        {
            foreach (var project in workspace.Projects) project.Dispose();
            SessionStore.Current = previous;
            AppSettings.Instance.ShellId = original;
        }
    }

    [Fact]
    public void SavedLayout_PreservesPaneShellsWithoutChangingTheTargetProjectDefault()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var source = new Project("Source", directory, "nu");
        var sourceTab = Assert.IsType<WorkspaceTab>(source.SelectedTab);
        sourceTab.Split(Orientation.Horizontal, "pwsh");
        var layout = SavedTerminalLayouts.CaptureLayout(sourceTab, directory);
        using var target = new Project("Target", directory, "cmd");
        var tab = Assert.IsType<WorkspaceTab>(target.OpenSavedLayout(layout));
        Assert.Equal(new[] { "nu", "pwsh" }, tab.Leaves.OfType<TerminalPane>().Select(pane => pane.ShellId));
        Assert.Equal("cmd", target.DefaultShellId);
        tab.Split(Orientation.Vertical);
        Assert.Equal("cmd", Assert.IsType<TerminalPane>(tab.ActiveLeaf).ShellId);
    }

    [Fact]
    public void SavedWindowsLayout_CannotSilentlyRunCommandsInALinuxDirectory()
    {
        Assert.Throws<System.IO.IOException>(() => SavedTerminalLayouts.CreateLayoutPaneTree(
            new SavedLayoutPane { ShellId = "cmd", Command = "echo should-not-run" }, @"\\wsl.localhost\Ubuntu\home\user", "wsl"));
    }

    [Fact]
    public void StartupPrewarm_UsesTheFocusedPaneOverride()
    {
        var snapshot = new SessionSnapshot { Projects = { new ProjectSnapshot
        {
            WorkingDirectory = @"\\wsl.localhost\Ubuntu\home\user",
            DefaultShellId = "wsl:/bin/bash",
            Tabs = { new TabSnapshot { Root = new SplitPaneSnapshot
            {
                First = new TerminalPaneSnapshot { ShellId = "wsl:/bin/bash" },
                Second = new TerminalPaneSnapshot { IsFocused = true, ShellId = "pwsh", WorkingDirectory = @"C:\work" },
            } } },
        } } };
        var launch = SessionStore.StartupTerminalLaunch(snapshot, "nu");
        Assert.NotNull(launch);
        Assert.Equal((@"C:\work", "pwsh"), launch.Value);
        Assert.Null(SessionStore.StartupTerminalLaunch(null, "wsl"));
    }

    [Theory]
    [InlineData("wsl:relative")]
    [InlineData("wsl:/bin/bash\n")]
    [InlineData("\ncmd")]
    public void CorruptShellIds_AreNotUsedToLaunchProcesses(string shellId) => Assert.Null(ShellRegistry.ValidShellId(shellId));

    [Fact]
    public void WslHome_IsFirstAndReusesLiveTabs()
    {
        var workspace = new Workspace();
        try
        {
            var windows = workspace.NewProject(@"C:\work", "nu");
            var home = workspace.OpenWslHome(@"\\wsl.localhost\Ubuntu\home\user");
            var tab = Assert.Single(home.Tabs);
            Assert.Same(home, workspace.Projects[0]);
            Assert.Equal("Home", home.Name);
            Assert.Equal("wsl", home.DefaultShellId);
            workspace.SelectedProject = windows;
            Assert.Same(home, workspace.OpenWslHome(home.WorkingDirectory));
            Assert.Same(tab, Assert.Single(home.Tabs));
            Assert.Equal(2, workspace.Projects.Count);
        }
        finally { foreach (var project in workspace.Projects) project.Dispose(); }
    }
}
