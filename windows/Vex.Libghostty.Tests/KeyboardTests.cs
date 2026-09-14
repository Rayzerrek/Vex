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
    public void KittyNegotiation_UpdatesFlagsAndEncodesAllEventTypes()
    {
        using var term = new GhosttyTerminal(80, 24);
        term.Feed("\x1b[>31u");

        Assert.Equal(31, term.KittyKeyboardFlags);
        Assert.Equal("\x1b[97:65;2;65u", Encode(term, TerminalKey.A,
            TerminalKeyModifiers.Shift, TerminalKeyAction.Press, "A", 'a'));
        Assert.Equal("\x1b[97:65;2:2;65u", Encode(term, TerminalKey.A,
            TerminalKeyModifiers.Shift, TerminalKeyAction.Repeat, "A", 'a'));
        Assert.Equal("\x1b[97;2:3u", Encode(term, TerminalKey.A,
            TerminalKeyModifiers.Shift, TerminalKeyAction.Release, unshifted: 'a'));
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
