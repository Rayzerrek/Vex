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

    [Theory]
    [InlineData(Key.D1, 0)]
    [InlineData(Key.D2, 1)]
    [InlineData(Key.D9, 8)]
    [InlineData(Key.D0, 9)]
    [InlineData(Key.NumPad1, 0)]
    [InlineData(Key.NumPad5, 4)]
    [InlineData(Key.NumPad0, 9)]
    public void TryGetWorkspaceShortcut_CtrlShiftDigits_MapsToSwitchProject(Key key, int expectedProjectIndex)
    {
        var matched = MainWindow.TryGetWorkspaceShortcut(
            ModifierKeys.Control | ModifierKeys.Shift,
            key,
            out var shortcut,
            out var projectIndex);

        Assert.True(matched);
        Assert.Equal(WorkspaceShortcut.SwitchProject, shortcut);
        Assert.Equal(expectedProjectIndex, projectIndex);
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

    [Fact]
    public void TryGetWorkspaceShortcut_CtrlS_MapsToSaveFile()
    {
        var matched = MainWindow.TryGetWorkspaceShortcut(
            ModifierKeys.Control,
            Key.S,
            out var shortcut,
            out _);

        Assert.True(matched);
        Assert.Equal(WorkspaceShortcut.SaveFile, shortcut);
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
    [InlineData(Key.A)]
    [InlineData(Key.Z)]
    public void TryGetWorkspaceShortcut_PlainCtrl_DoesNotInterceptTuiKeys(Key key)
    {
        // Plain Ctrl+key (except Ctrl+Tab and Ctrl+S) belongs to the terminal/TUI.
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
