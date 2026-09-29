using Vex.Libghostty;
using Xunit;

namespace Vex.Libghostty.Tests;

public sealed class UnicodeCellTests
{
    [Theory]
    [InlineData("ż")]
    [InlineData("漢")]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👩\u200D💻")]
    [InlineData("🇵🇱")]
    public void UnicodeText_StaysCorrectAcrossUnrelatedUpdatesAndReplacement(string text)
    {
        using var terminal = new GhosttyTerminal(20, 4);
        terminal.Feed(text);
        terminal.UpdateFrame();
        Assert.Equal(text, string.Concat(terminal.FrameRows[0].Cells.Select(cell => cell.Text)));

        terminal.Feed("\x1b[2;1Hother row");
        terminal.UpdateFrame();
        Assert.Equal(text, string.Concat(terminal.FrameRows[0].Cells.Select(cell => cell.Text)));

        terminal.Feed("\x1b[H\x1b[2Kö");
        terminal.UpdateFrame();
        Assert.Equal("ö", terminal.FrameRows[0].Cells[0].Text);

        terminal.Feed("\x1b[H\x1b[2K" + text);
        terminal.UpdateFrame();
        Assert.Equal(text, string.Concat(terminal.FrameRows[0].Cells.Select(cell => cell.Text)));
    }

    [Theory]
    [InlineData("e\u0301", "e\u0300")]
    [InlineData("e\u0301", "e")]
    [InlineData("e", "e\u0301")]
    [InlineData("😀", "😁")]
    public void ReplacingUnicodeText_UpdatesEveryCodepointAndLength(string before, string after)
    {
        using var terminal = new GhosttyTerminal(20, 4);
        terminal.Feed(before);
        terminal.UpdateFrame();
        terminal.Feed("\x1b[H\x1b[2K" + after);
        terminal.UpdateFrame();
        Assert.Equal(after, terminal.FrameRows[0].Cells[0].Text);
    }

    [Fact]
    public void RepeatedUnicodeSnapshots_DoNotAllocatePerCell()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        terminal.Feed(string.Join("\r\n", Enumerable.Repeat("ż─漢😀e\u0301👩\u200D💻", 20)));
        for (var frame = 0; frame < 100; frame++)
        {
            terminal.InvalidateCellCache();
            terminal.UpdateFrame();
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 100; frame++)
        {
            terminal.InvalidateCellCache();
            terminal.UpdateFrame();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(allocated < 1024, $"Repeated snapshots allocated {allocated} bytes.");
    }
}
