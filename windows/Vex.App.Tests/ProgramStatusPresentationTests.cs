using System.Windows;
using System.Windows.Controls;
using Vex.App.Model;
using Vex.Libghostty;
using Xunit;

namespace Vex.App.Tests;

public sealed class ProgramStatusPresentationTests
{
    [Theory]
    [InlineData(ProgramStatusState.Working, null, "◌")]
    [InlineData(ProgramStatusState.Blocked, "permission", "◇")]
    [InlineData(ProgramStatusState.Blocked, "auth", "↪")]
    [InlineData(ProgramStatusState.Blocked, "question", "?")]
    [InlineData(ProgramStatusState.Done, null, "✓")]
    [InlineData(ProgramStatusState.Error, null, "!")]
    public void StateSymbols_AreDistinct(ProgramStatusState state, string? kind, string symbol) =>
        Assert.Equal(symbol, ProgramStatusPresentation.StatusSymbol(new(state, Kind: kind)));

    [Fact]
    public void Attention_IdentifiesAndFocusesTheReportingSplit()
    {
        using var tab = new WorkspaceTab("Work", Environment.CurrentDirectory);
        var first = Assert.IsType<TerminalPane>(tab.ActiveLeaf);
        tab.Split(Orientation.Horizontal);
        var second = Assert.IsType<TerminalPane>(tab.ActiveLeaf);
        first.Title = "Agent";
        first.ProgramStatus = new(ProgramStatusState.Blocked, Kind: "permission", Progress: 40);
        tab.ToggleFocusMode();
        Assert.Equal("Needs approval · 40% · Pane 1 · Agent", tab.ProgramStatusLabel);
        Assert.True(tab.FocusAttentionPane());
        Assert.Same(first, tab.ActiveLeaf);
        Assert.Same(first, tab.DisplayRoot);
        first.Title = "Review";
        Assert.EndsWith("Review", tab.ProgramStatusLabel);
        first.ProgramStatus = ProgramStatusSummary.Empty;
        second.ProgramStatus = new(ProgramStatusState.Working);
        Assert.False(tab.FocusAttentionPane());
        Assert.Same(first, tab.ActiveLeaf);
    }

    [Fact]
    public void WorkingAnimation_IsVisibleOnlyAndStopsForProgressAndUnload()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var badge = new ProgramStatusBadge { Status = new(ProgramStatusState.Working) };
                Assert.False(badge.IsSpinning);
                window = new Window { Content = badge, Width = 100, Height = 100, Left = -10000, ShowActivated = false, ShowInTaskbar = false };
                window.Show();
                window.UpdateLayout();
                Assert.Equal(SystemParameters.ClientAreaAnimation, badge.IsSpinning);
                badge.Visibility = Visibility.Hidden;
                Assert.False(badge.IsSpinning);
                badge.Visibility = Visibility.Visible;
                Assert.Equal(SystemParameters.ClientAreaAnimation, badge.IsSpinning);
                badge.Status = new(ProgramStatusState.Working, Progress: 35);
                Assert.Contains("35%", Assert.IsType<string>(badge.ToolTip));
                Assert.False(badge.IsSpinning);
                badge.Status = new(ProgramStatusState.Error, Message: "Operation failed");
                Assert.Contains("Operation failed", Assert.IsType<string>(badge.ToolTip));
                badge.Status = new(ProgramStatusState.Working);
                window.Close();
                Assert.False(badge.IsSpinning);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Program status animation test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
