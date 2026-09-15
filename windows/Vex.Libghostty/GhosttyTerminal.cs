using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static Vex.Libghostty.Native;

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

public readonly record struct CursorState(
    int X, int Y, bool Visible, bool Blinking, CursorShape Shape);

public enum ColorTag : int
{
    None = 0,
    Palette = 1,
    Rgb = 2,
}

/// <summary>
/// One rendered cell: the grapheme text ("" for empty cells and wide-char
/// spacer tails), the grid width, style flags, and the cell's fg/bg colors
/// as tagged values (palette index or packed RGB, or None for the default).
/// </summary>
public struct CellInfo
{
    public string Text;
    public bool Wide;
    public bool Tail;
    public CellFlags Flags;
    public ColorTag FgTag;
    public int FgValue;
    public ColorTag BgTag;
    public int BgValue;
}

public sealed class FrameRow
{
    public bool Dirty;
    public bool HasSelection;
    public int SelectionStart;
    public int SelectionEnd;
    public CellInfo[] Cells = Array.Empty<CellInfo>();
}

/// <summary>
/// Managed wrapper over one libghostty-vt terminal plus its render state.
/// All native calls are serialized through <see cref="SyncRoot"/>: VT input
/// is fed on the PTY reader thread so query responses (DSR, OSC color
/// reports, DA) go back to the child immediately, while the WPF UI thread
/// only takes the lock to snapshot render state.
/// </summary>
public sealed class GhosttyTerminal : IDisposable
{
    private const int MaxGraphemes = 32;

    private static readonly TitleChangedFn s_titleChanged = OnTitleChanged;
    private static readonly BellFn s_bell = OnBell;
    private static readonly WritePtyFn s_writePty = OnWritePty;

    private IntPtr _terminal;
    private IntPtr _renderState;
    private IntPtr _rowIterator;
    private IntPtr _rowCells;
    private IntPtr _gesture;
    private IntPtr _pressEvent;
    private IntPtr _dragEvent;
    private IntPtr _releaseEvent;
    private IntPtr _mouseEncoder;
    private IntPtr _mouseEvent;
    private IntPtr _keyEncoder;
    private IntPtr _keyEvent;
    private int _mouseAnyButtonPressed = -1;
    private MouseTrackingMode _lastEncodedTracking = (MouseTrackingMode)(-1);
    private MouseFormat _lastEncodedFormat = (MouseFormat)(-1);
    private GCHandle _selfHandle;
    private uint[] _codepoints = new uint[MaxGraphemes];
    private bool _disposed;

    // Serializes every native terminal call. Feed runs on the PTY reader
    // thread while snapshots (UpdateFrame, modes, mouse, selection) run on
    // the UI thread. Callbacks (WritePty, TitleChanged) fire on the feeding
    // thread while the lock is held; C# monitors are re-entrant, so they may
    // safely re-enter locked accessors on the same thread. Lock ordering is
    // always terminal-lock before session-write-lock, never the reverse.
    private readonly object _vtLock = new();
    public object SyncRoot => _vtLock;

    // OSC 133 (FTCS shell-integration) filter state. Vex does not implement
    // shell integration, and ghostty-vt's "fresh line" handling of OSC 133;A
    // moves the cursor when it is not at column 0. A shell (nushell) sends
    // these markers on every prompt redraw, so after a full-screen TUI exits
    // the mid-line marker forces a line feed and the prompt is drawn twice.
    // Stripping the sequence makes the emulator ignore it like an unknown OSC.
    //
    // The same filter also drops bare DCS query payloads. ConPTY eats the DCS
    // wrapper (introducer and terminator) of client queries such as nvim's
    // XTGETTCAP (`ESC P + q 4D73 ST`) and DECRQSS (`ESC P $ q m ST`), so only
    // the middles (`+q4D73`, `$qm`) reach the emulator — which would print
    // them as visible garbage. The payloads are matched against known probes
    // (whitelist, never a bare prefix) so ordinary text is never eaten.
    private enum FeedState { Normal, EscapeSeen, InOsc, InOscEscapeSeen, InCsi, InDcs, InDcsEsc, PlusSeen, InPlusQuery, DollarSeen, DollarQSeen }
    private FeedState _feedState;
    private readonly byte[] _oscBuf = new byte[1024];
    private int _oscLen;
    private readonly byte[] _plusBuf = new byte[256];
    private int _plusLen;

    /// <summary>
    /// Hex-encoded capability names of XTGETTCAP probes worth hiding when
    /// ConPTY delivers them without their DCS wrapper. Compared
    /// case-insensitively; every `;`-separated token of a candidate must be
    /// listed, otherwise the candidate is ordinary text and passes through.
    /// </summary>
    private static readonly HashSet<string> s_strippedCaps = new(StringComparer.OrdinalIgnoreCase)
    {
        "5463", // Tc
        "524742", // RGB
        "4D73", // Ms
        "536D756C78", // Smulx (extended underline)
        "53796E63", // Sync (synchronized output)
        "73657472676266", // setrgbf
        "73657472676262", // setrgbb
        "536574756C63", // Setulc (underline color)
        "544E", // TN
        "436F", // Co
    };

    public event Action<string>? TitleChanged;
    public event Action<byte[], int>? WritePty;

    /// <summary>Raised when the application rings the terminal bell (BEL).
    /// Fires on the feed thread, like <see cref="TitleChanged"/>.</summary>
    public event Action? Bell;

    public FrameDirty FrameDirty { get; private set; }
    public CursorState Cursor { get; private set; }
    public FrameRow[] FrameRows { get; private set; } = Array.Empty<FrameRow>();

    private int _cols;
    private int _rows;

    private int _cellWidthPx = 8;
    private int _cellHeightPx = 16;

