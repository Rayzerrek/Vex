using System.Text;
using Xunit;

namespace Vex.Libghostty.Tests;

public sealed class TerminalClipboardOscTests
{
    [Theory]
    [InlineData("\x07")]
    [InlineData("\x1b\\")]
    public void ClipboardWrite_SplitAcrossEveryByte_DecodesUnicodeAndPreservesOutput(string terminator)
    {
        using var terminal = new GhosttyTerminal(80, 24);
        var writes = new List<string>();
        terminal.ClipboardWriteRequested += writes.Add;
        const string copied = "Zażółć 漢 🦀 e\u0301\nsecond line";
        var output = "before\x1b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(copied)) + terminator + "after";
        foreach (var value in Encoding.UTF8.GetBytes(output))
            terminal.Feed([value], 0, 1);
        terminal.UpdateFrame();
        Assert.Equal([copied], writes);
        Assert.StartsWith("beforeafter", string.Concat(terminal.FrameRows[0].Cells.Select(cell => cell.Text)));
    }

    [Theory]
    [InlineData("c", "?")]
    [InlineData("c", "invalid!")]
    [InlineData("p", "aGVsbG8=")]
    [InlineData("c", "/w==")]
    public void ClipboardReadOrInvalidWrite_IsIgnored(string target, string payload)
    {
        using var terminal = new GhosttyTerminal(80, 24);
        var writes = new List<string>();
        terminal.ClipboardWriteRequested += writes.Add;
        terminal.Feed($"\x1b]52;{target};{payload}\u0007after");
        terminal.UpdateFrame();
        Assert.Empty(writes);
        Assert.StartsWith("after", string.Concat(terminal.FrameRows[0].Cells.Select(cell => cell.Text)));
    }

    [Fact]
    public void ClipboardWrite_LargerThanOriginalOscBuffer_IsDelivered()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        string? copied = null;
        terminal.ClipboardWriteRequested += text => copied = text;
        var text = new string('x', 20_000);
        var output = "\x1b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\x1b\\";
        for (var offset = 0; offset < output.Length; offset += 37)
            terminal.Feed(output.Substring(offset, Math.Min(37, output.Length - offset)));
        Assert.Equal(text, copied);
    }

    [Fact]
    public void ClipboardWrite_OverLimit_IsDiscardedAndNextWriteWorks()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        var writes = new List<string>();
        terminal.ClipboardWriteRequested += writes.Add;
        terminal.Feed("\x1b]52;c;" + new string('A', 1024 * 1024) + "\x07\x1b]52;c;b2s=\x07");
        Assert.Equal(["ok"], writes);
    }
}
