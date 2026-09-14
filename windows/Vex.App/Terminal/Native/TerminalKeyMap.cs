using System.Windows.Input;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

/// <summary>Translates WPF's physical key identities and modifiers to the
/// platform-neutral values consumed by libghostty's keyboard encoder.</summary>
public static class TerminalKeyMap
{
    public static bool TryMap(Key key, out TerminalKey terminalKey)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            terminalKey = (TerminalKey)((int)TerminalKey.A + key - Key.A);
            return true;
        }
        if (key >= Key.D0 && key <= Key.D9)
        {
            terminalKey = (TerminalKey)((int)TerminalKey.Digit0 + key - Key.D0);
            return true;
        }
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            terminalKey = (TerminalKey)((int)TerminalKey.Numpad0 + key - Key.NumPad0);
            return true;
        }
        if (key >= Key.F1 && key <= Key.F24)
        {
            terminalKey = (TerminalKey)((int)TerminalKey.F1 + key - Key.F1);
            return true;
        }

        terminalKey = key switch
        {
            Key.Oem3 => TerminalKey.Backquote,
            Key.Oem5 => TerminalKey.Backslash,
            Key.Oem4 => TerminalKey.BracketLeft,
            Key.Oem6 => TerminalKey.BracketRight,
            Key.OemComma => TerminalKey.Comma,
            Key.OemPlus => TerminalKey.Equal,
            Key.OemMinus => TerminalKey.Minus,
            Key.OemPeriod => TerminalKey.Period,
            Key.OemQuotes => TerminalKey.Quote,
            Key.Oem1 => TerminalKey.Semicolon,
            Key.Oem2 => TerminalKey.Slash,
            Key.LeftAlt => TerminalKey.AltLeft,
            Key.RightAlt => TerminalKey.AltRight,
            Key.Back => TerminalKey.Backspace,
            Key.CapsLock => TerminalKey.CapsLock,
            Key.Apps => TerminalKey.ContextMenu,
            Key.LeftCtrl => TerminalKey.ControlLeft,
            Key.RightCtrl => TerminalKey.ControlRight,
            Key.Return => TerminalKey.Enter,
            Key.LWin => TerminalKey.MetaLeft,
            Key.RWin => TerminalKey.MetaRight,
            Key.LeftShift => TerminalKey.ShiftLeft,
            Key.RightShift => TerminalKey.ShiftRight,
            Key.Space => TerminalKey.Space,
            Key.Tab => TerminalKey.Tab,
            Key.Delete => TerminalKey.Delete,
            Key.End => TerminalKey.End,
            Key.Home => TerminalKey.Home,
            Key.Insert => TerminalKey.Insert,
            Key.PageDown => TerminalKey.PageDown,
            Key.PageUp => TerminalKey.PageUp,
            Key.Down => TerminalKey.ArrowDown,
            Key.Left => TerminalKey.ArrowLeft,
            Key.Right => TerminalKey.ArrowRight,
            Key.Up => TerminalKey.ArrowUp,
            Key.NumLock => TerminalKey.NumLock,
            Key.Add => TerminalKey.NumpadAdd,
            Key.Decimal => TerminalKey.NumpadDecimal,
            Key.Divide => TerminalKey.NumpadDivide,
            Key.Multiply => TerminalKey.NumpadMultiply,
            Key.Subtract => TerminalKey.NumpadSubtract,
            Key.Escape => TerminalKey.Escape,
            Key.PrintScreen => TerminalKey.PrintScreen,
            Key.Scroll => TerminalKey.ScrollLock,
            Key.Pause => TerminalKey.Pause,
            _ => TerminalKey.Unidentified,
        };
        return terminalKey != TerminalKey.Unidentified;
    }

    public static TerminalKeyModifiers MapModifiers(ModifierKeys modifiers)
    {
        var result = TerminalKeyModifiers.None;
        if ((modifiers & ModifierKeys.Shift) != 0) result |= TerminalKeyModifiers.Shift;
        if ((modifiers & ModifierKeys.Control) != 0) result |= TerminalKeyModifiers.Control;
        if ((modifiers & ModifierKeys.Alt) != 0) result |= TerminalKeyModifiers.Alt;
        if ((modifiers & ModifierKeys.Windows) != 0) result |= TerminalKeyModifiers.Super;
        return result;
    }

    public static bool IsTextKey(Key key) =>
        key is >= Key.A and <= Key.Z or
            >= Key.D0 and <= Key.D9 or
            >= Key.NumPad0 and <= Key.NumPad9 or
            Key.Space or Key.Oem1 or Key.Oem2 or Key.Oem3 or Key.Oem4 or Key.Oem5 or Key.Oem6 or
            Key.OemComma or Key.OemMinus or Key.OemPeriod or Key.OemPlus or Key.OemQuotes or
            Key.Add or Key.Decimal or Key.Divide or Key.Multiply or Key.Subtract;

    public static uint UnshiftedCodepoint(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
            return (uint)('a' + key - Key.A);
        if (key >= Key.D0 && key <= Key.D9)
            return (uint)('0' + key - Key.D0);
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return (uint)('0' + key - Key.NumPad0);
        return key == Key.Space ? (uint)' ' : 0;
    }
}
