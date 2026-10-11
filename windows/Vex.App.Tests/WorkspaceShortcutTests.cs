using System.Windows.Input;
using Vex.App;
using Xunit;

namespace Vex.App.Tests;

public sealed class WorkspaceShortcutTests
{
    [Theory]
    [InlineData(Key.P, WorkspaceShortcut.CommandPalette)]
    [InlineData(Key.Tab, WorkspaceShortcut.PreviousTab)]
    [InlineData(Key.R, WorkspaceShortcut.SplitRight)]
    [InlineData(Key.D, WorkspaceShortcut.SplitDown)]
    [InlineData(Key.T, WorkspaceShortcut.NewTab)]
    [InlineData(Key.W, WorkspaceShortcut.ClosePaneOrTab)]
    [InlineData(Key.M, WorkspaceShortcut.ThemeSwitcher)]
    [InlineData(Key.Space, WorkspaceShortcut.TabPeek)]
    [InlineData(Key.PageDown, WorkspaceShortcut.NextProject)]
    [InlineData(Key.PageUp, WorkspaceShortcut.PreviousProject)]
    [InlineData(Key.O, WorkspaceShortcut.ProjectPicker)]
    public void TryGetWorkspaceShortcut_CtrlShift_MapsToExpectedShortcut(Key key, WorkspaceShortcut expected)
    {
        var matched = MainWindow.TryGetWorkspaceShortcut(
            ModifierKeys.Control | ModifierKeys.Shift,
            key,
            out var shortcut,
            out _);

        Assert.True(matched);
        Assert.Equal(expected, shortcut);
    }

    [Fact]
    public void TryGetWorkspaceShortcut_AllDigitKeys_MatchProjectIndexMapping()
    {
        var digitKeys = new[] { Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9, Key.D0 };
        var numPadKeys = new[] { Key.NumPad1, Key.NumPad2, Key.NumPad3, Key.NumPad4, Key.NumPad5, Key.NumPad6, Key.NumPad7, Key.NumPad8, Key.NumPad9, Key.NumPad0 };

        for (var expectedIndex = 0; expectedIndex < digitKeys.Length; expectedIndex++)
        foreach (var key in new[] { digitKeys[expectedIndex], numPadKeys[expectedIndex] })
        {
            Assert.True(MainWindow.TryGetProjectIndex(key, out var index), $"Project key {key} was not recognized.");
            Assert.True(index == expectedIndex, $"Project key {key} mapped to {index}, expected {expectedIndex}.");
            var matched = MainWindow.TryGetWorkspaceShortcut(
                ModifierKeys.Control | ModifierKeys.Shift, key, out var shortcut, out var projectIndex);

            Assert.True(matched, $"Ctrl+Shift+{key} was not recognized.");
            Assert.True(shortcut == WorkspaceShortcut.SwitchProject, $"Ctrl+Shift+{key} mapped to {shortcut}.");
            Assert.True(projectIndex == expectedIndex, $"Ctrl+Shift+{key} mapped to project {projectIndex}, expected {expectedIndex}.");
        }
    }

    [Fact]
    public void TryGetWorkspaceShortcut_CtrlTab_MapsToNextTab()
    {
        var matched = MainWindow.TryGetWorkspaceShortcut(
            ModifierKeys.Control,
            Key.Tab,
            out var shortcut,
            out _);

        Assert.True(matched);
        Assert.Equal(WorkspaceShortcut.NextTab, shortcut);
    }

    [Theory]
    [InlineData(Key.W)]
    [InlineData(Key.R)]
    [InlineData(Key.D)]
    [InlineData(Key.T)]
    [InlineData(Key.P)]
    [InlineData(Key.M)]
    [InlineData(Key.C)]
    [InlineData(Key.V)]
    [InlineData(Key.X)]
    [InlineData(Key.S)]
    [InlineData(Key.A)]
    [InlineData(Key.Z)]
    public void TryGetWorkspaceShortcut_PlainCtrl_DoesNotInterceptTuiKeys(Key key)
    {
        // Plain Ctrl+key (except Ctrl+Tab) belongs to the terminal/TUI.
        var matched = MainWindow.TryGetWorkspaceShortcut(
            ModifierKeys.Control,
            key,
            out _,
            out _);

        Assert.False(matched);
    }

    [Theory]
    [InlineData(Key.W)]
    [InlineData(Key.R)]
    [InlineData(Key.D)]
    [InlineData(Key.T)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Enter)]
    public void TryGetWorkspaceShortcut_NoModifiers_ReturnsFalse(Key key)
    {
        var matched = MainWindow.TryGetWorkspaceShortcut(
            ModifierKeys.None,
            key,
            out _,
            out _);

        Assert.False(matched);
    }
}