    public GhosttyTerminal(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        Check(Native.ghostty_terminal_new(IntPtr.Zero, out _terminal, (ushort)cols, (ushort)rows), "terminal_new");

        _selfHandle = GCHandle.Alloc(this);
        SetOption(TerminalOption.Userdata, GCHandle.ToIntPtr(_selfHandle));
        SetOption(TerminalOption.TitleChanged, Marshal.GetFunctionPointerForDelegate(s_titleChanged));
        SetOption(TerminalOption.Bell, Marshal.GetFunctionPointerForDelegate(s_bell));
        SetOption(TerminalOption.WritePty, Marshal.GetFunctionPointerForDelegate(s_writePty));
        // Use the familiar text-insertion bar by default. Applications can
        // still select another DECSCUSR shape (for example vim's block mode).
        var defaultCursorStyle = TerminalCursorStyle.Bar;
        SetOption(TerminalOption.DefaultCursorStyle, defaultCursorStyle);

        // xterm's default cursor (DECSCUSR 0) blinks; Vex's caret follows
        // the terminal unless an app forces a steady cursor.
        var defaultCursorBlink = true;
        SetOption(TerminalOption.DefaultCursorBlink, defaultCursorBlink);

        Check(Native.ghostty_render_state_new(IntPtr.Zero, out _renderState), "render_state_new");
        Check(Native.ghostty_render_state_row_iterator_new(IntPtr.Zero, out _rowIterator), "row_iterator_new");
        Check(Native.ghostty_render_state_row_cells_new(IntPtr.Zero, out _rowCells), "row_cells_new");
        Check(Native.ghostty_selection_gesture_new(IntPtr.Zero, out _gesture), "gesture_new");
        Check(Native.ghostty_selection_gesture_event_new(IntPtr.Zero, out _pressEvent, SelectionGestureEventType.Press), "event_new press");
        Check(Native.ghostty_selection_gesture_event_new(IntPtr.Zero, out _dragEvent, SelectionGestureEventType.Drag), "event_new drag");
        Check(Native.ghostty_selection_gesture_event_new(IntPtr.Zero, out _releaseEvent, SelectionGestureEventType.Release), "event_new release");
        Check(Native.ghostty_mouse_encoder_new(IntPtr.Zero, out _mouseEncoder), "mouse_encoder_new");
        Check(Native.ghostty_mouse_event_new(IntPtr.Zero, out _mouseEvent), "mouse_event_new");
        Check(Native.ghostty_key_encoder_new(IntPtr.Zero, out _keyEncoder), "key_encoder_new");
        Check(Native.ghostty_key_event_new(IntPtr.Zero, out _keyEvent), "key_event_new");

        byte trackLastCell = 1;
        unsafe
        {
            Native.ghostty_mouse_encoder_setopt(_mouseEncoder, MouseEncoderOption.TrackLastCell, (IntPtr)(&trackLastCell));
        }
        ConfigureMouseEncoderSize();

        var grid = new GhosttySelectionGestureBehaviors
        {
            singleClick = 0, // cell
            doubleClick = 1, // word
            tripleClick = 2, // line
        };
        var nowNs = NowNs();
        var repeatIntervalNs = 500_000_000UL;
        const double repeatDistance = 8.0;
        SetEventOption(_pressEvent, SelectionGestureEventOption.Behaviors, grid);
        SetEventOption(_pressEvent, SelectionGestureEventOption.TimeNs, nowNs);
        SetEventOption(_pressEvent, SelectionGestureEventOption.RepeatIntervalNs, repeatIntervalNs);
        SetEventOption(_pressEvent, SelectionGestureEventOption.RepeatDistance, repeatDistance);
    }

    // ---- Feed ------------------------------------------------------------

    public void Feed(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Feed(bytes, 0, bytes.Length);
    }

    public unsafe void Feed(byte[] data, int offset, int count)
    {
        lock (_vtLock)
        {
            FeedLocked(data, offset, count);
        }
    }

