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

    [Fact]
    public void Resize_LeavesNoNullCellText()
    {
        using var term = new GhosttyTerminal(20, 5);
        Feed(term, "hello world");
        term.UpdateFrame();

        term.Resize(40, 10, 8, 16);
        term.UpdateFrame();

        Assert.Equal(10, term.FrameRows.Length);
        foreach (var row in term.FrameRows)
        {
            Assert.Equal(40, row.Cells.Length);
            foreach (var cell in row.Cells)
            {
                Assert.NotNull(cell.Text);
            }
        }
    }

    [Fact]
    public void Resize_GrowingRows_DoesNotPullConPtyScrollbackIntoScreen()
    {
        using var term = new GhosttyTerminal(5, 3);
        Feed(term, "1\r\n2\r\n3\r\n4\r\n5");

        term.Resize(5, 5, 8, 16);
        term.UpdateFrame();

        var rows = term.FrameRows
            .Select(row => string.Concat(row.Cells.Select(cell => cell.Text)).TrimEnd())
            .ToArray();
        Assert.Equal(new[] { "3", "4", "5", "", "" }, rows);
    }

    [Fact]
    public void StreamingLineUpdate_WrappedLine_Behavior()
    {
        using var term = new GhosttyTerminal(40, 10);
        Feed(term, "prompt: daj lorem 3\r\n");
        Feed(term, "Donec lacus nunc, viverra nec, blandit vel, egestas et, augue.\r\n"); // 62 chars -> 2 rows (40 + 22)
        Feed(term, "esc Working...\r\n");
        Feed(term, "status bar 1\r\n");
        term.UpdateFrame();

        var r0 = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();
        var r1 = string.Concat(term.FrameRows[1].Cells.Select(c => c.Text)).TrimEnd();
        var r2 = string.Concat(term.FrameRows[2].Cells.Select(c => c.Text)).TrimEnd();
        var r3 = string.Concat(term.FrameRows[3].Cells.Select(c => c.Text)).TrimEnd();
        var r4 = string.Concat(term.FrameRows[4].Cells.Select(c => c.Text)).TrimEnd();

        Assert.Equal("prompt: daj lorem 3", r0);
        Assert.Equal("Donec lacus nunc, viverra nec, blandit v", r1);
        Assert.Equal("el, egestas et, augue.", r2);
        Assert.Equal("esc Working...", r3);
        Assert.Equal("status bar 1", r4);
    }

    [Fact]
    public void UpdateFrame_NonDirtyRows_PreserveCellContent()
    {
        // Frame 1: write text on rows 0 and 1, then snapshot.
        using var term = new GhosttyTerminal(40, 10);
        Feed(term, "Hello World\r\n");
        Feed(term, "Second line\r\n");
        term.UpdateFrame();

        var r0a = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();
        var r1a = string.Concat(term.FrameRows[1].Cells.Select(c => c.Text)).TrimEnd();
        Assert.Equal("Hello World", r0a);
        Assert.Equal("Second line", r1a);

        // Frame 2: write only on row 2 (cursor is already there after the \r\n).
        // Rows 0 and 1 should NOT be dirty.
        Feed(term, "Third line");
        term.UpdateFrame();

        // Rows 0 and 1 must still have their content, not be blanked.
        var r0b = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();
        var r1b = string.Concat(term.FrameRows[1].Cells.Select(c => c.Text)).TrimEnd();
        var r2b = string.Concat(term.FrameRows[2].Cells.Select(c => c.Text)).TrimEnd();
        Assert.Equal("Hello World", r0b);
        Assert.Equal("Second line", r1b);
        Assert.Equal("Third line", r2b);
    }

    [Fact]
    public void UpdateFrame_ScrollingContent_RowsShiftCorrectly()
    {
        // Fill a small terminal to trigger scrolling, then verify
        // that rows contain the correct shifted content.
        using var term = new GhosttyTerminal(40, 5);
        for (var i = 0; i < 7; i++)
            Feed(term, $"Line {i}\r\n");
        term.UpdateFrame();

        // 7 lines + cursor each produced \r\n, that's 7 newlines from a 5-row terminal.
        // Row 0 ends up being "Line 3" (lines 0-2 scrolled off into scrollback).
        var r0 = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();
        var r1 = string.Concat(term.FrameRows[1].Cells.Select(c => c.Text)).TrimEnd();
        var r2 = string.Concat(term.FrameRows[2].Cells.Select(c => c.Text)).TrimEnd();
        var r3 = string.Concat(term.FrameRows[3].Cells.Select(c => c.Text)).TrimEnd();
        var r4 = string.Concat(term.FrameRows[4].Cells.Select(c => c.Text)).TrimEnd();
        Assert.Equal("Line 3", r0);
        Assert.Equal("Line 4", r1);
        Assert.Equal("Line 5", r2);
        Assert.Equal("Line 6", r3);
        Assert.Equal("", r4);
    }

    [Fact]
    public void UpdateFrame_CursorUpOverwrite_ContentUpdatedCorrectly()
    {
        // Simulates TUI differential rendering: write lines, then use
        // cursor-up to overwrite a middle line.
        using var term = new GhosttyTerminal(40, 10);
        Feed(term, "Line A\r\n");
        Feed(term, "Line B\r\n");
        Feed(term, "Line C\r\n");
        term.UpdateFrame();

        // Move cursor up 2 rows and overwrite "Line B" with "UPDATED"
        Feed(term, "\x1b[2A\rUPDATED\x1b[K");
        term.UpdateFrame();

        var r0 = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();
        var r1 = string.Concat(term.FrameRows[1].Cells.Select(c => c.Text)).TrimEnd();
        var r2 = string.Concat(term.FrameRows[2].Cells.Select(c => c.Text)).TrimEnd();
        Assert.Equal("Line A", r0);
        Assert.Equal("UPDATED", r1);
        Assert.Equal("Line C", r2);
    }

    [Fact]
    public void UpdateFrame_IncrementalDirty_DoesNotBlankCleanRows()
    {
        // Specifically tests the case where UpdateFrame is called multiple
        // times with only partial dirty flags — clean rows must not lose data.
        using var term = new GhosttyTerminal(40, 10);
        Feed(term, "Row0 content\r\n");
        Feed(term, "Row1 content\r\n");
        Feed(term, "Row2 content\r\n");
        term.UpdateFrame();

        // Only touch row 4 (cursor is at row 3 after 3x \r\n)
        Feed(term, "\r\n");
        Feed(term, "Row4 new");
        term.UpdateFrame();

        // All previous rows must be intact
        var r0 = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();
        var r1 = string.Concat(term.FrameRows[1].Cells.Select(c => c.Text)).TrimEnd();
        var r2 = string.Concat(term.FrameRows[2].Cells.Select(c => c.Text)).TrimEnd();
        Assert.Equal("Row0 content", r0);
        Assert.Equal("Row1 content", r1);
        Assert.Equal("Row2 content", r2);

        // Row 4 has the new content
        var r4 = string.Concat(term.FrameRows[4].Cells.Select(c => c.Text)).TrimEnd();
        Assert.Equal("Row4 new", r4);
    }

    [Fact]
    public void UpdateFrame_TuiDifferentialRender_FullScreen()
    {
        // Simulate pi-tui: fill screen, then do differential update using
        // cursor movement. The screen is 10 rows. We write 10 lines filling it,
        // then each "render pass" does: \r\n (scroll), cursor up N, rewrite lines.
        using var term = new GhosttyTerminal(40, 10);
        
        // Initial render: fill all 10 visible rows
        for (var i = 0; i < 10; i++)
            Feed(term, $"Initial {i}\r\n");
        term.UpdateFrame();

        // Now simulate a streaming update: append a new line at bottom,
        // then cursor up 3 to overwrite rows.
        // The \r\n scrolls everything up by 1.
        Feed(term, "New bottom\r\n");
        // Cursor up 2 to go back and update
        Feed(term, "\x1b[2A\rUpdated line\x1b[K");
        term.UpdateFrame();

        // After the scroll, row content shifts. The "New bottom" appeared,
        // then cursor moved up 2 and overwrote that row with "Updated line".
        // Verify no duplication — each row has unique content.
        var rows = new string[10];
        for (var i = 0; i < 10; i++)
            rows[i] = string.Concat(term.FrameRows[i].Cells.Select(c => c.Text)).TrimEnd();
        
        // Row 8 was "New bottom" but we moved up 2 from row 10 (the cursor row
        // after \r\n scrolled) to row 8 and wrote "Updated line" there.
        // Verify there's no duplication: count distinct non-empty rows
        var nonEmpty = rows.Where(r => r.Length > 0).ToArray();
        Assert.Equal(nonEmpty.Length, nonEmpty.Distinct().Count());
    }

    [Fact]
    public void UpdateFrame_RapidPartialUpdates_NoStaleContent()
    {
        // Simulate rapid streaming: multiple Feed+UpdateFrame cycles
        // where only the last row changes.
        using var term = new GhosttyTerminal(40, 10);
        Feed(term, "Header line\r\n");
        Feed(term, "Content A\r\n");
        term.UpdateFrame();

        // Rapid updates on row 2 (cursor is there)
        for (var i = 0; i < 5; i++)
        {
            Feed(term, $"\rProgress: {i * 20}%\x1b[K");
            term.UpdateFrame();
        }

        var r0 = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();
        var r1 = string.Concat(term.FrameRows[1].Cells.Select(c => c.Text)).TrimEnd();
        var r2 = string.Concat(term.FrameRows[2].Cells.Select(c => c.Text)).TrimEnd();
        Assert.Equal("Header line", r0);
        Assert.Equal("Content A", r1);
        Assert.Equal("Progress: 80%", r2);
    }

    [Fact]
    public void UpdateFrame_CleanFrame_PreservesCellsWithoutReread()
    {
        using var term = new GhosttyTerminal(40, 10);
        Feed(term, "Stable line\r\n");
        term.UpdateFrame();
        var before = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();

        // Second snapshot with no new input must stay Clean and keep cells.
        term.UpdateFrame();
        Assert.Equal(FrameDirty.Clean, term.FrameDirty);
        var after = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();
        Assert.Equal(before, after);
        Assert.Equal("Stable line", after);
    }

    [Fact]
    public void UpdateFrame_InvalidateCellCache_ForcesRereadOnClean()
    {
        using var term = new GhosttyTerminal(40, 10);
        Feed(term, "Before\r\n");
        term.UpdateFrame();

        term.InvalidateCellCache();
        term.UpdateFrame();
        var text = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();
        Assert.Equal("Before", text);
    }

    [Fact]
    public void UpdateFrame_ScrollToBottom_RefreshesViewportCells()
    {
        using var term = new GhosttyTerminal(40, 5);
        for (var i = 0; i < 8; i++)
            Feed(term, $"Line {i}\r\n");
        term.UpdateFrame();

        term.ScrollBy(-3);
        term.UpdateFrame();
        var mid = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();

        term.ScrollToBottom();
        term.UpdateFrame();
        var bottom = string.Concat(term.FrameRows[0].Cells.Select(c => c.Text)).TrimEnd();
        Assert.NotEqual(mid, bottom);
        Assert.Equal("Line 4", bottom);
    }
}
