using Vex.Libghostty;
using Xunit;

namespace Vex.Libghostty.Tests;

public sealed class PackedCellSnapshotTests
{
    [Theory]
    [InlineData("A", false)]
    [InlineData("\u017c", false)]
    [InlineData("\u6f22", true)]
    [InlineData("\U0001f9ea", true)]
    [InlineData("\U0010ffff", false)]
    public void StyledTextThenDefaultText_PreservesScalarWidthAndClearsStyle(string text, bool wide)
    {
        using var terminal = new GhosttyTerminal(20, 4);
        terminal.Feed("\x1b[1;3;4;38;2;12;34;56;48;5;123m" + text);
        terminal.UpdateFrame();
        var styled = terminal.FrameRows[0].Cells[0];
        Assert.Equal(text, styled.Text);
        Assert.Equal(wide, styled.Wide);
        Assert.Equal(CellFlags.Bold | CellFlags.Italic | CellFlags.Underline, styled.Flags);
        Assert.Equal(ColorTag.Rgb, styled.FgTag);
        Assert.Equal(0x0c2238, styled.FgValue);
        Assert.Equal(ColorTag.Palette, styled.BgTag);
        Assert.Equal(123, styled.BgValue);

        terminal.Feed("\x1b[0m\x1b[H\x1b[2K" + text);
        terminal.UpdateFrame();
        var plain = terminal.FrameRows[0].Cells[0];
        Assert.Equal(text, plain.Text);
        Assert.Equal(wide, plain.Wide);
        Assert.Equal(CellFlags.None, plain.Flags);
        Assert.Equal(ColorTag.None, plain.FgTag);
        Assert.Equal(ColorTag.None, plain.BgTag);
        if (wide)
        {
            Assert.True(terminal.FrameRows[0].Cells[1].Tail);
            Assert.Equal("", terminal.FrameRows[0].Cells[1].Text);
        }
    }

    [Theory]
    [InlineData("48;5;123", ColorTag.Palette, 123)]
    [InlineData("48;2;12;34;56", ColorTag.Rgb, 0x0c2238)]
    public void ErasedCells_KeepBackgroundAndLosePreviousText(string sgr, ColorTag tag, int value)
    {
        using var terminal = new GhosttyTerminal(20, 4);
        terminal.Feed("\x1b[1mtext\x1b[0m\x1b[" + sgr + "m\x1b[2J");
        terminal.UpdateFrame();
        foreach (var cell in terminal.FrameRows.SelectMany(row => row.Cells))
        {
            Assert.Equal("", cell.Text);
            Assert.Equal(CellFlags.None, cell.Flags);
            Assert.Equal(tag, cell.BgTag);
            Assert.Equal(value, cell.BgValue);
        }
    }
}
