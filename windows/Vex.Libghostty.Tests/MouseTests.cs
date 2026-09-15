using System.Text;
using Vex.Libghostty;
using Xunit;

namespace Vex.Libghostty.Tests;

/// <summary>
/// Locks down the mouse round-trip contract: tracking detection reads the
/// live DEC mode bits, the encoder emits the wire format the terminal
/// negotiated, and routing/encoding can never disagree. The divergence case
/// (an app resetting one tracking mode while another is still set) used to
/// silence every event when the encoder relied on libghostty's cached
/// last-transition flag.
/// </summary>
public sealed class MouseTests
{
    // A 10x20 cell: the geometry the app supplies to ghostty_terminal_resize
    // for a typical 14pt monospace font at 100% DPI. The encoder converts
    // pixel positions to cells with the size it was last resized with, so a
    // terminal that never resizes keeps the 8x16 defaults and every expected
    // cell below would be off by one. Tests must resize to state the
    // geometry they assert against.
    private const int CellW = 10;
    private const int CellH = 20;

    private static GhosttyTerminal NewTerm()
    {
        var term = new GhosttyTerminal(80, 25);
        term.WritePty += (_, _) => { };
        term.Resize(80, 25, CellW, CellH);
        return term;
    }

    private static void Feed(GhosttyTerminal term, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        term.Feed(bytes, 0, bytes.Length);
    }

    private const string ResetModes =
        "\x1b[?9l\x1b[?1000l\x1b[?1002l\x1b[?1003l" +
        "\x1b[?1005l\x1b[?1006l\x1b[?1015l\x1b[?1016l";

    private static string Encode(GhosttyTerminal term, MouseInputAction action,
        MouseInputButton? button, double x, double y, bool anyButtonPressed)
    {
        var output = new byte[128];
        var len = term.EncodeMouse(action, button, MouseInputModifiers.None,
            x, y, anyButtonPressed, output);
        return Encoding.Latin1.GetString(output, 0, len).Replace("\x1b", "<ESC>");
    }

    public static TheoryData<string, MouseTrackingMode, bool> TrackingCases => new()
    {
        { $"{ResetModes}\x1b[?1002h\x1b[?1006h", MouseTrackingMode.Button, true }, // nvim 0.10+ mouse=a
        { $"{ResetModes}\x1b[?1000h\x1b[?1002h\x1b[?1006h", MouseTrackingMode.Button, true },
        { $"{ResetModes}\x1b[?1003h\x1b[?1006h", MouseTrackingMode.Any, true },
        { $"{ResetModes}\x1b[?1000h", MouseTrackingMode.Normal, true }, // less, htop
        { $"{ResetModes}\x1b[?9h", MouseTrackingMode.X10, true },
        { ResetModes, MouseTrackingMode.None, false }, // plain shell prompt
    };

    [Theory]
    [MemberData(nameof(TrackingCases))]
    public void TrackingMode_FollowsLiveModeBits(string modes, MouseTrackingMode expected, bool tracking)
    {
        using var term = NewTerm();
        Feed(term, modes);
        Assert.Equal(expected, term.TrackingMode);
        Assert.Equal(tracking, term.MouseTracking);
    }

    [Fact]
    public void MixedTrackingModeReset_KeepsReporting()
    {
        // 1002 stays set while 1000 is reset: routing sees tracking and the
        // encoder must still emit SGR press/drag/release, not zero bytes.
        using var term = NewTerm();
        Feed(term, $"{ResetModes}\x1b[?1002h\x1b[?1000h\x1b[?1006h\x1b[?1000l");
        Assert.Equal(MouseTrackingMode.Button, term.TrackingMode);
        Assert.True(term.MouseTracking);
        Assert.Equal("<ESC>[<0;6;3M", Encode(term, MouseInputAction.Press, MouseInputButton.Left, 55, 45, anyButtonPressed: true));
        Assert.Equal("<ESC>[<32;21;3M", Encode(term, MouseInputAction.Motion, MouseInputButton.Left, 205, 45, anyButtonPressed: true));
        Assert.Equal("<ESC>[<0;21;3m", Encode(term, MouseInputAction.Release, MouseInputButton.Left, 205, 45, anyButtonPressed: false));
    }

