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

    [Theory]
    [InlineData("nvim", "main.rs*", "Neovim", "Unsaved Changes in Neovim")]
    [InlineData("vim", "main.rs*", "Vim", "Unsaved Changes in Vim")]
    [InlineData("helix", "main.rs*", "Helix", "Unsaved Changes in Helix")]
    [InlineData("nano", "main.rs*", "Nano", "Unsaved Changes in Nano")]
    [InlineData("micro", "main.rs*", "Micro", "Unsaved Changes in Micro")]
    [InlineData("emacs", "main.rs*", "Emacs", "Unsaved Changes in Emacs")]
    [InlineData("kak", "main.rs*", "Kak", "Unsaved Changes in Kak")]
    [InlineData("amp", "main.rs*", "Amp", "Unsaved Changes in Amp")]
    [InlineData("lapce", "main.rs*", "Lapce", "Unsaved Changes in Lapce")]
    [InlineData("joe", "main.rs*", "Joe", "Unsaved Changes in Joe")]
    [InlineData("custom_editor", "main.rs*", "Custom_editor", "Unsaved Changes in Custom_editor")]
    [InlineData(null, "main.rs* - kak", "Kak", "Unsaved Changes in Kak")]
    [InlineData(null, "main.rs [+] - amp", "Amp", "Unsaved Changes in Amp")]
    [InlineData(null, "file.py ● - customtool", "Customtool", "Unsaved Changes in Customtool")]
    [InlineData(null, "doc.txt [modified] - randomeditor", "Randomeditor", "Unsaved Changes in Randomeditor")]
    [InlineData(null, "main.rs*", "Editor", "Unsaved Changes")]
    public void GetCloseInfo_DirtyTerminalPane_ResolvesEditorFromProcessOrTitle(
        string? processName, string title, string expectedApp, string expectedTitle)
    {
        var terminal = new TerminalPane(@"C:\work")
        {
            Title = title,
            ActiveProcessName = processName,
            IsDirty = true,
        };

        var info = TabCloseConfirmation.GetCloseInfo(terminal);

        Assert.True(info.NeedsConfirmation);
        Assert.True(info.IsDirty);
        Assert.Equal(expectedApp, info.AppName);
        Assert.Equal(expectedTitle, info.Title);
        Assert.Contains(title.TrimEnd('*'), info.Message);
        if (expectedApp != "Editor")
            Assert.NotNull(info.Icon);
        else
            Assert.Null(info.Icon);
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
            term.ActiveProcessName = "nvim";
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
