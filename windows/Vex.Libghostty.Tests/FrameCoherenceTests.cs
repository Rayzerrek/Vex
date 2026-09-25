using System.Text;
using Vex.Libghostty;
using Xunit;

namespace Vex.Libghostty.Tests;

/// <summary>
/// The managed cell cache must match the emulator after every consumed frame.
/// <see cref="GhosttyTerminal.UpdateFrame"/> trusts the render state's dirty
/// flag: a Clean frame skips the cell walk and keeps whatever
/// <see cref="GhosttyTerminal.FrameRows"/> already holds. If the emulator ever
/// changes the viewport without reporting dirty, the renderer keeps painting
/// the previous rows — the duplicated/stale lines users see while a TUI or an
/// agent streams output, on resize, and when scrolling.
/// <para>
/// Each case drives the emulator the way the live pump does (chunked feeds,
/// one snapshot per chunk) and then compares the incrementally gathered rows
/// against a forced full reread of the same state. Any difference is a frame
/// the renderer would have painted stale.
/// </para>
/// </summary>
public sealed class FrameCoherenceTests
{
    private static GhosttyTerminal NewTerm(int cols, int rows)
    {
        var term = new GhosttyTerminal(cols, rows);
        term.SetDefaultColors(
            new GhosttyColorRgb(0xE0, 0xE0, 0xE0),
            new GhosttyColorRgb(0x1E, 0x1E, 0x1E),
            new GhosttyColorRgb(0xFF, 0xFF, 0xFF),
            Enumerable.Range(0, 256).Select(i => new GhosttyColorRgb((byte)i, (byte)(255 - i), 0x80)).ToArray());
        term.WritePty += (_, _) => { };
        return term;
    }

    private static void Feed(GhosttyTerminal term, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        term.Feed(bytes, 0, bytes.Length);
    }

