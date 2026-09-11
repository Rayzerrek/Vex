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
    private static GhosttyTerminal NewTerm()
    {
        var term = new GhosttyTerminal(80, 25);
        term.WritePty += (_, _) => { };
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
}
