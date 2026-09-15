using System.Text;
using Vex.Libghostty;
using Xunit;

namespace Vex.Libghostty.Tests;

public sealed class KeyboardTests
{
    private static string Encode(GhosttyTerminal term, TerminalKey key,
        TerminalKeyModifiers modifiers = TerminalKeyModifiers.None,
        TerminalKeyAction action = TerminalKeyAction.Press,
        string text = "", uint unshifted = 0)
    {
        Span<byte> output = stackalloc byte[128];
        var textBytes = Encoding.UTF8.GetBytes(text);
        var written = term.EncodeKey(key, action, modifiers, textBytes, unshifted, output);
        return Encoding.ASCII.GetString(output[..written]);
    }

    [Fact]
    public void LegacyEncoding_HonorsTerminalModes()
    {
        using var term = new GhosttyTerminal(80, 24);

        Assert.Equal("\r", Encode(term, TerminalKey.Enter));
        Assert.Equal("\x1b[A", Encode(term, TerminalKey.ArrowUp));
        Assert.Equal("\x01", Encode(term, TerminalKey.A, TerminalKeyModifiers.Control, unshifted: 'a'));
        Assert.Equal("\x1b[3;5~", Encode(term, TerminalKey.Delete, TerminalKeyModifiers.Control));

        term.Feed("\x1b[?1h");
        Assert.Equal("\x1bOA", Encode(term, TerminalKey.ArrowUp));
    }

    [Fact]
    public void KittyNegotiation_UpdatesFlagsButEncodingStaysLegacy()
    {
        // The terminal tracks the negotiated Kitty flags (query replies,
        // state reporting), but the encoder must keep emitting legacy bytes:
        // ConPTY cannot carry CSI-u input and would swallow every key.
        using var term = new GhosttyTerminal(80, 24);
        term.Feed("[>31u");

        Assert.Equal(31, term.KittyKeyboardFlags);
        Assert.Equal("A", Encode(term, TerminalKey.A,
            TerminalKeyModifiers.Shift, TerminalKeyAction.Press, "A", 'a'));
        Assert.Equal("a", Encode(term, TerminalKey.A,
            TerminalKeyModifiers.None, TerminalKeyAction.Press, "a", 'a'));
        // Release events are a Kitty-only concept; legacy sends nothing.
        Assert.Equal("", Encode(term, TerminalKey.A,
            TerminalKeyModifiers.Shift, TerminalKeyAction.Release, unshifted: 'a'));
    }

    [Fact]
    public void KittyMode_CtrlCAndEscapeStayLegacyBytes()
    {
        // Regression: pi/agy push Kitty flags 7 and expect CSI-u input
        // (ESC[99;5u for Ctrl+C, ESC[27u for Escape). ConPTY's input parser
        // drops those sequences entirely, so the app saw nothing. The encoder
        // must emit the C0/legacy bytes pi's raw-byte fallbacks accept.
        using var term = new GhosttyTerminal(80, 24);
        term.Feed("[>7u");

        Assert.Equal("", Encode(term, TerminalKey.C, TerminalKeyModifiers.Control, unshifted: 'c'));
        Assert.Equal("", Encode(term, TerminalKey.Escape));
    }

    [Fact]
    public void KittyQuery_IsAnsweredByTheTerminal()
    {
        using var term = new GhosttyTerminal(80, 24);
        var responses = new List<string>();
        term.WritePty += (bytes, length) => responses.Add(Encoding.ASCII.GetString(bytes, 0, length));

        term.Feed("\x1b[>5u\x1b[?u");

        Assert.Contains("\x1b[?5u", responses);
    }
}
