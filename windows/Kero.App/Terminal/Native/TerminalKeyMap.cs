using System.Text;
using System.Windows.Input;
using XtermSharp;

namespace Kero.App.Terminal.Native;

/// <summary>
/// Maps WPF key events to the VT byte stream a real terminal would send,
/// honoring DECCKM (application cursor mode). Character keys are left out:
/// they arrive through <c>TextInput</c> as composed text.
/// </summary>
public static class TerminalKeyMap
{
    /// <summary>
    /// Returns the bytes to write to the PTY for <paramref name="key"/>, or
    /// null when the key produces regular text input instead.
    /// </summary>
    public static byte[]? Map(Key key, ModifierKeys modifiers, bool applicationCursor)
    {
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var alt = modifiers.HasFlag(ModifierKeys.Alt);

        // xterm modifier parameter: 1 + Shift(1) + Alt(2) + Ctrl(4).
        var mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

        switch (key)
        {
            case Key.Return: // same value as Key.Enter
                return mod > 1 ? Csi(mod, "~", 13) : EscapeSequences.CmdRet;
            case Key.Back:
                return EscapeSequences.CmdDel;
            case Key.Escape:
                return EscapeSequences.CmdEsc;
            case Key.Tab:
                return shift ? EscapeSequences.CmdBackTab : EscapeSequences.CmdTab;

            case Key.Up:
                return Arrow("A", EscapeSequences.MoveUpApp, EscapeSequences.MoveUpNormal);
            case Key.Down:
                return Arrow("B", EscapeSequences.MoveDownApp, EscapeSequences.MoveDownNormal);
            case Key.Right:
                return Arrow("C", EscapeSequences.MoveRightApp, EscapeSequences.MoveRightNormal);
            case Key.Left:
                return Arrow("D", EscapeSequences.MoveLeftApp, EscapeSequences.MoveLeftNormal);
            case Key.Home:
                return Arrow("H", EscapeSequences.MoveHomeApp, EscapeSequences.MoveHomeNormal);
            case Key.End:
                return Arrow("F", EscapeSequences.MoveEndApp, EscapeSequences.MoveEndNormal);

            case Key.Delete:
                return mod > 1 ? Csi(mod, "~", 3) : EscapeSequences.CmdDelKey;
            case Key.Insert:
                return Csi(mod, "~", 2);
            case Key.PageUp:
                return mod > 1 ? Csi(mod, "~", 5) : EscapeSequences.CmdPageUp;
            case Key.PageDown:
                return mod > 1 ? Csi(mod, "~", 6) : EscapeSequences.CmdPageDown;

            case Key.Space when ctrl:
                return new byte[] { 0 };

            case Key.Oem4 when ctrl:  // [
                return new byte[] { 0x1b };
            case Key.Oem5 when ctrl:  // backslash
                return new byte[] { 0x1c };
            case Key.Oem6 when ctrl:  // ]
                return new byte[] { 0x1d };
        }

        if (key >= Key.F1 && key <= Key.F12)
            return EscapeSequences.CmdF[key - Key.F1];

        if (key >= Key.A && key <= Key.Z)
        {
            var letter = (char)('a' + key - Key.A);
            if (ctrl)
                return new byte[] { (byte)(letter - 'a' + 1) };
            if (alt)
                return PrefixEsc(letter);
        }

        // Alt+<char> sends ESC + char (meta), like xterm's metaSendsEscape.
        if (alt && !ctrl)
        {
            var ch = KeyToChar(key, shift);
            if (ch != '\0')
                return PrefixEsc(ch);
        }

        return null;

        byte[] Arrow(string code, byte[] app, byte[] normal)
        {
            if (mod > 1)
                return Csi(mod, code);
            return applicationCursor ? app : normal;
        }
    }

    private static byte[] Csi(int mod, string final, int number = 1)
        => Encoding.ASCII.GetBytes($"\x1b[{number};{mod}{final}");

    private static byte[] PrefixEsc(char ch)
    {
        var body = Encoding.UTF8.GetBytes(new[] { ch });
        var result = new byte[body.Length + 1];
        result[0] = 0x1b;
        Array.Copy(body, 0, result, 1, body.Length);
        return result;
    }

    private static char KeyToChar(Key key, bool shift)
    {
        if (key >= Key.D0 && key <= Key.D9)
            return (char)('0' + key - Key.D0);
        return '\0';
    }
}