    /// <summary>Text plus attributes of every visible cell, so a mismatch is
    /// a real content difference rather than a stale string reference.</summary>
    private static string Snapshot(GhosttyTerminal term)
    {
        var sb = new StringBuilder();
        foreach (var row in term.FrameRows)
        {
            foreach (var cell in row.Cells)
            {
                sb.Append(cell.Text.Length == 0 ? "\u00b7" : cell.Text);
                sb.Append((byte)cell.Flags).Append((int)cell.FgTag).Append(cell.FgValue)
                  .Append((int)cell.BgTag).Append(cell.BgValue).Append(',');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>One pump tick: consume whatever is dirty, then verify the rows
    /// the renderer just gathered are the rows the emulator actually holds.</summary>
    private static void AssertCoherent(GhosttyTerminal term, string label)
    {
        var dirty = term.FrameDirty;
        var gathered = Snapshot(term);
        term.InvalidateCellCache();
        term.UpdateFrame();
        var reread = Snapshot(term);
        Assert.True(gathered == reread,
            $"frame dirty={dirty} after {label} left the managed rows stale:\n" +
            $"gathered:\n{gathered}\nreread:\n{reread}");
    }

    /// <summary>Feeds a script in chunks the way ConPTY delivers them, taking a
    /// snapshot after every chunk.</summary>
    private static void Stream(GhosttyTerminal term, string script, int chunk, string label)
    {
        for (var off = 0; off < script.Length; off += chunk)
        {
            var n = Math.Min(chunk, script.Length - off);
            Feed(term, script.Substring(off, n));
            term.UpdateFrame();
            AssertCoherent(term, $"{label}@{off}");
        }
    }

    /// <summary>
    /// Synchronized output (DEC 2026) is how nvim, opencode and the agent CLIs
    /// publish a frame: everything between `?2026h` and `?2026l` is one atomic
    /// screen change. The emulator tracks the mode (so the host can wait for
    /// the close) but it still applies rows as they arrive, which is why the
    /// renderer must not paint until the mode clears. Both halves of that
    /// contract are pinned here: a host that paints mid-block shows a torn
    /// frame — new text in some rows, the previous frame in the rest — which
    /// reads as duplicated/ghost text while an agent streams.
    /// </summary>
    [Fact]
    public void SynchronizedOutput_TracksModeButAppliesRowsOnArrival()
    {
        using var term = NewTerm(20, 4);
        Feed(term, "alpha line\r\nbravo line\r\ncharlie line\r\n");
        term.UpdateFrame();
        var settled = Snapshot(term);
        Assert.False(term.SynchronizedOutput);

        Feed(term, "\x1b[?2026h");
        Assert.True(term.SynchronizedOutput);
        Feed(term, "\x1b[1;1HALPHA");
        term.UpdateFrame();
        Assert.NotEqual(settled, Snapshot(term));

        Feed(term, "\x1b[2;1HBRAVO");
        Feed(term, "\x1b[?2026l");
        Assert.False(term.SynchronizedOutput);
        term.UpdateFrame();
        Assert.NotEqual(settled, Snapshot(term));
    }

    [Fact]
    public void StreamingOutput_StaysCoherent()
    {
        using var term = NewTerm(40, 8);
        var sb = new StringBuilder();
        for (var i = 1; i <= 40; i++)
            sb.Append("line-").Append(i).Append(" the quick brown fox jumps over the lazy dog\r\n");
        Stream(term, sb.ToString(), chunk: 23, label: "stream");
    }

    /// <summary>A synchronized-output frame must be coherent once it is
    /// committed, and the rows it publishes at the close are the rows the
    /// block wrote as a whole.</summary>
    [Fact]
    public void SynchronizedOutputFrame_IsCoherentAfterClose()
    {
        using var term = NewTerm(20, 4);
        Feed(term, "\x1b[?2026h");
        for (var i = 0; i < 20; i++)
            Feed(term, $"\x1b[{i % 4 + 1};1Hframe row{i}");
        Feed(term, "\x1b[?2026l");
        term.UpdateFrame();
        AssertCoherent(term, "sync-frame");
    }

    [Fact]
    public void ScrollingRegionsAndClears_StayCoherent()
    {
        using var term = NewTerm(24, 6);
        var sb = new StringBuilder();
        sb.Append("\x1b[2J\x1b[H");
        // A status line repainted in place many times (spinner), a scroll
        // region with reverse index, ED/EL clears, and a colored bar.
        for (var i = 0; i < 30; i++)
        {
            sb.Append("\x1b[1;1H\x1b[48;2;30;40;60mstatus: working ").Append(i % 10).Append("     \x1b[0m");
            sb.Append("\x1b[4;1H\x1b[0J");
            sb.Append("body line ").Append(i).Append("\r\n");
            sb.Append("\x1b[3;1H\x1b[K");
        }
        sb.Append("\x1b[2;5r\x1b[2;1H\x1bM\x1b[2J");
        Stream(term, sb.ToString(), chunk: 31, label: "clear");
    }

    [Fact]
    public void Resize_StaysCoherent()
    {
        using var term = NewTerm(30, 8);
        var sb = new StringBuilder();
        for (var i = 1; i <= 30; i++)
            sb.Append("before-resize line ").Append(i).Append(" wrapped text here\r\n");
        Stream(term, sb.ToString(), chunk: 17, label: "pre-resize");

        // A pane resize reflows the buffer; the next gathered frame must match
        // the emulator rather than reuse pre-resize rows.
        term.Resize(22, 6, 8, 16);
        term.UpdateFrame();
        AssertCoherent(term, "resize-narrower");
        var narrowed = Snapshot(term);

        term.Resize(44, 12, 8, 16);
        term.UpdateFrame();
        AssertCoherent(term, "resize-wider");
        Assert.NotEqual(narrowed, Snapshot(term));
    }

    [Fact]
    public void AlternateScreenRoundTrip_StaysCoherent()
    {
        using var term = NewTerm(30, 8);
        Feed(term, "primary screen content\r\nsecond line\r\n");
        term.UpdateFrame();
        AssertCoherent(term, "primary");

        Feed(term, "\x1b[?1049h");
        term.UpdateFrame();
        AssertCoherent(term, "alt-enter");
        Feed(term, "\x1b[2J\x1b[H");
        for (var i = 1; i <= 20; i++)
            Feed(term, $"tui row {i}\r\n");
        term.UpdateFrame();
        AssertCoherent(term, "alt-stream");

        Feed(term, "\x1b[?1049l");
        term.UpdateFrame();
        AssertCoherent(term, "alt-exit");
    }

    [Fact]
    public void ViewportScroll_StaysCoherent()
    {
        using var term = NewTerm(30, 6);
        var sb = new StringBuilder();
        for (var i = 1; i <= 40; i++)
            sb.Append("scroll line ").Append(i).Append("\r\n");
        Stream(term, sb.ToString(), chunk: 19, label: "scroll-feed");

        term.ScrollBy(-3);
        term.UpdateFrame();
        AssertCoherent(term, "scroll-up");
        term.ScrollBy(3);
        term.UpdateFrame();
        AssertCoherent(term, "scroll-back");
        term.ScrollToTop();
        term.UpdateFrame();
        AssertCoherent(term, "scroll-top");
        term.ScrollToBottom();
        term.UpdateFrame();
        AssertCoherent(term, "scroll-bottom");
    }
}
