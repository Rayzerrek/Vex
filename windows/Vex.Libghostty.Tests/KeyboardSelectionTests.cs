using System.Text;
using Xunit;

namespace Vex.Libghostty.Tests;

public sealed class KeyboardSelectionTests
{
    private static GhosttyTerminal NewTerm()
    {
        var term = new GhosttyTerminal(20, 3);
        term.Resize(20, 3, 10, 20);
        var bytes = Encoding.UTF8.GetBytes("alpha beta\r\nsecond line");
        term.Feed(bytes, 0, bytes.Length);
        return term;
    }

    [Fact]
    public void RepeatedKeyboardGestures_DoNotBecomeWordOrLineSelections()
    {
        using var term = NewTerm();
        for (var focus = 9; focus >= 6; focus--)
        {
            term.SetKeyboardSelection(10, 0, focus, 0);
            Assert.Equal("alpha beta"[focus..], term.GetSelectedText());
        }
    }

    [Theory]
    [InlineData(6, 7, "b")]
    [InlineData(7, 6, "b")]
    [InlineData(6, 10, "beta")]
    [InlineData(10, 6, "beta")]
    public void CaretBoundaries_SelectOnlyCharactersBetweenThem(int anchor, int focus, string expected)
    {
        using var term = NewTerm();
        term.SetKeyboardSelection(anchor, 0, focus, 0);
        Assert.Equal(expected, term.GetSelectedText());
    }

    [Fact]
    public void ReturningToAnchor_ClearsSelectionAndAllowsChangingDirection()
    {
        using var term = NewTerm();
        term.SetKeyboardSelection(6, 0, 5, 0);
        term.SetKeyboardSelection(6, 0, 6, 0);
        Assert.False(term.HasSelection);
        term.SetKeyboardSelection(6, 0, 7, 0);
        Assert.Equal("b", term.GetSelectedText());
    }

    [Theory]
    [InlineData(0, 0, 20, 0, "alpha beta")]
    [InlineData(0, 0, 0, 1, "alpha beta")]
    [InlineData(6, 0, 6, 1, "beta\nsecond")]
    [InlineData(6, 1, 6, 0, "beta\nsecond")]
    public void LineBoundaries_AreExclusive(int anchorCol, int anchorRow, int focusCol, int focusRow, string expected)
    {
        using var term = NewTerm();
        term.SetKeyboardSelection(anchorCol, anchorRow, focusCol, focusRow);
        Assert.Equal(expected, term.GetSelectedText());
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    [InlineData(40, true)]
    public void CursorCaretBoundary_IncludesLastCharacterAtPendingWrap(int length, bool pendingWrap)
    {
        using var term = new GhosttyTerminal(20, 3);
        term.Resize(20, 3, 10, 20);
        var input = new string('x', length - 1) + "Z";
        term.Feed(input);
        term.UpdateFrame();
        var cursor = term.Cursor;
        Assert.Equal(pendingWrap, cursor.PendingWrap);
        Assert.Equal(pendingWrap ? 19 : length % 20, cursor.X);
        Assert.Equal(length, cursor.Y * 20 + cursor.CaretColumn);
        term.SetKeyboardSelection(cursor.CaretColumn, cursor.Y, 0, 0);
        Assert.Equal(input, term.GetSelectedText(trim: false));
    }

    [Theory]
    [InlineData("abcdefghijklmnopqr\U0001F9EA")]
    [InlineData("abcdefghijklmnopqrse\u0301")]
    public void PendingWrap_UnicodeAtRightEdgeUsesBoundaryAfterLastCell(string input)
    {
        using var term = new GhosttyTerminal(20, 3);
        term.Feed(input);
        term.UpdateFrame();
        Assert.True(term.Cursor.PendingWrap);
        Assert.Equal(20, term.Cursor.CaretColumn);
        term.SetKeyboardSelection(term.Cursor.CaretColumn, 0, 0, 0);
        Assert.Equal(input, term.GetSelectedText(trim: false));
    }

    [Fact]
    public void CursorMovement_ClearsPendingWrapWithoutMovingTheDrawnCell()
    {
        using var term = new GhosttyTerminal(20, 3);
        term.Feed(new string('x', 20));
        term.UpdateFrame();
        Assert.True(term.Cursor.PendingWrap);
        Assert.Equal(20, term.Cursor.CaretColumn);

        term.Feed("\x1b[1;20H");
        term.UpdateFrame();
        Assert.False(term.Cursor.PendingWrap);
        Assert.Equal(19, term.Cursor.X);
        Assert.Equal(19, term.Cursor.CaretColumn);
        term.SetKeyboardSelection(term.Cursor.CaretColumn, 0, 18, 0);
        Assert.Equal("x", term.GetSelectedText());
    }

    [Fact]
    public void PreviousMouseClicks_DoNotChangeKeyboardSelection()
    {
        using var term = NewTerm();
        term.SelectionPress(7, 0, 75, 10);
        term.SelectionRelease(7, 0);
        term.SetKeyboardSelection(7, 0, 8, 0);
        Assert.Equal("e", term.GetSelectedText());
    }
}
