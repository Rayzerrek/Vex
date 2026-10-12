using System.Runtime.InteropServices;
using System.Text;
using static Vex.Libghostty.Native;

namespace Vex.Libghostty;

/// <summary>
/// Managed wrapper over one libghostty-vt terminal plus its render state.
/// All native calls are serialized through <see cref="SyncRoot"/>: VT input
/// is fed on the PTY reader thread so query responses (DSR, OSC color
/// reports, DA) go back to the child immediately, while the WPF UI thread
/// only takes the lock to snapshot render state.
/// </summary>
public sealed partial class GhosttyTerminal : IDisposable
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
    private string? _workingDirectory;
    private readonly string? _wslDistribution;

    // Serializes every native terminal call. Feed runs on the PTY reader
    // thread while snapshots (UpdateFrame, modes, mouse, selection) run on
    // the UI thread. Callbacks (WritePty, TitleChanged) fire on the feeding
    // thread while the lock is held; C# monitors are re-entrant, so they may
    // safely re-enter locked accessors on the same thread. Lock ordering is
    // always terminal-lock before session-write-lock, never the reverse.
    private readonly object _vtLock = new();
    public object SyncRoot => _vtLock;

    // Capture OSC 133 semantics here rather than passing them to ghostty-vt.
    // Its "fresh line" handling of OSC 133;A
    // moves the cursor when it is not at column 0. A shell (nushell) sends
    // these markers on every prompt redraw, so after a full-screen TUI exits
    // the mid-line marker forces a line feed and the prompt is drawn twice.
    // Reading status before stripping the marker preserves the shell's cursor.
    //
    // The same filter also drops bare DCS query payloads. ConPTY eats the DCS
    // wrapper (introducer and terminator) of client queries such as nvim's
    // XTGETTCAP (`ESC P + q 4D73 ST`) and DECRQSS (`ESC P $ q m ST`), so only
    // the middles (`+q4D73`, `$qm`) reach the emulator — which would print
    // them as visible garbage. The payloads are matched against known probes
    // (whitelist, never a bare prefix) so ordinary text is never eaten.
    private enum FeedState { Normal, EscapeSeen, InOsc, InOscEscapeSeen, DropOsc, DropOscEscapeSeen, InCsi, InDcs, InDcsEsc, PlusSeen, InPlusQuery, DollarSeen, DollarQSeen }
    private FeedState _feedState;
    private byte[] _oscBuf = new byte[4096];
    private const int MaxClipboardOscBytes = 1024 * 1024;
    private int _oscLen;
    private readonly ProgramStatusTracker _programStatus = new();
    private static readonly byte[] ProgramStatusReply = "\x1b]7501;?\x1b\\"u8.ToArray();
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

    /// <summary>Last local working directory reported by OSC 7; null when the shell does not report it.</summary>
    public string? WorkingDirectory => Volatile.Read(ref _workingDirectory);
    public event Action<byte[], int>? WritePty;

    /// <summary>Raised when the application rings the terminal bell (BEL).
    /// Fires on the feed thread, like <see cref="TitleChanged"/>.</summary>
    public event Action? Bell;

    /// <summary>Decoded OSC 52 clipboard write on the feed thread; clipboard reads are never exposed.</summary>
    public event Action<string>? ClipboardWriteRequested;

    /// <summary>Program status changes on the feed thread; the boolean latches attention across UI batching.</summary>
    public event Action<ProgramStatusSummary, bool>? ProgramStatusChanged;

    /// <summary>Current program status, including shell integration fallback, independent of screen mode.</summary>
    public ProgramStatusSummary ProgramStatus { get { lock (_vtLock) return _programStatus.Summary; } }

    /// <summary>Acknowledges completed results when the user sends input to this terminal.</summary>
    public void AcknowledgeProgramStatus() { lock (_vtLock) _programStatus.AcknowledgeProgramStatus(); }

    /// <summary>Drops running status records when the process attached to this terminal exits.</summary>
    public void NotifyProgramExited() { lock (_vtLock) _programStatus.OnProgramExited(); }

    public FrameDirty FrameDirty { get; private set; }
    public CursorState Cursor { get; private set; }
    public FrameRow[] FrameRows { get; private set; } = Array.Empty<FrameRow>();

    private int _cols;
    private int _rows;
    private bool _needsReadAll = true;
    private TerminalScreen _lastScreen = TerminalScreen.Primary;
    private readonly StringBuilder _graphemeBuilder = new(8);

    private int _cellWidthPx = 8;
    private int _cellHeightPx = 16;

    /// <summary>Creates a terminal; WSL sessions map Linux OSC 7 directories into their distribution's network filesystem.</summary>
    public GhosttyTerminal(int cols, int rows, string? wslDistribution = null)
    {
        if (wslDistribution is not null && (wslDistribution.Length == 0 || wslDistribution.Any(char.IsControl) || wslDistribution.IndexOfAny(['/', '\\', ':']) >= 0))
            throw new ArgumentException("Invalid WSL distribution name.", nameof(wslDistribution));
        _wslDistribution = wslDistribution;
        _programStatus.Changed += attention => ProgramStatusChanged?.Invoke(_programStatus.Summary, attention);
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

        // ConPTY owns a separate screen buffer without scrollback. Pulling
        // history into this emulator during a resize would make the two grids
        // disagree, so later cursor-relative redraws would land on old rows.
        SetOption(TerminalOption.ResizePullScrollback, false);

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

    // ---- Geometry / colors ------------------------------------------------

    public void Resize(int cols, int rows, int cellWidthPx, int cellHeightPx)
    {
        lock (_vtLock)
        {
            _needsReadAll = true;
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

    /// <summary>
    /// Whether the application is inside a synchronized-output block
    /// (DEC private mode 2026). Applications that redraw a whole frame — nvim,
    /// opencode, the agent CLIs — wrap it in `CSI ? 2026 h` / `CSI ? 2026 l`
    /// to publish it atomically, and until the block closes the emulator
    /// already exposes the rows written so far. A host that paints between the
    /// markers shows a torn screen: new text in the rows the app has reached,
    /// the previous frame everywhere else. Hosts should wait for the close.
    /// </summary>
    public bool SynchronizedOutput
    {
        get
        {
            lock (_vtLock)
                return GetMode(2026);
        }
    }

    /// <summary>Whether the application enabled DEC private mode 2048
    /// (in-band resize). Under ConPTY the child never learns about grid
    /// changes from the OS — no SIGWINCH and no console resize event — so a
    /// TUI that negotiated this mode depends on the host terminal sending
    /// CSI 48;rows;cols;yPx;xPx t reports on every resize. Vex advertises
    /// mode 2048 through libghostty-vt's DECRQM answers, which creates the
    /// obligation to emit those reports.</summary>
    public bool InBandResize
    {
        get
        {
            lock (_vtLock)
                return GetMode(2048);
        }
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

    /// <summary>Forces the next <see cref="UpdateFrame"/> to re-read every
    /// cell. Used after host-side events (resize settle, alt-screen) that
    /// can leave managed row caches behind the emulator.</summary>
    public void InvalidateCellCache()
    {
        lock (_vtLock)
            _needsReadAll = true;
    }

    private void Scroll(Native.ScrollViewportTag tag, nint value)
    {
        lock (_vtLock)
        {
            // Viewport moves rematerialize every visible row; dirty flags
            // alone under-report the shift, so the next snapshot must
            // rebuild the managed cell cache from scratch.
            _needsReadAll = true;
            var behavior = new Native.GhosttyTerminalScrollViewport { tag = (int)tag, value = default };
            if (tag == Native.ScrollViewportTag.Delta)
                behavior.value.delta = value;
            else if (tag == Native.ScrollViewportTag.Row)
                behavior.value.row = (nuint)value;
            Native.ghostty_terminal_scroll_viewport(_terminal, behavior);
        }
    }

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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void Check(Result result, string what)
    {
        if (result != Result.Success)
            ThrowFailed(result, what);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowFailed(Result result, string what)
    {
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
