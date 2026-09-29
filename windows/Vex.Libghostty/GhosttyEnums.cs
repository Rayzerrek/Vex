namespace Vex.Libghostty;

[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Underline = 1 << 2,
    Strikethrough = 1 << 3,
    Inverse = 1 << 4,
    Blink = 1 << 5,
    Invisible = 1 << 6,
    Faint = 1 << 7,
}

public enum FrameDirty
{
    Clean = 0,
    Partial = 1,
    Full = 2,
}

public enum CursorShape
{
    Bar = 0,
    Block = 1,
    Underline = 2,
    BlockHollow = 3,
}

public enum MouseInputAction
{
    Press = 0,
    Release = 1,
    Motion = 2,
}

/// <summary>libghostty's GhosttyMouseTrackingMode: which DEC tracking mode
/// (9/1000/1002/1003) the mouse encoder should report events under.</summary>
public enum MouseTrackingMode
{
    None = 0,
    X10 = 1,
    Normal = 2,
    Button = 3,
    Any = 4,
}

/// <summary>libghostty's GhosttyMouseFormat: which wire format (X10/UTF-8/
/// SGR/URXVT/SGR-pixels) the mouse encoder should emit.</summary>
public enum MouseFormat
{
    X10 = 0,
    Utf8 = 1,
    Sgr = 2,
    Urxvt = 3,
    SgrPixels = 4,
}

public enum MouseInputButton
{
    Left = 1,
    Right = 2,
    Middle = 3,
    WheelUp = 4,
    WheelDown = 5,
    WheelLeft = 6,
    WheelRight = 7,
}

public enum TerminalKeyAction
{
    Release = 0,
    Press = 1,
    Repeat = 2,
}

[Flags]
public enum TerminalKeyModifiers : ushort
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Alt = 1 << 2,
    Super = 1 << 3,
    CapsLock = 1 << 4,
    NumLock = 1 << 5,
    RightShift = 1 << 6,
    RightControl = 1 << 7,
    RightAlt = 1 << 8,
    RightSuper = 1 << 9,
}

public enum TerminalKey
{
    Unidentified = 0,
    Backquote = 1,
    Backslash = 2,
    BracketLeft = 3,
    BracketRight = 4,
    Comma = 5,
    Digit0 = 6,
    Digit1 = 7,
    Digit2 = 8,
    Digit3 = 9,
    Digit4 = 10,
    Digit5 = 11,
    Digit6 = 12,
    Digit7 = 13,
    Digit8 = 14,
    Digit9 = 15,
    Equal = 16,
    A = 20,
    B = 21,
    C = 22,
    D = 23,
    E = 24,
    F = 25,
    G = 26,
    H = 27,
    I = 28,
    J = 29,
    K = 30,
    L = 31,
    M = 32,
    N = 33,
    O = 34,
    P = 35,
    Q = 36,
    R = 37,
    S = 38,
    T = 39,
    U = 40,
    V = 41,
    W = 42,
    X = 43,
    Y = 44,
    Z = 45,
    Minus = 46,
    Period = 47,
    Quote = 48,
    Semicolon = 49,
    Slash = 50,
    AltLeft = 51,
    AltRight = 52,
    Backspace = 53,
    CapsLock = 54,
    ContextMenu = 55,
    ControlLeft = 56,
    ControlRight = 57,
    Enter = 58,
    MetaLeft = 59,
    MetaRight = 60,
    ShiftLeft = 61,
    ShiftRight = 62,
    Space = 63,
    Tab = 64,
    Delete = 68,
    End = 69,
    Home = 71,
    Insert = 72,
    PageDown = 73,
    PageUp = 74,
    ArrowDown = 75,
    ArrowLeft = 76,
    ArrowRight = 77,
    ArrowUp = 78,
    NumLock = 79,
    Numpad0 = 80,
    Numpad1 = 81,
    Numpad2 = 82,
    Numpad3 = 83,
    Numpad4 = 84,
    Numpad5 = 85,
    Numpad6 = 86,
    Numpad7 = 87,
    Numpad8 = 88,
    Numpad9 = 89,
    NumpadAdd = 90,
    NumpadDecimal = 95,
    NumpadDivide = 96,
    NumpadEnter = 97,
    NumpadMultiply = 104,
    NumpadSubtract = 107,
    Escape = 120,
    F1 = 121,
    F2 = 122,
    F3 = 123,
    F4 = 124,
    F5 = 125,
    F6 = 126,
    F7 = 127,
    F8 = 128,
    F9 = 129,
    F10 = 130,
    F11 = 131,
    F12 = 132,
    F13 = 133,
    F14 = 134,
    F15 = 135,
    F16 = 136,
    F17 = 137,
    F18 = 138,
    F19 = 139,
    F20 = 140,
    F21 = 141,
    F22 = 142,
    F23 = 143,
    F24 = 144,
    PrintScreen = 148,
    ScrollLock = 149,
    Pause = 150,
}

[Flags]
public enum MouseInputModifiers : ushort
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

public enum ColorTag : int
{
    None = 0,
    Palette = 1,
    Rgb = 2,
}