    [Fact]
    public void NvimSequence_EncodesSgrPressDragRelease()
    {
        using var term = NewTerm();
        Feed(term, $"{ResetModes}\x1b[?1002h\x1b[?1006h");
        Assert.Equal("<ESC>[<0;6;3M", Encode(term, MouseInputAction.Press, MouseInputButton.Left, 55, 45, anyButtonPressed: true));
        Assert.Equal("<ESC>[<32;21;3M", Encode(term, MouseInputAction.Motion, MouseInputButton.Left, 205, 45, anyButtonPressed: true));
        Assert.Equal("<ESC>[<0;21;3m", Encode(term, MouseInputAction.Release, MouseInputButton.Left, 205, 45, anyButtonPressed: false));
    }

    [Fact]
    public void TrackingModes_FilterMotion()
    {
        // Mode 1000 reports only press/release; 1003 also reports unbuttoned
        // motion (deduplicated per cell by TrackLastCell).
        using var term = NewTerm();
        Feed(term, $"{ResetModes}\x1b[?1000h\x1b[?1006h");
        Assert.Equal("", Encode(term, MouseInputAction.Motion, MouseInputButton.Left, 205, 45, anyButtonPressed: true));

        Feed(term, $"\x1b[?1000l\x1b[?1003h\x1b[?1006h");
        Assert.Equal("<ESC>[<35;10;5M", Encode(term, MouseInputAction.Motion, null, 95, 85, anyButtonPressed: false));
    }

    [Fact]
    public void Wheel_EncodesPressAndRelease()
    {
        // Wheel steps must emit both press and release: some TUI apps act on
        // the release, and the app's button state stays consistent.
        using var term = NewTerm();
        Feed(term, $"{ResetModes}\x1b[?1002h\x1b[?1006h");
        Assert.Equal("<ESC>[<64;6;3M", Encode(term, MouseInputAction.Press, MouseInputButton.WheelUp, 55, 45, anyButtonPressed: false));
        Assert.Equal("<ESC>[<64;6;3m", Encode(term, MouseInputAction.Release, MouseInputButton.WheelUp, 55, 45, anyButtonPressed: false));
        Assert.Equal("<ESC>[<65;6;3M", Encode(term, MouseInputAction.Press, MouseInputButton.WheelDown, 55, 45, anyButtonPressed: false));
    }

    [Fact]
    public void SameCell_ClicksAreNotDeduplicated()
    {
        // TrackLastCell deduplicates motion only: press/release at one cell
        // must always go through (double-click and wheel rely on it).
        using var term = NewTerm();
        Feed(term, $"{ResetModes}\x1b[?1002h\x1b[?1006h");
        Assert.Equal("<ESC>[<0;6;3M", Encode(term, MouseInputAction.Press, MouseInputButton.Left, 55, 45, anyButtonPressed: true));
        Assert.Equal("<ESC>[<0;6;3m", Encode(term, MouseInputAction.Release, MouseInputButton.Left, 55, 45, anyButtonPressed: false));
        Assert.Equal("<ESC>[<0;6;3M", Encode(term, MouseInputAction.Press, MouseInputButton.Left, 55, 45, anyButtonPressed: true));
    }

    [Fact]
    public void UrxvtFormat_IsNegotiated()
    {
        using var term = NewTerm();
        Feed(term, $"{ResetModes}\x1b[?1000h\x1b[?1015h");
        Assert.Equal(MouseFormat.Urxvt, term.Format);
        Assert.Equal("<ESC>[32;6;3M", Encode(term, MouseInputAction.Press, MouseInputButton.Left, 55, 45, anyButtonPressed: true));
    }

    [Fact]
    public void MiddleAndRightButtons_UseXtermButtonCodes()
    {
        // X10/SGR button codes are 0=left, 1=middle, 2=right, 3=release.
        // Getting middle and right swapped silently breaks tmux's paste and
        // context menus, which is invisible until a user tries them.
        using var term = NewTerm();
        Feed(term, $"{ResetModes}\x1b[?1002h\x1b[?1006h");
        Assert.Equal("<ESC>[<1;6;3M", Encode(term, MouseInputAction.Press, MouseInputButton.Middle, 55, 45, anyButtonPressed: true));
        Assert.Equal("<ESC>[<2;6;3M", Encode(term, MouseInputAction.Press, MouseInputButton.Right, 55, 45, anyButtonPressed: true));
    }

