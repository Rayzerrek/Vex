using System.Text;
using Vex.Libghostty;
using Xunit;

namespace Vex.Libghostty.Tests;

/// <summary>
/// ConPTY eats the DCS wrapper (introducer and terminator) of client queries
/// such as nvim's XTGETTCAP (`ESC P + q 4D73 ST`) and DECRQSS
/// (`ESC P $ q m ST`), so only the middles reach the emulator. These tests
/// lock down that the feed filter hides those middles instead of printing
/// them, without touching ordinary text or intact sequences.
/// </summary>
public sealed class FeedFilterTests
{
    private static GhosttyTerminal NewTerm()
    {
        var term = new GhosttyTerminal(100, 30);
        term.SetDefaultColors(
            new GhosttyColorRgb(0xE0, 0xE0, 0xE0),
            new GhosttyColorRgb(0x1E, 0x1E, 0x1E),
            new GhosttyColorRgb(0xFF, 0xFF, 0xFF),
            Enumerable.Range(0, 256).Select(i => new GhosttyColorRgb((byte)i, (byte)(255 - i), 0x80)).ToArray());
        term.WritePty += (_, _) => { };
        return term;
    }

    private static string GridText(GhosttyTerminal term)
    {
        term.UpdateFrame();
        var sb = new StringBuilder();
        foreach (var row in term.FrameRows)
            foreach (var cell in row.Cells)
                sb.Append(cell.Text);
        return sb.ToString();
    }

    private static void Feed(GhosttyTerminal term, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        term.Feed(bytes, 0, bytes.Length);
    }

    [Theory]
    [InlineData("+q4D73\x1b[H")] // XTGETTCAP Ms, as nvim sends it through ConPTY
    [InlineData("+q5463;524742;73657472676266;73657472676262\x1b[H")] // Tc;RGB;setrgbf;setrgbb
    [InlineData("$qm\x1b[K")] // DECRQSS SGR (nvim undercurl probe)
    public void StrippedDcsPayload_DoesNotReachGrid(string input)
    {
        using var term = NewTerm();
        Feed(term, input);
        var grid = GridText(term);
        Assert.DoesNotContain("+q", grid, StringComparison.Ordinal);
        Assert.DoesNotContain("$qm", grid, StringComparison.Ordinal);
    }

    [Fact]
    public void StrippedDcsPayload_SplitAcrossFeeds_DoesNotReachGrid()
    {
        using var term = NewTerm();
        var first = Encoding.ASCII.GetBytes("+q4D");
        var second = Encoding.ASCII.GetBytes("73\x1b[H");
        term.Feed(first, 0, first.Length);
        term.Feed(second, 0, second.Length);
        Assert.DoesNotContain("+q", GridText(term), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a+b=c")]
    [InlineData("C++ rocks")]
    [InlineData("echo $query $q1 $env:x")]
    [InlineData("cost $100 and $5")]
    [InlineData("xx+qCD12yy")] // unknown cap: must stay visible
    [InlineData("what+q is this")]
    [InlineData("cost $q? maybe")]
    public void OrdinaryText_PassesThroughUntouched(string text)
    {
        using var term = NewTerm();
        Feed(term, text);
        Assert.Contains(text, GridText(term), StringComparison.Ordinal);
    }

    [Fact]
    public void IntactDcsQuery_StillAnswered()
    {
        using var term = NewTerm();
        var responses = 0;
        term.WritePty += (_, _) => responses++;
        Feed(term, "\x1bP+q5463\x1b\\");
        Assert.True(responses > 0, "expected an XTGETTCAP response from the emulator");
    }

    [Fact]
    public void IntactDcsQuery_SplitAcrossFeeds_StillAnswered()
    {
        using var term = NewTerm();
        var responses = 0;
        term.WritePty += (_, _) => responses++;
        var first = Encoding.ASCII.GetBytes("\x1bP+q54");
        var second = Encoding.ASCII.GetBytes("63\x1b\\");
        term.Feed(first, 0, first.Length);
        term.Feed(second, 0, second.Length);
        Assert.True(responses > 0, "expected an XTGETTCAP response from the emulator");
    }

    [Fact]
    public void DsrQuery_StillAnswered()
    {
        using var term = NewTerm();
        var seen = new List<byte[]>();
        term.WritePty += (data, len) =>
        {
            var copy = new byte[len];
            Array.Copy(data, copy, len);
            seen.Add(copy);
        };
        Feed(term, "\x1b[5n");
        Assert.Contains(seen, r => r.SequenceEqual(new byte[] { 0x1B, 0x5B, 0x30, 0x6E }));
    }
}
