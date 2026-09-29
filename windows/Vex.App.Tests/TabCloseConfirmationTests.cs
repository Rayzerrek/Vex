using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class TabCloseConfirmationTests
{
    [Theory]
    [InlineData("pi", true)]
    [InlineData("claude", true)]
    [InlineData("claudecode", true)]
    [InlineData("claude-code", true)]
    [InlineData("agy", true)]
    [InlineData("antigravity", true)]
    [InlineData("gemini", true)]
    [InlineData("googlegemini", true)]
    [InlineData("oc", true)]
    [InlineData("opencode", true)]
    [InlineData("open-code", true)]
    [InlineData("codex", true)]
    [InlineData("aider", true)]
    [InlineData("cursor", true)]
    [InlineData("copilot", true)]
    [InlineData("deepseek", true)]
    [InlineData("pi-worker", true)]
    [InlineData("pwsh", false)]
    [InlineData("powershell", false)]
    [InlineData("cmd", false)]
    [InlineData("bash", false)]
    [InlineData("python", false)]
    [InlineData("cargo", false)]
    [InlineData("node", false)]
    [InlineData("nvim", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAgent_RecognizesAgentsAndRejectsOthers(string? input, bool expected)
    {
        Assert.Equal(expected, TabCloseConfirmation.IsAgent(input));
    }

    [Fact]
    public void GetCloseInfo_DirtyEditorPane_RequiresConfirmation()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            var editor = new EditorPane(tempFile);
            editor.IsDirty = true;

            var info = TabCloseConfirmation.GetCloseInfo(editor);

            Assert.True(info.NeedsConfirmation);
            Assert.True(info.IsDirty);
            Assert.False(info.IsAgent);
            Assert.Contains(editor.Title, info.Message);
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetCloseInfo_CleanEditorPane_DoesNotRequireConfirmation()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            var editor = new EditorPane(tempFile);
            editor.IsDirty = false;

            var info = TabCloseConfirmation.GetCloseInfo(editor);

            Assert.False(info.NeedsConfirmation);
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetCloseInfo_DirtyTerminalPane_RequiresConfirmation()
    {
        var terminal = new TerminalPane(@"C:\work");
        terminal.Title = "main.rs*";
        terminal.IsDirty = true;

        var info = TabCloseConfirmation.GetCloseInfo(terminal);

        Assert.True(info.NeedsConfirmation);
        Assert.True(info.IsDirty);
        Assert.Equal("Neovim", info.AppName);
        Assert.Contains("main.rs", info.Message);
    }

    [Fact]
    public void GetCloseInfo_BusyTuiTerminalPane_RequiresConfirmation()
    {
        var terminal = new TerminalPane(@"C:\work");
        terminal.Title = "htop";
        terminal.State = PaneState.Busy;

        var info = TabCloseConfirmation.GetCloseInfo(terminal);

        Assert.True(info.NeedsConfirmation);
        Assert.False(info.IsDirty);
        Assert.Contains("htop", info.Message);
    }

    [Fact]
    public void GetCloseInfo_IdleBareTerminalPane_DoesNotRequireConfirmation()
    {
        var terminal = new TerminalPane(@"C:\work");
        terminal.Title = "Terminal";
        terminal.State = PaneState.Idle;
        terminal.IsDirty = false;

        var info = TabCloseConfirmation.GetCloseInfo(terminal);

        Assert.False(info.NeedsConfirmation);
    }

    [Theory]
    [InlineData("Vex")]
    [InlineData("code")]
    [InlineData("nushell")]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    [InlineData("cmd")]
    [InlineData("bash")]
    [InlineData("Terminal")]
    [InlineData(@"C:\Users\kacpe\code\Vex")]
    [InlineData("elvish")]
    public void GetCloseInfo_IdleShellWithFolderOrShellTitle_DoesNotRequireConfirmation(string title)
    {
        var terminal = new TerminalPane(@"C:\work");
        terminal.Title = title;
        terminal.State = PaneState.Idle;
        terminal.IsDirty = false;

        var info = TabCloseConfirmation.GetCloseInfo(terminal);

        Assert.False(info.NeedsConfirmation);
    }

    [Fact]
    public void GetCloseInfo_WorkspaceTabWithIdleShell_SafeToClose()
    {
        var tab = new WorkspaceTab("Terminal", @"C:\work");
        var info = TabCloseConfirmation.GetCloseInfo(tab);

        Assert.False(info.NeedsConfirmation);
    }

    [Fact]
    public void GetCloseInfo_WorkspaceTabWithDirtyTerminal_RequiresConfirmation()
    {
        var tab = new WorkspaceTab("Terminal", @"C:\work");
        if (tab.ActiveLeaf is TerminalPane terminal)
        {
            terminal.Title = "main.rs*";
            terminal.IsDirty = true;
        }

        var info = TabCloseConfirmation.GetCloseInfo(tab);

        Assert.True(info.NeedsConfirmation);
        Assert.True(info.IsDirty);
    }

    [Fact]
    public void GetWorkspaceExitInfo_CleanWorkspace_SafeToClose()
    {
        var workspace = new Workspace();
        workspace.NewProject(@"C:\work1");

        var info = TabCloseConfirmation.GetWorkspaceExitInfo(workspace);

        Assert.False(info.NeedsConfirmation);
    }

    [Fact]
    public void GetWorkspaceExitInfo_DirtyTabInAnyProject_RequiresConfirmationWithProjectName()
    {
        var workspace = new Workspace();
        var p1 = workspace.NewProject(@"C:\work1");
        var p2 = workspace.NewProject(@"C:\work2");

        if (p2.SelectedTab?.ActiveLeaf is TerminalPane term)
        {
            term.Title = "main.rs*";
            term.IsDirty = true;
        }

        var info = TabCloseConfirmation.GetWorkspaceExitInfo(workspace);

        Assert.True(info.NeedsConfirmation);
        Assert.Single(info.Items);
        Assert.Equal(p2.Name, info.Items[0].ProjectName);
        Assert.Equal("Neovim", info.Items[0].AppName);
    }

    [Fact]
    public void AppSettings_ConfirmOnExit_DefaultsToTrue()
    {
        var settings = new AppSettings();
        Assert.True(settings.ConfirmOnExit);
    }
}