    [Fact]
    public void X10Mode_ReportsPressOnly()
    {
        // Mode 9 is press-only by definition: releases and motion must not
        // reach the app, and the wire format is the legacy 6-byte form.
        using var term = NewTerm();
        Feed(term, $"{ResetModes}\x1b[?9h");
        Assert.Equal(MouseTrackingMode.X10, term.TrackingMode);
        Assert.StartsWith("<ESC>[M", Encode(term, MouseInputAction.Press, MouseInputButton.Left, 55, 45, anyButtonPressed: true));
        Assert.Equal("", Encode(term, MouseInputAction.Release, MouseInputButton.Left, 55, 45, anyButtonPressed: false));
        Assert.Equal("", Encode(term, MouseInputAction.Motion, MouseInputButton.Left, 55, 45, anyButtonPressed: true));
    }

    [Fact]
    public void Modifiers_AreReportedInTheButtonCode()
    {
        // xterm packs modifiers into the low bits above the button: +4 shift,
        // +8 alt, +16 ctrl. Apps branch on these for ctrl+click and the like.
        using var term = NewTerm();
        Feed(term, $"{ResetModes}\x1b[?1002h\x1b[?1006h");
        var output = new byte[128];
        var len = term.EncodeMouse(MouseInputAction.Press, MouseInputButton.Left,
            MouseInputModifiers.Control, 55, 45, anyButtonPressed: true, output);
        Assert.Equal("<ESC>[<16;6;3M", Encoding.Latin1.GetString(output, 0, len).Replace("\x1b", "<ESC>"));
    }

    [Fact]
    public void NoTracking_EncodesNothing()
    {
        // A plain shell prompt must never see mouse reports; otherwise typed
        // output gets random escape sequences injected into it.
        using var term = NewTerm();
        Feed(term, ResetModes);
        Assert.False(term.MouseTracking);
        Assert.Equal("", Encode(term, MouseInputAction.Press, MouseInputButton.Left, 55, 45, anyButtonPressed: true));
    }

    [Fact]
    public void CellSizeFromResize_MovesTheReportedColumn()
    {
        // The encoder converts pixels to cells with the geometry the host
        // passed to Resize. A stale size puts every click one column off, so
        // the same pixel must map to different cells for different widths.
        using var narrow = new GhosttyTerminal(80, 25);
        narrow.WritePty += (_, _) => { };
        narrow.Resize(80, 25, 10, 20);
        Feed(narrow, $"{ResetModes}\x1b[?1002h\x1b[?1006h");
        Assert.Equal("<ESC>[<0;6;3M", Encode(narrow, MouseInputAction.Press, MouseInputButton.Left, 55, 45, anyButtonPressed: true));

        using var wide = new GhosttyTerminal(80, 25);
        wide.WritePty += (_, _) => { };
        wide.Resize(80, 25, 12, 20);
        Feed(wide, $"{ResetModes}\x1b[?1002h\x1b[?1006h");
        Assert.Equal("<ESC>[<0;5;3M", Encode(wide, MouseInputAction.Press, MouseInputButton.Left, 55, 45, anyButtonPressed: true));
    }

    [Fact]
    public void RepeatedClicks_SelectWordThenLineAfterSingleClickIsCleared()
    {
        using var term = NewTerm();
        Feed(term, "\x1b[2J\x1b[Halpha beta\r\nsecond line");

        Assert.Equal(1, Click(term, col: 7, row: 0));
        term.ClearSelection(resetGesture: false);
        Assert.False(term.HasSelection);

        Assert.Equal(2, Click(term, col: 7, row: 0));
        Assert.Equal("beta", term.GetSelectedText());

        Assert.Equal(3, Click(term, col: 7, row: 0));
        Assert.Equal("alpha beta", term.GetSelectedText());
    }

    private static int Click(GhosttyTerminal term, int col, int row)
    {
        var x = col * CellW + CellW / 2.0;
        var y = row * CellH + CellH / 2.0;
        var clickCount = term.SelectionPress(col, row, x, y);
        term.SelectionRelease(col, row);
        return clickCount;
    }
}
