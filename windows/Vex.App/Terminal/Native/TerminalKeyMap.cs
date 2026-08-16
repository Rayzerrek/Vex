using System.Text;
using System.Windows.Input;

namespace Vex.App.Terminal.Native;

/// <summary>
/// Maps WPF key events to the VT byte stream a real terminal would send,
/// honoring DECCKM (application cursor mode). Character keys are left out:
/// they arrive through <c>TextInput</c> as composed text. Precomputes and caches
/// all static byte sequences to eliminate GC allocations during keyboard interaction.
/// </summary>
public static class TerminalKeyMap
{
    private static readonly byte[] CmdRet = { (byte)'\r' };
    private static readonly byte[] CmdDel = { 0x7f };
    private static readonly byte[] CmdEsc = { 0x1b };
    private static readonly byte[] CmdTab = { (byte)'\t' };
    private static readonly byte[] CmdBackTab = "\x1b[Z"u8.ToArray();
    private static readonly byte[] CmdDelKey = "\x1b[3~"u8.ToArray();
    private static readonly byte[] CmdPageUp = "\x1b[5~"u8.ToArray();
    private static readonly byte[] CmdPageDown = "\x1b[6~"u8.ToArray();
    private static readonly byte[] CmdInsert = "\x1b[2~"u8.ToArray();

    private static readonly byte[] CmdZero = { 0 };
    private static readonly byte[] CmdCtrlBackslash = { 0x1c };
    private static readonly byte[] CmdCtrlCloseBracket = { 0x1d };

    private static readonly byte[] CmdUpNormal = "\x1b[A"u8.ToArray();
    private static readonly byte[] CmdDownNormal = "\x1b[B"u8.ToArray();
    private static readonly byte[] CmdRightNormal = "\x1b[C"u8.ToArray();
    private static readonly byte[] CmdLeftNormal = "\x1b[D"u8.ToArray();
    private static readonly byte[] CmdHomeNormal = "\x1b[H"u8.ToArray();
    private static readonly byte[] CmdEndNormal = "\x1b[F"u8.ToArray();

    private static readonly byte[] CmdUpApp = "\x1bOA"u8.ToArray();
    private static readonly byte[] CmdDownApp = "\x1bOB"u8.ToArray();
    private static readonly byte[] CmdRightApp = "\x1bOC"u8.ToArray();
    private static readonly byte[] CmdLeftApp = "\x1bOD"u8.ToArray();
    private static readonly byte[] CmdHomeApp = "\x1bOH"u8.ToArray();
    private static readonly byte[] CmdEndApp = "\x1bOF"u8.ToArray();

    // Precomputed modified arrow keys: [direction 0..5, mod - 2]
    // directions: 0=Up, 1=Down, 2=Right, 3=Left, 4=Home, 5=End
    // mod: 2..8 (Shift=2, Alt=3, Alt+Shift=4, Ctrl=5, Ctrl+Shift=6, Ctrl+Alt=7, Ctrl+Alt+Shift=8)
    private static readonly byte[][][] CmdModifiedArrows = BuildModifiedArrows();

    private static byte[][][] BuildModifiedArrows()
    {
        char[] codes = { 'A', 'B', 'C', 'D', 'H', 'F' };
        var result = new byte[6][][];
        for (var dir = 0; dir < 6; dir++)
        {
            result[dir] = new byte[7][];
            for (var m = 2; m <= 8; m++)
            {
                result[dir][m - 2] = Encoding.ASCII.GetBytes($"\x1b[1;{m}{codes[dir]}");
            }
        }
        return result;
    }

    private static readonly byte[][] CmdF1_F4 =
    {
        "\x1bOP"u8.ToArray(),
        "\x1bOQ"u8.ToArray(),
        "\x1bOR"u8.ToArray(),
        "\x1bOS"u8.ToArray(),
    };

