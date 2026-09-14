using System.Windows.Input;
using Vex.App.Terminal.Native;
using Xunit;

namespace Vex.App.Tests;

/// <summary>
/// Locks down the WPF-to-VT key encoding. These bytes are a wire contract with
/// every TUI that runs in a pane, so a regression here surfaces as a broken
/// arrow key or a stray character in a remote shell rather than a crash.
/// </summary>
public sealed class TerminalKeyMapTests
{
    private const ModifierKeys None = ModifierKeys.None;
    private const ModifierKeys Ctrl = ModifierKeys.Control;
    private const ModifierKeys Shift = ModifierKeys.Shift;
    private const ModifierKeys Alt = ModifierKeys.Alt;

    private static string Map(Key key, ModifierKeys mods = None, bool appCursor = false)
    {
        var bytes = TerminalKeyMap.Map(key, mods, appCursor);
        return bytes is null ? "<null>" : string.Concat(bytes.Select(b => (char)b));
    }

    [Fact]
    public void PlainKeys_ProduceTheirControlByte()
    {
        Assert.Equal("\r", Map(Key.Return));
        Assert.Equal("\x7f", Map(Key.Back));
        Assert.Equal("\x1b", Map(Key.Escape));
        Assert.Equal("\t", Map(Key.Tab));
    }

    [Fact]
    public void ShiftTab_SendsBackTab()
    {
        Assert.Equal("\x1b[Z", Map(Key.Tab, Shift));
    }

    [Fact]
    public void CtrlBackspace_SendsEtb_SoShellsDeleteAWord()
    {
        // PSReadLine and readline bind 0x17 to backward-kill-word; this is what
        // makes Ctrl+Backspace match Windows Terminal.
        Assert.Equal("\x17", Map(Key.Back, Ctrl));
    }

    [Fact]
    public void CtrlBackspace_WithAlt_DoesNotDeleteAWord()
    {
        Assert.Equal("\x7f", Map(Key.Back, Ctrl | Alt));
    }

    [Fact]
    public void Arrows_HonorApplicationCursorMode()
    {
        Assert.Equal("\x1b[A", Map(Key.Up));
        Assert.Equal("\x1bOA", Map(Key.Up, None, appCursor: true));
        Assert.Equal("\x1b[D", Map(Key.Left));
        Assert.Equal("\x1bOD", Map(Key.Left, None, appCursor: true));
        Assert.Equal("\x1b[H", Map(Key.Home));
        Assert.Equal("\x1bOH", Map(Key.Home, None, appCursor: true));
        Assert.Equal("\x1b[F", Map(Key.End));
        Assert.Equal("\x1bOF", Map(Key.End, None, appCursor: true));
    }

    [Theory]
    // xterm modifier parameter: 1 + Shift(1) + Alt(2) + Ctrl(4).
    [InlineData(Shift, "\x1b[1;2A")]
    [InlineData(Alt, "\x1b[1;3A")]
    [InlineData(Shift | Alt, "\x1b[1;4A")]
    [InlineData(Ctrl, "\x1b[1;5A")]
    [InlineData(Ctrl | Shift, "\x1b[1;6A")]
    [InlineData(Ctrl | Alt, "\x1b[1;7A")]
    [InlineData(Ctrl | Shift | Alt, "\x1b[1;8A")]
    public void ModifiedArrows_EncodeTheXtermModifierParameter(ModifierKeys mods, string expected)
    {
        Assert.Equal(expected, Map(Key.Up, mods));
    }

    [Fact]
    public void ModifiedArrows_IgnoreApplicationCursorMode()
    {
        // The modifier form has no app-cursor variant; both modes agree.
        Assert.Equal("\x1b[1;5C", Map(Key.Right, Ctrl, appCursor: true));
    }

    [Fact]
    public void CtrlLetters_SendControlCodes()
    {
        Assert.Equal("\x01", Map(Key.A, Ctrl));
        Assert.Equal("\x03", Map(Key.C, Ctrl));
        Assert.Equal("\x1a", Map(Key.Z, Ctrl));
    }

    [Fact]
    public void AltLetters_SendEscapePrefixedLowerCase()
    {
        // \u is fixed-width, so the escape cannot swallow the letter the way
        // a variable-width \x1b literal would.
        Assert.Equal("\u001ba", Map(Key.A, Alt));
        Assert.Equal("\u001bz", Map(Key.Z, Alt));
    }

    [Fact]
    public void AltDigits_SendEscapePrefixedDigit()
    {
        Assert.Equal("\u001b5", Map(Key.D5, Alt));
    }

    [Fact]
    public void CtrlSpace_SendsNul()
    {
        Assert.Equal("\0", Map(Key.Space, Ctrl));
    }

    [Fact]
    public void CtrlBrackets_MapToEscapeAndControlCodes()
    {
        Assert.Equal("\x1b", Map(Key.Oem4, Ctrl));
        Assert.Equal("\x1c", Map(Key.Oem5, Ctrl));
        Assert.Equal("\x1d", Map(Key.Oem6, Ctrl));
    }

    [Fact]
    public void FunctionKeys_UseTheirStandardSequences()
    {
        Assert.Equal("\x1bOP", Map(Key.F1));
        Assert.Equal("\x1bOS", Map(Key.F4));
        Assert.Equal("\x1b[15~", Map(Key.F5));
        Assert.Equal("\x1b[24~", Map(Key.F12));
    }

    [Fact]
    public void NavKeys_UseTildeSequencesAndGainModifiers()
    {
        Assert.Equal("\x1b[3~", Map(Key.Delete));
        Assert.Equal("\x1b[2~", Map(Key.Insert));
        Assert.Equal("\x1b[5~", Map(Key.PageUp));
        Assert.Equal("\x1b[6~", Map(Key.PageDown));

        Assert.Equal("\x1b[3;5~", Map(Key.Delete, Ctrl));
        Assert.Equal("\x1b[5;2~", Map(Key.PageUp, Shift));
    }

    [Fact]
    public void CharacterKeys_ReturnNull_SoTextInputHandlesThem()
    {
        // Composed text arrives through TextInput; the key map must not
        // duplicate it, or every letter would be typed twice.
        Assert.Null(TerminalKeyMap.Map(Key.A, None, false));
        Assert.Null(TerminalKeyMap.Map(Key.D5, None, false));
    }
}