    private unsafe void FeedLocked(byte[] data, int offset, int count)
    {
        if (count <= 0 || _disposed)
            return;

        // Fast path: no ESC byte, no query introducer, and no half-parsed
        // sequence pending, so the chunk goes straight to the emulator
        // without the pooled-array copy the filter below needs. Only `+q`
        // and `$q` pairs (ConPTY-stripped DCS payloads carry no ESC to key
        // off) force the slow path — lone `+`/`$` (`a+b`, `$env:`) would pass
        // through the filter unchanged anyway. A trailing `+`/`$` also takes
        // the slow path so a pair split across Feed calls is still caught.
        if (_feedState == FeedState.Normal && !NeedsSlowPath(data, offset, count))
        {
            fixed (byte* p = data)
                Native.ghostty_terminal_vt_write(_terminal, (IntPtr)(p + offset), (nuint)count);
            return;
        }

        // Strip OSC 133 sequences (see the filter state above) before feeding,
        // tolerating sequences that are split across separate Feed calls.
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(count + _oscBuf.Length + 4);
        try
        {
            var written = 0;
            for (var i = offset; i < offset + count; i++)
            {
                var b = data[i];
                switch (_feedState)
                {
                    case FeedState.Normal:
                        if (b == 0x1B)
                            _feedState = FeedState.EscapeSeen;
                        else if (b == (byte)'+')
                            _feedState = FeedState.PlusSeen;
                        else if (b == (byte)'$')
                            _feedState = FeedState.DollarSeen;
                        else
                            buffer[written++] = b;
                        break;
                    case FeedState.EscapeSeen:
                        if (b == 0x5D) // ESC ] starts an OSC sequence
                        {
                            _feedState = FeedState.InOsc;
                            _oscLen = 0;
                        }
                        else if (b == (byte)'P' || b == (byte)'X' || b == (byte)'^' || b == (byte)'_')
                        {
                            // DCS/SOS/PM/APC: arbitrary payload that may itself
                            // contain `+q`/`$q` (XTGETTCAP, DECRQSS). Pass it
                            // through untouched until its terminator so the
                            // bare-payload matcher below never fires inside a
                            // real sequence.
                            buffer[written++] = 0x1B;
                            buffer[written++] = b;
                            _feedState = FeedState.InDcs;
                        }
                        else if (b == (byte)'[')
                        {
                            // CSI: params/intermediates may contain `+`/`$`
                            // (DECRQM `$p`); same passthrough requirement.
                            buffer[written++] = 0x1B;
                            buffer[written++] = b;
                            _feedState = FeedState.InCsi;
                        }
                        else
                        {
                            buffer[written++] = 0x1B;
                            buffer[written++] = b;
                            _feedState = FeedState.Normal;
                        }
                        break;
                    case FeedState.InOsc:
                        if (b == 0x07) // BEL terminates the OSC string
                        {
                            FlushOscBel(buffer, ref written);
                            _feedState = FeedState.Normal;
                        }
                        else if (b == 0x1B) // possible ST (ESC \) terminator
                        {
                            _feedState = FeedState.InOscEscapeSeen;
                        }
                        else if (_oscLen < _oscBuf.Length)
                        {
                            _oscBuf[_oscLen++] = b;
                        }
                        else
                        {
                            // Pathological oversized OSC: emit verbatim and bail.
                            buffer[written++] = 0x1B;
                            buffer[written++] = 0x5D;
                            for (var j = 0; j < _oscLen; j++)
                                buffer[written++] = _oscBuf[j];
                            buffer[written++] = b;
                            _feedState = FeedState.Normal;
                        }
                        break;
                    case FeedState.InOscEscapeSeen:
                        if (b == 0x5C) // ST terminator: ESC \
                        {
                            FlushOscSt(buffer, ref written);
                            _feedState = FeedState.Normal;
                        }
                        else
                        {
                            // A lone ESC inside the OSC string, not a terminator.
                            if (_oscLen < _oscBuf.Length)
                                _oscBuf[_oscLen++] = 0x1B;
                            if (_oscLen < _oscBuf.Length)
                                _oscBuf[_oscLen++] = b;
                            _feedState = FeedState.InOsc;
                        }
                        break;
                    case FeedState.PlusSeen:
                        if (b == (byte)'q')
                        {
                            _feedState = FeedState.InPlusQuery;
                            _plusLen = 0;
                        }
                        else
                        {
                            // Ordinary text (`a+b`, `C++`): emit the held `+`
                            // and reprocess this byte as Normal.
                            buffer[written++] = (byte)'+';
                            _feedState = FeedState.Normal;
                            i--;
                        }
                        break;
                    case FeedState.InPlusQuery:
                        if (IsQueryByte(b) && _plusLen < _plusBuf.Length)
                        {
                            _plusBuf[_plusLen++] = b;
                        }
                        else if (_plusLen >= _plusBuf.Length)
                        {
                            // Pathological run: fail open and emit verbatim.
                            buffer[written++] = (byte)'+';
                            buffer[written++] = (byte)'q';
                            for (var j = 0; j < _plusLen; j++)
                                buffer[written++] = _plusBuf[j];
                            buffer[written++] = b;
                            _feedState = FeedState.Normal;
                        }
                        else if (IsStrippedQuery(_plusBuf, _plusLen))
                        {
                            // A ConPTY-stripped XTGETTCAP probe: drop it, then
                            // reprocess the terminating byte as Normal (it is
                            // usually the ESC of whatever the app sent next).
                            _feedState = FeedState.Normal;
                            i--;
                        }
                        else
                        {
                            // Not a known probe: emit everything verbatim.
                            buffer[written++] = (byte)'+';
                            buffer[written++] = (byte)'q';
                            for (var j = 0; j < _plusLen; j++)
                                buffer[written++] = _plusBuf[j];
                            _feedState = FeedState.Normal;
                            i--;
                        }
                        break;
                    case FeedState.DollarSeen:
                        if (b == (byte)'q')
                        {
                            _feedState = FeedState.DollarQSeen;
                        }
                        else
                        {
                            // Ordinary text (`$env:`, `$100`): emit the held
                            // `$` and reprocess this byte as Normal.
                            buffer[written++] = (byte)'$';
                            _feedState = FeedState.Normal;
                            i--;
                        }
                        break;
                    case FeedState.DollarQSeen:
                        if (b == (byte)'m')
                        {
                            // nvim's undercurl DECRQSS probe (`ESC P $ q m`)
                            // without its DCS wrapper: drop it. Anything else
                            // after `$q` (shell `$query`, …) is text.
                            _feedState = FeedState.Normal;
                        }
                        else
                        {
                            buffer[written++] = (byte)'$';
                            buffer[written++] = (byte)'q';
                            _feedState = FeedState.Normal;
                            i--;
                        }
                        break;
                    case FeedState.InCsi:
                        if (b == 0x1B)
                        {
                            // ESC aborts CSI and starts a new sequence.
                            _feedState = FeedState.EscapeSeen;
                        }
                        else
                        {
                            buffer[written++] = b;
                            if (b >= 0x40 && b <= 0x7E)
                                _feedState = FeedState.Normal;
                        }
                        break;
                    case FeedState.InDcs:
                        if (b == 0x07) // BEL terminates some DCS uses
                        {
                            buffer[written++] = b;
                            _feedState = FeedState.Normal;
                        }
                        else if (b == 0x1B) // possible ST (ESC \) terminator
                        {
                            _feedState = FeedState.InDcsEsc;
                        }
                        else
                        {
                            buffer[written++] = b;
                        }
                        break;
                    case FeedState.InDcsEsc:
                        if (b == 0x5C) // ST terminator: ESC \
                        {
                            buffer[written++] = 0x1B;
                            buffer[written++] = 0x5C;
                            _feedState = FeedState.Normal;
                        }
                        else
                        {
                            // A lone ESC inside the payload, not a terminator.
                            buffer[written++] = 0x1B;
                            buffer[written++] = b;
                            _feedState = FeedState.InDcs;
                        }
                        break;
                }
            }
            if (written > 0)
                Native.ghostty_terminal_vt_write(_terminal, buffer, (nuint)written);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Re-emits a buffered OSC sequence (with its terminator) unless it is an
    /// OSC 133 semantic-prompt marker, which is dropped entirely. BEL and ST
    /// are the only terminators the feed filter emits, so the terminator
    /// bytes are inlined instead of allocating a params array.
    /// </summary>
    private void FlushOscBel(byte[] dst, ref int written)
    {
        if (IsOsc133(_oscBuf, _oscLen))
            return;
        dst[written++] = 0x1B;
        dst[written++] = 0x5D;
        for (var j = 0; j < _oscLen; j++)
            dst[written++] = _oscBuf[j];
        dst[written++] = 0x07;
    }

    private void FlushOscSt(byte[] dst, ref int written)
    {
        if (IsOsc133(_oscBuf, _oscLen))
            return;
        dst[written++] = 0x1B;
        dst[written++] = 0x5D;
        for (var j = 0; j < _oscLen; j++)
            dst[written++] = _oscBuf[j];
        dst[written++] = 0x1B;
        dst[written++] = 0x5C;
    }

    private static bool IsOsc133(byte[] buf, int len)
    {
        // The OSC number is the leading decimal digits; distinguish 133 from
        // 1337 (iTerm2 shell integration) by reading every leading digit.
        var num = 0;
        for (var i = 0; i < len && buf[i] >= (byte)'0' && buf[i] <= (byte)'9'; i++)
        {
            if (num < 10000)
                num = num * 10 + (buf[i] - '0');
        }
        return num == 133;
    }

    private static bool IsQueryByte(byte b) =>
        (b >= (byte)'0' && b <= (byte)'9')
        || (b >= (byte)'A' && b <= (byte)'F')
        || (b >= (byte)'a' && b <= (byte)'f')
        || b == (byte)';';

    /// <summary>
    /// Single vectorized pass deciding whether the chunk needs the byte loop
    /// below: any ESC, any `+q`/`$q` pair, or a trailing `+`/`$` that could
    /// pair across the Feed boundary. Everything else is filter-neutral.
    /// Scanning for `q` (rare) instead of `+`/`$` (common in code and shells)
    /// keeps the scan to one pass with almost no scalar steps.
    /// </summary>
    private static bool NeedsSlowPath(byte[] data, int offset, int count)
    {
        var span = data.AsSpan(offset, count);
        if (span.IsEmpty)
            return false;
        if (span[^1] is (byte)'+' or (byte)'$')
            return true;
        var i = 0;
        while (i < span.Length)
        {
            var j = span[i..].IndexOfAny((byte)0x1B, (byte)'q');
            if (j < 0)
                return false;
            i += j;
            if (span[i] == 0x1B)
                return true;
            // A `q` only matters when directly preceded by `+`/`$`. A pair
            // split across chunks is already covered: the previous chunk
            // ended with `+`/`$`, which forced the slow path above.
            if (i > 0 && span[i - 1] is (byte)'+' or (byte)'$')
                return true;
            i++;
        }
        return false;
    }

    /// <summary>
    /// True when the buffered `+q…` payload is a known XTGETTCAP probe: every
    /// `;`-separated token must be a listed capability. Anything else is
    /// ordinary text (`a+q1B`, base64, …) and must pass through untouched.
    /// </summary>
    private static bool IsStrippedQuery(byte[] buf, int len)
    {
        if (len == 0)
            return false;
        var start = 0;
        for (var i = 0; i <= len; i++)
        {
            if (i == len || buf[i] == (byte)';')
            {
                if (i == start)
                    return false;
                if (!IsKnownCap(buf, start, i - start))
                    return false;
                start = i + 1;
            }
        }
        return true;
    }

    private static bool IsKnownCap(byte[] buf, int offset, int length)
    {
        // Queries are rare (a few per TUI startup), so a short string per
        // token against the case-insensitive set is fine.
        var token = Encoding.ASCII.GetString(buf, offset, length);
        return s_strippedCaps.Contains(token);
    }

    public void Reset()
    {
        lock (_vtLock)
        {
            // Pending one-byte candidates (`+`, `$`, half OSC/DCS payloads)
            // are dropped with the state; Reset has no callers today and the
            // most that can vanish is an unflushed probe fragment.
            _feedState = FeedState.Normal;
            _oscLen = 0;
            _plusLen = 0;
            Native.ghostty_terminal_reset(_terminal);
        }
    }

    // ---- Geometry / colors ------------------------------------------------

    public void Resize(int cols, int rows, int cellWidthPx, int cellHeightPx)
    {
        lock (_vtLock)
        {
            _cols = cols;
            _rows = rows;
            _cellWidthPx = cellWidthPx;
            _cellHeightPx = cellHeightPx;
            Check(Native.ghostty_terminal_resize(_terminal, (ushort)cols, (ushort)rows, (uint)cellWidthPx, (uint)cellHeightPx), "resize");
            ConfigureMouseEncoderSize();
        }
    }

    public unsafe void SetDefaultColors(GhosttyColorRgb foreground, GhosttyColorRgb background,
        GhosttyColorRgb cursor, GhosttyColorRgb[] palette256)
    {
        lock (_vtLock)
        {
            SetOption(TerminalOption.ColorForeground, foreground);
            SetOption(TerminalOption.ColorBackground, background);
            SetOption(TerminalOption.ColorCursor, cursor);
            fixed (GhosttyColorRgb* palette = palette256)
                Check(Native.ghostty_terminal_set(_terminal, TerminalOption.ColorPalette, (IntPtr)palette), "set palette");
        }
    }

    // ---- Modes ------------------------------------------------------------

    public bool IsAlternateScreen
    {
        get
        {
            lock (_vtLock)
                return TryGet(TerminalData.ActiveScreen, out TerminalScreen screen) && screen == TerminalScreen.Alternate;
        }
    }

    /// <summary>Mouse tracking mode read from the live DEC private mode bits.
    /// This deliberately avoids libghostty's cached last-transition flag: that
    /// cache collapses to "none" when an application resets one tracking mode
    /// while another is still set (e.g. 1000h 1002h 1000l), which would make
    /// every encoded report empty even though the app still tracks the mouse.
    /// Single lock acquisition; the encoding hot path uses the Locked variant
    /// to avoid re-entering the monitor per mode read.</summary>
    public MouseTrackingMode TrackingMode
    {
        get
        {
            lock (_vtLock)
                return TrackingModeLocked;
        }
    }

    /// <summary>Mouse report wire format, from the live format mode bits
    /// (1005/1006/1015/1016).</summary>
    public MouseFormat Format
    {
        get
        {
            lock (_vtLock)
                return FormatLocked;
        }
    }

    public bool MouseTracking
    {
        get
        {
            lock (_vtLock)
                return TrackingModeLocked != MouseTrackingMode.None;
        }
    }

    public bool ApplicationCursor
    {
        get
        {
            lock (_vtLock)
                return GetMode(1);
        }
    }

    public byte KittyKeyboardFlags
    {
        get
        {
            lock (_vtLock)
                return TryGet(TerminalData.KittyKeyboardFlags, out byte flags) ? flags : (byte)0;
        }
    }

    /// <summary>Encodes one physical key event using libghostty's current
    /// legacy, xterm, application-cursor/keypad, and Kitty keyboard modes.</summary>
    public unsafe int EncodeKey(TerminalKey key, TerminalKeyAction action, TerminalKeyModifiers modifiers,
        ReadOnlySpan<byte> utf8, uint unshiftedCodepoint, Span<byte> output)
    {
        lock (_vtLock)
        {
            Native.ghostty_key_encoder_setopt_from_terminal(_keyEncoder, _terminal);
            // ConPTY cannot carry Kitty CSI-u or modifyOtherKeys input: its
            // input parser only round-trips keystrokes it recognizes as
            // Windows console key events and swallows the rest, so an app
            // that negotiated Kitty (e.g. any pi-based TUI) would receive
            // nothing for Escape or Ctrl+C. The encoder is therefore locked
            // to legacy bytes — Kitty-aware apps still accept those on their
            // raw-byte fallback paths. The emulator's own protocol state is
            // untouched: queries are answered honestly, push/pop works, and
            // KittyKeyboardFlags still reports what the app requested.
            byte kittyFlags = 0;
            Native.ghostty_key_encoder_setopt(_keyEncoder, Native.KeyEncoderOption.KittyFlags, (IntPtr)(&kittyFlags));
            byte modifyOtherKeys = 0;
            Native.ghostty_key_encoder_setopt(_keyEncoder, Native.KeyEncoderOption.ModifyOtherKeysState2, (IntPtr)(&modifyOtherKeys));
            Native.ghostty_key_event_set_key(_keyEvent, key);
            Native.ghostty_key_event_set_action(_keyEvent, (Native.KeyAction)action);
            Native.ghostty_key_event_set_mods(_keyEvent, modifiers);
            Native.ghostty_key_event_set_consumed_mods(_keyEvent, TerminalKeyModifiers.None);
            Native.ghostty_key_event_set_composing(_keyEvent, false);
            Native.ghostty_key_event_set_unshifted_codepoint(_keyEvent, unshiftedCodepoint);

            fixed (byte* textPtr = utf8)
            fixed (byte* outputPtr = output)
            {
                Native.ghostty_key_event_set_utf8(_keyEvent, (IntPtr)textPtr, (nuint)utf8.Length);
                var result = Native.ghostty_key_encoder_encode(
                    _keyEncoder, _keyEvent, (IntPtr)outputPtr, (nuint)output.Length, out var written);
                Native.ghostty_key_event_set_utf8(_keyEvent, IntPtr.Zero, 0);
                Check(result, "key_encoder_encode");
                return checked((int)written);
            }
        }
    }

    public bool BracketedPaste
    {
        get
        {
            lock (_vtLock)
                return GetMode(2004);
        }
    }

    private unsafe bool GetMode(ushort mode)
    {
        var config = new GhosttyTerminalModeConfig { mode = mode, value = false };
        return Native.ghostty_terminal_get(_terminal, TerminalData.Mode, (IntPtr)(&config)) == Result.Success && config.value;
    }

    private MouseTrackingMode TrackingModeLocked =>
        GetMode(1003) ? MouseTrackingMode.Any
        : GetMode(1002) ? MouseTrackingMode.Button
        : GetMode(1000) ? MouseTrackingMode.Normal
        : GetMode(9) ? MouseTrackingMode.X10
        : MouseTrackingMode.None;

    private MouseFormat FormatLocked =>
        GetMode(1016) ? MouseFormat.SgrPixels
        : GetMode(1015) ? MouseFormat.Urxvt
        : GetMode(1005) ? MouseFormat.Utf8
        : GetMode(1006) ? MouseFormat.Sgr
        : MouseFormat.X10;

    // ---- Scroll -------------------------------------------------------------

    public (ulong Total, ulong Offset, ulong Len) Scrollbar
    {
        get
        {
            lock (_vtLock)
                return TryGet(TerminalData.Scrollbar, out GhosttyTerminalScrollbar sb) ? (sb.total, sb.offset, sb.len) : (0, 0, 0);
        }
    }

    public void ScrollToTop() => Scroll(Native.ScrollViewportTag.Top, 0);

    public void ScrollToBottom() => Scroll(Native.ScrollViewportTag.Bottom, 0);

    public void ScrollToRow(ulong row) => Scroll(Native.ScrollViewportTag.Row, (nint)row);

    public void ScrollBy(int delta) => Scroll(Native.ScrollViewportTag.Delta, delta);

    private void Scroll(Native.ScrollViewportTag tag, nint value)
    {
        lock (_vtLock)
        {
            var behavior = new Native.GhosttyTerminalScrollViewport { tag = (int)tag, value = default };
            if (tag == Native.ScrollViewportTag.Delta)
                behavior.value.delta = value;
            else if (tag == Native.ScrollViewportTag.Row)
                behavior.value.row = (nuint)value;
            Native.ghostty_terminal_scroll_viewport(_terminal, behavior);
        }
    }

    // ---- Mouse input -------------------------------------------------------

    /// <summary>Encodes a pointer event using the exact tracking mode and
    /// wire format requested by the terminal application. The encoder is
    /// driven from the live mode bits — the same state the host's routing
    /// decisions read — so routing and encoding can never disagree; the
    /// cached last-transition flag that setopt_from_terminal relied on
    /// collapses to "none" when an app resets one tracking mode while
    /// another is still set, silently emitting zero bytes for every event.
    /// setopt is dirty-checked since modes change rarely. A successful event
    /// can still produce zero bytes when its current mode filters that
    /// event.</summary>
    public unsafe int EncodeMouse(MouseInputAction action, MouseInputButton? button,
        MouseInputModifiers modifiers, double x, double y, bool anyButtonPressed,
        byte[] output)
    {
        lock (_vtLock)
        {
            var tracking = TrackingModeLocked;
            if (_lastEncodedTracking != tracking)
            {
                _lastEncodedTracking = tracking;
                Native.ghostty_mouse_encoder_setopt(_mouseEncoder, MouseEncoderOption.Event, (IntPtr)(&tracking));
            }
            var format = FormatLocked;
            if (_lastEncodedFormat != format)
            {
                _lastEncodedFormat = format;
                Native.ghostty_mouse_encoder_setopt(_mouseEncoder, MouseEncoderOption.Format, (IntPtr)(&format));
            }

            var pressedValue = anyButtonPressed ? 1 : 0;
            if (_mouseAnyButtonPressed != pressedValue)
            {
                _mouseAnyButtonPressed = pressedValue;
                byte pressed = anyButtonPressed ? (byte)1 : (byte)0;
                Native.ghostty_mouse_encoder_setopt(_mouseEncoder, MouseEncoderOption.AnyButtonPressed, (IntPtr)(&pressed));
            }

            Native.ghostty_mouse_event_set_action(_mouseEvent, action);
            if (button is { } value)
                Native.ghostty_mouse_event_set_button(_mouseEvent, value);
            else
                Native.ghostty_mouse_event_clear_button(_mouseEvent);
            Native.ghostty_mouse_event_set_mods(_mouseEvent, modifiers);
            Native.ghostty_mouse_event_set_position(_mouseEvent, new Native.GhosttyMousePosition
            {
                x = (float)x,
                y = (float)y,
            });

            var result = Native.ghostty_mouse_encoder_encode(
                _mouseEncoder, _mouseEvent, output, (nuint)output.Length, out var written);
            Check(result, "mouse_encoder_encode");
            return checked((int)written);
        }
    }

    private unsafe void ConfigureMouseEncoderSize()
    {
        var size = new Native.GhosttyMouseEncoderSize
        {
            size = (nuint)Marshal.SizeOf<Native.GhosttyMouseEncoderSize>(),
            screenWidth = (uint)Math.Max(1, _cols * _cellWidthPx),
            screenHeight = (uint)Math.Max(1, _rows * _cellHeightPx),
            cellWidth = (uint)Math.Max(1, _cellWidthPx),
            cellHeight = (uint)Math.Max(1, _cellHeightPx),
        };
        Native.ghostty_mouse_encoder_setopt(_mouseEncoder, MouseEncoderOption.Size, (IntPtr)(&size));
    }

    // ---- Frame --------------------------------------------------------------

    /// <summary>
    /// Pulls the latest terminal state into the render state and copies the
    /// viewport into managed <see cref="FrameRows"/>. Row/cell arrays are
    /// reused between calls; data is only valid until the next call.
    /// Rows the emulator did not mark dirty keep their previous cell data:
    /// per the render-state contract, this method consumes the dirty flags it
    /// reads (global and per-row), so the next update reports exactly the
    /// rows that changed since. Only a full rebuild (viewport move, screen
    /// switch, resize, terminal-wide change) re-reads every row.
    /// </summary>
    public unsafe void UpdateFrame()
    {
        // The whole snapshot is one critical section: the reader thread may
        // feed concurrently, and render_state_update reads the terminal.
        lock (_vtLock)
        {
            UpdateFrameLocked();
        }
    }

    private unsafe void UpdateFrameLocked()
    {
        Check(Native.ghostty_render_state_update(_renderState, _terminal), "render_state_update");

        FrameDirty dirty = FrameDirty.Clean;
        ushort cols = 0, rows = 0;
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.Dirty, (IntPtr)(&dirty)), "get dirty");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.Cols, (IntPtr)(&cols)), "get cols");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.Rows, (IntPtr)(&rows)), "get rows");
        FrameDirty = dirty;
        var readAll = dirty == FrameDirty.Full || cols != _cols || rows != _rows;
        _cols = cols;
        _rows = rows;

        byte cursorVisible = 0, cursorBlink = 0, cursorInViewport = 0;
        var cursorStyle = CursorShape.Block;
        ushort cursorX = 0, cursorY = 0;
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorVisible, (IntPtr)(&cursorVisible)), "cursor visible");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorBlinking, (IntPtr)(&cursorBlink)), "cursor blink");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorVisualStyle, (IntPtr)(&cursorStyle)), "cursor style");
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorViewportHasValue, (IntPtr)(&cursorInViewport)), "cursor vp");
        if (cursorInViewport != 0)
        {
            Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorViewportX, (IntPtr)(&cursorX)), "cursor x");
            Check(Native.ghostty_render_state_get(_renderState, RenderStateData.CursorViewportY, (IntPtr)(&cursorY)), "cursor y");
        }
        Cursor = new CursorState(cursorX, cursorY, cursorVisible != 0, cursorBlink != 0, cursorStyle);

        EnsureFrameRows(rows);
        var rowIterator = _rowIterator;
        Check(Native.ghostty_render_state_get(_renderState, RenderStateData.RowIterator, (IntPtr)(&rowIterator)), "row iterator");

        byte rowDirtyFalse = 0;
        var row = 0;
        while (Native.ghostty_render_state_row_iterator_next(_rowIterator))
        {
            var frameRow = FrameRows[row];
            byte rowDirty = 0;
            Check(Native.ghostty_render_state_row_get(_rowIterator, RenderStateRowData.Dirty, (IntPtr)(&rowDirty)), "row dirty");
            frameRow.Dirty = rowDirty != 0;

            var selection = new Native.GhosttyRenderStateRowSelection { size = (nuint)Marshal.SizeOf<Native.GhosttyRenderStateRowSelection>() };
            var selResult = Native.ghostty_render_state_row_get(_rowIterator, RenderStateRowData.Selection, (IntPtr)(&selection));
            frameRow.HasSelection = selResult == Result.Success;
            if (frameRow.HasSelection)
            {
                frameRow.SelectionStart = selection.startX;
                frameRow.SelectionEnd = selection.endX;
            }

            // A clean row was unchanged since the previous update, so its
            // managed cells are still valid; the per-row dirty flag is only
            // consumed for rows whose cells are actually re-read.
            if (readAll || frameRow.Dirty)
            {
                var rowCellsHandle = _rowCells;
                Check(Native.ghostty_render_state_row_get(_rowIterator, RenderStateRowData.Cells, (IntPtr)(&rowCellsHandle)), "row cells");
                var cells = frameRow.Cells;
                var col = 0;
                while (col < cols && Native.ghostty_render_state_row_cells_next(_rowCells))
                {
                    ref var cell = ref cells[col];
                    ReadCell(ref cell);
                    col++;
                }
                for (; col < cols; col++)
                    cells[col] = default;

                Check(Native.ghostty_render_state_row_set(_rowIterator, RenderStateRowOption.Dirty, (IntPtr)(&rowDirtyFalse)), "row dirty clear");
            }

            row++;
            if (row >= rows)
                break;
        }

        // Clear the global dirty state too: it accumulates across updates and
        // would otherwise force a full read (and full repaint) on every frame.
        if (dirty != FrameDirty.Clean)
        {
            var clean = FrameDirty.Clean;
            Check(Native.ghostty_render_state_set(_renderState, RenderStateOption.Dirty, (IntPtr)(&clean)), "dirty clear");
        }
    }

    private unsafe void ReadCell(ref CellInfo cell)
    {
        var style = Native.GhosttyStyle.New();
        Check(Native.ghostty_render_state_row_cells_get(_rowCells, RenderStateRowCellsData.Style, (IntPtr)(&style)), "cell style");

        cell.Flags = CellFlags.None;
        if (style.bold != 0) cell.Flags |= CellFlags.Bold;
        if (style.italic != 0) cell.Flags |= CellFlags.Italic;
        if (style.underline != 0) cell.Flags |= CellFlags.Underline;
        if (style.strikethrough != 0) cell.Flags |= CellFlags.Strikethrough;
        if (style.inverse != 0) cell.Flags |= CellFlags.Inverse;
        if (style.blink != 0) cell.Flags |= CellFlags.Blink;
        if (style.invisible != 0) cell.Flags |= CellFlags.Invisible;
        if (style.faint != 0) cell.Flags |= CellFlags.Faint;

        cell.FgTag = (ColorTag)style.fgColor.tag;
        cell.FgValue = style.fgColor.tag == (int)ColorTag.Rgb
            ? (style.fgColor.value.rgb.r << 16) | (style.fgColor.value.rgb.g << 8) | style.fgColor.value.rgb.b
            : (int)style.fgColor.value.palette;
        cell.BgTag = (ColorTag)style.bgColor.tag;
        cell.BgValue = style.bgColor.tag == (int)ColorTag.Rgb
            ? (style.bgColor.value.rgb.r << 16) | (style.bgColor.value.rgb.g << 8) | style.bgColor.value.rgb.b
            : (int)style.bgColor.value.palette;

        ulong raw = 0;
        Check(Native.ghostty_render_state_row_cells_get(_rowCells, RenderStateRowCellsData.Raw, (IntPtr)(&raw)), "cell raw");
        var wide = Native.CellWide.Narrow;
        Check(Native.ghostty_cell_get(raw, CellData.Wide, (IntPtr)(&wide)), "cell wide");
        cell.Wide = wide == Native.CellWide.Wide;
        cell.Tail = wide == Native.CellWide.WideSpacerTail;

        // A cell's background can live in the cell CONTENT rather than the
        // style: erases with a color set ("clear" in TUI apps) write cells
        // whose content is a palette index or RGB and whose style is the
        // default. Paint those like any other background.
        // Packed cell layout for the pinned ghostty@f64f4aca ABI:
        // content_tag = bits 0-1, content = bits 2-25. Decoding this in
        // managed code avoids another P/Invoke for every blank grid cell.
        var contentTag = (int)(raw & 0b11);
        if (cell.BgTag == ColorTag.None && contentTag == 2)
        {
            // color_palette: packed struct(u24) { data: u8 at bits 2-9 }
            cell.BgTag = ColorTag.Palette;
            cell.BgValue = (int)((raw >> 2) & 0xFF);
        }
        else if (cell.BgTag == ColorTag.None && contentTag == 3)
        {
            // color_rgb: packed RGB at bits 2-25, LSB first: r 2-9, g 10-17,
            // b 18-25. Repack into (r<<16)|(g<<8)|b like style RGB values.
            var content = (int)((raw >> 2) & 0xFFFFFF);
            var cr = content & 0xFF;
            var cg = (content >> 8) & 0xFF;
            var cb = (content >> 16) & 0xFF;
            cell.BgTag = ColorTag.Rgb;
            cell.BgValue = (cr << 16) | (cg << 8) | cb;
        }

        uint graphemesLen = 0;
        Check(Native.ghostty_render_state_row_cells_get(_rowCells, RenderStateRowCellsData.GraphemesLen, (IntPtr)(&graphemesLen)), "cell graphemes len");
        if (graphemesLen == 0 || cell.Tail)
        {
            cell.Text = "";
            return;
        }

        if (_codepoints.Length < graphemesLen)
            _codepoints = new uint[Math.Max(graphemesLen, MaxGraphemes)];
        var codepoints = _codepoints;
        fixed (uint* p = codepoints)
            Check(Native.ghostty_render_state_row_cells_get(_rowCells, RenderStateRowCellsData.GraphemesBuf, (IntPtr)p), "cell graphemes buf");

        if (graphemesLen == 1)
        {
            var cp = _codepoints[0];
            cell.Text = cp < (uint)s_asciiStrings.Length
                ? s_asciiStrings[(int)cp]
                : char.ConvertFromUtf32((int)cp);
        }
        else
        {
            var sb = new StringBuilder((int)graphemesLen * 2);
            for (var i = 0; i < graphemesLen; i++)
                sb.Append(char.ConvertFromUtf32((int)_codepoints[i]));
            cell.Text = sb.ToString();
        }
    }

    private void EnsureFrameRows(int rows)
    {
        if (FrameRows.Length != rows)
        {
            var previous = FrameRows;
            FrameRows = new FrameRow[rows];
            for (var i = 0; i < rows; i++)
                FrameRows[i] = i < previous.Length ? previous[i] : new FrameRow();
        }
        // A width-only resize keeps the row count unchanged, so the early
        // return above cannot be the only guard: the per-row cell arrays must
        // still grow to the new width. Size every row unconditionally.
        for (var i = 0; i < rows; i++)
        {
            if (FrameRows[i].Cells.Length != _cols)
                FrameRows[i].Cells = new CellInfo[_cols];
        }
    }

    // ---- Selection -----------------------------------------------------------

    public bool HasSelection
    {
        get
        {
            lock (_vtLock)
                return TryGetBuffer(TerminalData.Selection, s_selectionScratch);
        }
    }

    public unsafe int SelectionPress(int viewportCol, int viewportRow, double xPx, double yPx)
    {
        lock (_vtLock)
        {
            var point = new Native.GhosttyPoint
            {
                tag = (int)Native.PointTag.Viewport,
                value = { coordinate = new Native.GhosttyPointCoordinate { x = (ushort)viewportCol, y = (uint)viewportRow } },
            };
            // A point that no longer maps to the grid (resize, alt-screen switch)
            // must skip the press, not crash the app.
            if (Native.ghostty_terminal_grid_ref(_terminal, point, out var gridRef) != Result.Success)
                return 0;

            SetEventOption(_pressEvent, SelectionGestureEventOption.Ref, gridRef);
            SetEventOption(_pressEvent, SelectionGestureEventOption.TimeNs, NowNs());
            var position = new Native.GhosttySurfacePosition { x = xPx, y = yPx };
            SetEventOption(_pressEvent, SelectionGestureEventOption.Position, position);

            var snapshot = GhosttySelectionScratch();
            var result = Native.ghostty_selection_gesture_event(_gesture, _terminal, _pressEvent, (IntPtr)(&snapshot));
            if (result == Result.Success)
                Check(Native.ghostty_terminal_set(_terminal, TerminalOption.Selection, (IntPtr)(&snapshot)), "set selection");

            byte clickCount = 0;
            Check(Native.ghostty_selection_gesture_get(_gesture, _terminal,
                SelectionGestureData.ClickCount, (IntPtr)(&clickCount)), "get selection click count");
            return clickCount;
        }
    }
    public unsafe void SelectionDrag(int viewportCol, int viewportRow, double xPx, double yPx)
    {
        lock (_vtLock)
        {
            var point = new Native.GhosttyPoint
            {
                tag = (int)Native.PointTag.Viewport,
                value = { coordinate = new Native.GhosttyPointCoordinate { x = (ushort)viewportCol, y = (uint)viewportRow } },
            };
            if (Native.ghostty_terminal_grid_ref(_terminal, point, out var gridRef) != Result.Success)
                return;

            SetEventOption(_dragEvent, SelectionGestureEventOption.Ref, gridRef);
            var geometry = new Native.GhosttySelectionGestureGeometry
            {
                columns = (uint)_cols,
                cellWidth = (uint)Math.Max(1, _cellWidthPx),
                paddingLeft = 0,
                screenHeight = (uint)Math.Max(1, _rows * _cellHeightPx),
            };
            SetEventOption(_dragEvent, SelectionGestureEventOption.Geometry, geometry);
            var position = new Native.GhosttySurfacePosition { x = xPx, y = yPx };
            SetEventOption(_dragEvent, SelectionGestureEventOption.Position, position);

            var snapshot = GhosttySelectionScratch();
            var result = Native.ghostty_selection_gesture_event(_gesture, _terminal, _dragEvent, (IntPtr)(&snapshot));
            if (result == Result.Success)
                Check(Native.ghostty_terminal_set(_terminal, TerminalOption.Selection, (IntPtr)(&snapshot)), "set selection");
        }
    }

    public unsafe void SelectionRelease(int viewportCol, int viewportRow)
    {
        lock (_vtLock)
        {
            var point = new Native.GhosttyPoint
            {
                tag = (int)Native.PointTag.Viewport,
                value = { coordinate = new Native.GhosttyPointCoordinate { x = (ushort)viewportCol, y = (uint)viewportRow } },
            };
            if (Native.ghostty_terminal_grid_ref(_terminal, point, out var gridRef) == Result.Success)
                SetEventOption(_releaseEvent, SelectionGestureEventOption.Ref, gridRef);
            else
                SetEventOption(_releaseEvent, SelectionGestureEventOption.Ref, IntPtr.Zero);

            var snapshot = GhosttySelectionScratch();
            Native.ghostty_selection_gesture_event(_gesture, _terminal, _releaseEvent, (IntPtr)(&snapshot));
        }
    }

    public unsafe void ClearSelection(bool resetGesture = true)
    {
        lock (_vtLock)
        {
            Native.ghostty_terminal_set(_terminal, TerminalOption.Selection, IntPtr.Zero);
            if (resetGesture)
                Native.ghostty_selection_gesture_reset(_gesture, _terminal);
        }
    }

    public unsafe string? GetSelectedText()
    {
        lock (_vtLock)
        {
            var options = new Native.GhosttyTerminalSelectionFormatOptions
            {
                size = (nuint)Marshal.SizeOf<Native.GhosttyTerminalSelectionFormatOptions>(),
                emit = (int)Native.FormatterFormat.Plain,
                unwrap = true,
                trim = true,
                selection = IntPtr.Zero,
            };
            var result = Native.ghostty_terminal_selection_format_alloc(_terminal, IntPtr.Zero, options, out var ptr, out var len);
            if (result != Result.Success || ptr == IntPtr.Zero)
                return null;
            try
            {
                return Encoding.UTF8.GetString((byte*)ptr, (int)len);
            }
            finally
            {
                Native.ghostty_free(IntPtr.Zero, ptr, len);
            }
        }
    }

    private static GhosttySelection GhosttySelectionScratch() => new()
    {
        size = (nuint)Marshal.SizeOf<GhosttySelection>(),
        start = new Native.GhosttyGridRef { size = (nuint)Marshal.SizeOf<Native.GhosttyGridRef>() },
        end = new Native.GhosttyGridRef { size = (nuint)Marshal.SizeOf<Native.GhosttyGridRef>() },
    };

    // ---- Plumbing ------------------------------------------------------------

    private static readonly byte[] s_selectionScratch = new byte[64];

    private unsafe bool TryGetBuffer(TerminalData data, byte[] buffer)
    {
        var scratch = buffer;
        fixed (byte* p = scratch)
            return Native.ghostty_terminal_get(_terminal, data, (IntPtr)p) == Result.Success;
    }

    private unsafe bool TryGet<T>(TerminalData data, out T value) where T : unmanaged
    {
        value = default;
        var buffer = value;
        var ok = Native.ghostty_terminal_get(_terminal, data, (IntPtr)(&buffer)) == Result.Success;
        value = buffer;
        return ok;
    }

    private void SetOption(TerminalOption option, IntPtr value) =>
        Check(Native.ghostty_terminal_set(_terminal, option, value), $"set {(int)option}");

    private void SetOption<T>(TerminalOption option, T value) where T : unmanaged
    {
        unsafe
        {
            Check(Native.ghostty_terminal_set(_terminal, option, (IntPtr)(&value)), $"set {(int)option}");
        }
    }

    private void SetEventOption<T>(IntPtr eventHandle, SelectionGestureEventOption option, T value) where T : unmanaged
    {
        unsafe
        {
            Check(Native.ghostty_selection_gesture_event_set(eventHandle, option, (IntPtr)(&value)), $"event set {(int)option}");
        }
    }

    private void SetEventOption(IntPtr eventHandle, SelectionGestureEventOption option, IntPtr value) =>
        Native.ghostty_selection_gesture_event_set(eventHandle, option, value);

    private static ulong NowNs()
    {
        // Stopwatch.Frequency is constant for the process; precompute the
        // scale so selection events don't divide on every call.
        var ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        return (ulong)(ticks * s_ticksToNs);
    }

    private static readonly double s_ticksToNs = 1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Single-codepoint strings for the ASCII range: most terminal
    /// cells are ASCII, and ConvertFromUtf32 would allocate a fresh string
    /// per cell per frame otherwise.</summary>
    private static readonly string[] s_asciiStrings = BuildAsciiStrings();

    private static string[] BuildAsciiStrings()
    {
        var strings = new string[128];
        for (var i = 0; i < strings.Length; i++)
            strings[i] = ((char)i).ToString();
        return strings;
    }

    private static void Check(Result result, string what)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"libghostty: {what} failed: {result}");
    }

    private static void OnTitleChanged(IntPtr terminal, IntPtr userdata)
    {
        if (GetTarget(userdata) is not { } self)
            return;
        var title = GetBorrowed(terminal, TerminalData.Title);
        self.TitleChanged?.Invoke(title);
    }

    private static void OnBell(IntPtr terminal, IntPtr userdata)
    {
        if (GetTarget(userdata) is not { } self)
            return;
        self.Bell?.Invoke();
    }

    private static void OnWritePty(IntPtr terminal, IntPtr userdata, IntPtr data, nuint len)
    {
        if (GetTarget(userdata) is not { } self)
            return;
        var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent((int)len);
        try
        {
            Marshal.Copy(data, bytes, 0, (int)len);
            self.WritePty?.Invoke(bytes, (int)len);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static GhosttyTerminal? GetTarget(IntPtr userdata) =>
        GCHandle.FromIntPtr(userdata).Target as GhosttyTerminal;

    private static unsafe string GetBorrowed(IntPtr terminal, TerminalData data)
    {
        var s = new Native.GhosttyString();
        if (Native.ghostty_terminal_get(terminal, data, (IntPtr)(&s)) != Result.Success)
            return "";
        return s.ptr == IntPtr.Zero || s.len == 0 ? "" : Marshal.PtrToStringUTF8(s.ptr, (int)s.len) ?? "";
    }

    public void Dispose()
    {
        lock (_vtLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Native.ghostty_key_event_free(_keyEvent);
            Native.ghostty_key_encoder_free(_keyEncoder);
            Native.ghostty_mouse_event_free(_mouseEvent);
            Native.ghostty_mouse_encoder_free(_mouseEncoder);
            Native.ghostty_selection_gesture_event_free(_releaseEvent);
            Native.ghostty_selection_gesture_event_free(_dragEvent);
            Native.ghostty_selection_gesture_event_free(_pressEvent);
            Native.ghostty_selection_gesture_free(_gesture, _terminal);
            Native.ghostty_render_state_row_cells_free(_rowCells);
            Native.ghostty_render_state_row_iterator_free(_rowIterator);
            Native.ghostty_render_state_free(_renderState);
            Native.ghostty_terminal_free(_terminal);
            _terminal = IntPtr.Zero;
            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyTerminalModeConfig
{
    public ushort mode;
    [MarshalAs(UnmanagedType.I1)] public bool value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttySelection
{
    public nuint size;
    public Native.GhosttyGridRef start;
    public Native.GhosttyGridRef end;
    [MarshalAs(UnmanagedType.I1)] public bool rectangle;
}
