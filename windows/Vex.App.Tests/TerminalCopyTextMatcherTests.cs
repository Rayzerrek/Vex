using Vex.App.Terminal.Native;
using Vex.Libghostty;
using Xunit;

namespace Vex.App.Tests;

public sealed class TerminalCopyTextMatcherTests
{
    [Fact]
    public void ApplicationCopy_WithoutTerminalSelection_LocatesOnlyCopiedCells()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        terminal.Feed("prefix copied text suffix");
        terminal.UpdateFrame();
        Assert.False(terminal.HasSelection);
        Assert.Equal([new TerminalCopyRange(0, 7, 17)],
            TerminalCopyTextMatcher.FindCopyRanges(terminal.FrameRows, 80, "copied text", 0));
    }

    [Fact]
    public void ApplicationCopy_UnicodeAndCrLf_MapsGraphemesAndWideTails()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        terminal.Feed("prefix 漢🦀e\u0301 end\r\nsecond line suffix");
        terminal.UpdateFrame();
        Assert.Equal([new TerminalCopyRange(0, 7, 11), new TerminalCopyRange(1, 0, 10)],
            TerminalCopyTextMatcher.FindCopyRanges(terminal.FrameRows, 80, "漢🦀e\u0301\r\nsecond line\r\n", 0));
    }

    [Fact]
    public void ApplicationCopy_RepeatedText_PrefersRowNearestCursor()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        terminal.Feed("copy\r\ncopy\r\ncopy");
        terminal.UpdateFrame();
        Assert.Equal([new TerminalCopyRange(2, 0, 3)],
            TerminalCopyTextMatcher.FindCopyRanges(terminal.FrameRows, 80, "copy", 2));
    }
}