    private static readonly byte[][] CmdF5_F12 =
    {
        "\x1b[15~"u8.ToArray(), // F5
        "\x1b[17~"u8.ToArray(), // F6
        "\x1b[18~"u8.ToArray(), // F7
        "\x1b[19~"u8.ToArray(), // F8
        "\x1b[20~"u8.ToArray(), // F9
        "\x1b[21~"u8.ToArray(), // F10
        "\x1b[23~"u8.ToArray(), // F11
        "\x1b[24~"u8.ToArray(), // F12
    };

    private static readonly byte[][] CmdCtrlLetters = BuildCtrlLetters();
    private static readonly byte[][] CmdAltLetters = BuildAltLetters();
    private static readonly byte[][] CmdAltDigits = BuildAltDigits();

    private static byte[][] BuildCtrlLetters()
    {
        var array = new byte[26][];
        for (var i = 0; i < 26; i++)
            array[i] = new byte[] { (byte)(i + 1) };
        return array;
    }

    private static byte[][] BuildAltLetters()
    {
        var array = new byte[26][];
        for (var i = 0; i < 26; i++)
            array[i] = new byte[] { 0x1b, (byte)('a' + i) };
        return array;
    }

    private static byte[][] BuildAltDigits()
    {
        var array = new byte[10][];
        for (var i = 0; i < 10; i++)
            array[i] = new byte[] { 0x1b, (byte)('0' + i) };
        return array;
    }

    private static byte[] Csi(string sequence) => Encoding.ASCII.GetBytes(sequence);

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
                return CmdRet;
            case Key.Back:
                return CmdDel;
            case Key.Escape:
                return CmdEsc;
            case Key.Tab:
                return shift ? CmdBackTab : CmdTab;

            case Key.Up:
                return Arrow(0, "A", CmdUpApp, CmdUpNormal);
            case Key.Down:
                return Arrow(1, "B", CmdDownApp, CmdDownNormal);
            case Key.Right:
                return Arrow(2, "C", CmdRightApp, CmdRightNormal);
            case Key.Left:
                return Arrow(3, "D", CmdLeftApp, CmdLeftNormal);
            case Key.Home:
                return Arrow(4, "H", CmdHomeApp, CmdHomeNormal);
            case Key.End:
                return Arrow(5, "F", CmdEndApp, CmdEndNormal);

            case Key.Delete:
                return mod > 1 ? Csi(mod, "~", 3) : CmdDelKey;
            case Key.Insert:
                return mod > 1 ? Csi(mod, "~", 2) : CmdInsert;
            case Key.PageUp:
                return mod > 1 ? Csi(mod, "~", 5) : CmdPageUp;
            case Key.PageDown:
                return mod > 1 ? Csi(mod, "~", 6) : CmdPageDown;

            case Key.Space when ctrl:
                return CmdZero;

            case Key.Oem4 when ctrl:  // [
                return CmdEsc;
            case Key.Oem5 when ctrl:  // backslash
                return CmdCtrlBackslash;
            case Key.Oem6 when ctrl:  // ]
                return CmdCtrlCloseBracket;
        }

        if (key >= Key.F1 && key <= Key.F4)
            return CmdF1_F4[key - Key.F1];
        if (key >= Key.F5 && key <= Key.F12)
            return CmdF5_F12[key - Key.F5];

        if (key >= Key.A && key <= Key.Z)
        {
            var idx = key - Key.A;
            if (ctrl)
                return CmdCtrlLetters[idx];
            if (alt)
                return CmdAltLetters[idx];
        }

        // Alt+<digit> sends ESC + digit (meta), like xterm's metaSendsEscape.
        if (alt && !ctrl && key >= Key.D0 && key <= Key.D9)
        {
            return CmdAltDigits[key - Key.D0];
        }

        return null;

        byte[] Arrow(int dirIndex, string code, byte[] app, byte[] normal)
        {
            if (mod >= 2 && mod <= 8)
                return CmdModifiedArrows[dirIndex][mod - 2];
            if (mod > 8)
                return Csi(mod, code);
            return applicationCursor ? app : normal;
        }
    }

    private static byte[] Csi(int mod, string final, int number = 1)
        => Encoding.ASCII.GetBytes($"\x1b[{number};{mod}{final}");
}
