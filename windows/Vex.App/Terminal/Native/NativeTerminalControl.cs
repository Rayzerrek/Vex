using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vex.App.Model;
using Vex.Libghostty;
using Vex.Terminal;

namespace Vex.App.Terminal.Native;

/// <summary>
/// The native terminal surface: libghostty-vt emulates the VT stream and
/// this control renders the grid directly in WPF (one DrawingVisual per row,
/// redrawn only when the emulator marks it dirty). No browser bridge, no IPC —
/// the app's fast path in the spirit of upstream's Alacritty backend.
/// </summary>
public sealed partial class NativeTerminalControl : FrameworkElement, ITerminalView
{
    private readonly string _workingDirectory;
    private readonly GhosttyTerminal _terminal;
    private readonly VisualCollection _children;
    private readonly DrawingVisual _selectionVisual = new();
    private readonly DrawingVisual _caretVisual = new();
    private readonly DrawingVisual _scrollbarVisual = new();
    private readonly List<DrawingVisual> _rowVisuals = new();
    private readonly DispatcherTimer _blinkTimer;
    private readonly StringBuilder _runBuilder = new();
    private int[] _runWidths = Array.Empty<int>();

    // Per-row run caches: one glyph-index array per row, sized to the row
    // width, reused across redraws.  A row can have at most (cols+1)/2 runs
    // (alternating background runs), so the text-run pool is sized to that.
    private TerminalPalette _palette = null!;
    private FontFamily _fontFamily = null!;
    private double _fontSize = 13;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _pixelsPerDip = 1.0;
    private int _nativeCellWidth = 8;
    private int _nativeCellHeight = 16;
    private Typeface _normalTypeface = null!;
    private Typeface _boldTypeface = null!;
    private Typeface _italicTypeface = null!;
    private Typeface _boldItalicTypeface = null!;
    private int _cols;
    private int _rows;
    private volatile bool _needsFullRedraw = true;
    private volatile bool _disposed;
    private int _bellPending;

    // Redraw passes that threw. A throw aborts the pass mid-way, so every row
    // below the failure keeps its previous pixels until a later pass reaches
    // them — reported by the self-test so a silently-half-painted screen is
    // never mistaken for a rendering quirk.
    private int _renderFailures;

    // Start of the current synchronized-output deferral (DEC 2026). A block
    // publishes one atomic frame, so painting inside it tears the screen; the
    // timestamp is the safety net for an application that dies mid-frame with
    // the mode still set.
    private long _syncDeferSince;
    private const int SyncDeferralLimitMs = 400;

    // Per-row render caches: the row's last-painted content hash plus the
    // render version it was painted with. RedrawRow skips the DrawingVisual
    // pass when both match, so redundant full redraws cost only the hash
    // pass. A version bump (font/DPI/palette change) invalidates every row
    // at once. The cache is index-aligned with _rowVisuals: rows are never
    // shifted, only re-rendered in place.
    private int _renderVersion = 1;
    private ulong[] _rowHashes = Array.Empty<ulong>();
    private int[] _rowVersions = Array.Empty<int>();

    // Last viewport offset seen after UpdateFrame; a change means the
    // viewport moved (wheel, PgUp/Dn, autoscroll) and every visible row
    // maps to different content.
    private ulong _lastScrollOffset;
    private bool _resizeScheduled;
    private GlyphTypeface? _normalGlyph;
    private GlyphTypeface? _boldGlyph;
    private GlyphTypeface? _italicGlyph;
    private GlyphTypeface? _boldItalicGlyph;
    private ushort[]? _normalAscii;
    private ushort[]? _boldAscii;
    private ushort[]? _italicAscii;
    private ushort[]? _boldItalicAscii;
    private double _baselineY;
    private readonly record struct FontMetricsKey(Typeface Typeface, double FontSize, double PixelsPerDip);
    private FontMetricsKey? _fontMetricsKey;

    private readonly object _outputLock = new();
    private bool _redrawScheduled;
    private string? _pendingTitle;
    private bool _titleUpdateQueued;
    private DispatcherTimer? _outputRedrawTimer;
    private long _lastOutputRedraw;

    private bool _caretBlinkVisible = true;
    private bool _cursorBlinkSetting = true;
    private bool _mouseTracking;
    private bool _wasAlternateScreen;

    private static readonly string? DiagPath = Environment.GetEnvironmentVariable("VEX_DIAG");
    internal static void Diag(string message)
    {
        if (DiagPath is null)
            return;
        try { System.IO.File.AppendAllText(DiagPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n"); }
        catch { }
    }

    // Cached caret draw state: the overlay is only reopened when the caret's
    // position, visibility, style or blink phase changed. The cell beneath the
    // cursor is repainted by the row pass, so a stationary caret needs no
    // overlay work on pumps that do not touch its row.
    private bool _caretCacheValid;
    private bool _caretCacheShouldDraw;
    private int _caretCacheRow;
    private int _caretCacheCol;
    private int _caretCacheStyle;

    private void InvalidateCaretCache() => _caretCacheValid = false;

    public event Action<string>? TitleChanged;
    public event Action<string>? TitleRawChanged;
    public event Action<int>? ProcessExited;
    public event Action<bool>? TuiModeChanged;
    public event Action? FocusGained;
    public event Action<TerminalCommand>? CommandRequested;

    /// <summary>Raised when the pane's application rings the bell — an agent
    /// or build finishing, a prompt needing input. Marshalled to the UI
    /// thread; drives the attention dot.</summary>
    public event Action? Bell;

    /// <summary>Last directory reported by the shell, falling back to this pane's launch directory.</summary>
    public string WorkingDirectory => _terminal.WorkingDirectory ?? _workingDirectory;

    public NativeTerminalControl(string workingDirectory, string? initialCommand = null, string? shellId = null)
    {
        _workingDirectory = workingDirectory;
        _initialCommand = initialCommand;
        _sessionShellId = ShellRegistry.ValidShellId(shellId) ?? WslShellPaths.ShellIdForDirectory(AppSettings.Instance.ShellId, workingDirectory);
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        MouseDown += OnTerminalMouseDown;
        MouseUp += OnTerminalMouseUp;
        // The terminal paints an opaque background, so subpixel antialiasing
        // is safe here. Grayscale makes small monospace strokes look faint.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        _terminal = new GhosttyTerminal(80, 24, ShellRegistry.IsWsl(_sessionShellId) ? WslShellPaths.ParseNetworkPath(workingDirectory)?.Distribution : null);
        _terminal.ProgramStatusChanged += QueueProgramStatus;
        _terminal.ClipboardWriteRequested += OnApplicationClipboardWrite;
        _terminal.TitleChanged += QueueTerminalTitle;
        _terminal.Bell += () =>
        {
            // Bell fires on the feed thread. Coalesce bursts (a shell loop or
            // a tab-completion beep) into one UI callback: the attention dot
            // is idempotent, so intermediate dispatches would only add churn.
            if (Interlocked.Exchange(ref _bellPending, 1) == 1)
                return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _bellPending, 0);
                if (!_disposed)
                    Bell?.Invoke();
            }, DispatcherPriority.Background);
        };
        // Runs on the PTY reader thread during Feed (query responses) and is
        // written straight into ConPTY: DSR/OSC answers no longer wait for a
        // UI-thread pump, which is what tripped Neovim's 100 ms
        // "Did not detect DSR response" timeout. Responses go through
        // WriteResponse, which writes the intact VT sequence to ConPTY.
        _terminal.WritePty += (data, len) =>
        {
            var session = _session;
            session?.WriteResponse(data.AsSpan(0, len));
        };

        _children = new VisualCollection(this)
        {
            _selectionVisual,
            _linkHoverVisual,
            _scrollbarVisual,
            _caretVisual,
            _copyAnimVisual,
            _pathCompletionVisual,
        };

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) =>
        {
            _caretBlinkVisible = !_caretBlinkVisible;
            DrawCaret();
        };

        InitializeScrollbar();

        ApplySettings();
        AppSettings.Instance.PropertyChanged += OnSettingsChanged;

        RenderSelfTest.Run(this);

        Loaded += (_, _) =>
        {
            if (_disposed)
                return;
            StartClipboardListener();
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            RebuildFontMetrics();
            if (_needsFullRedraw && IsVisible)
                FlushRedraw();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (_disposed)
                return;
            if (!IsVisible)
            {
                ClosePathCompletion();
                StopCopyAnimation();
                ClearLinkHover();
            }
            if (IsVisible)
            {
                _terminal.InvalidateCellCache();
                _needsFullRedraw = true;
                if (IsLoaded)
                    FlushRedraw();
            }
            UpdateBlinkTimer();
        };
        Unloaded += (_, _) =>
        {
            StopClipboardListener();
            ClosePathCompletion();
            StopCopyAnimation();
            ClearLinkHover();
        };
        // Start the shell immediately instead of waiting for layout to finish
        // calculating the exact grid size. This hides the 50-200ms ConPTY startup
        // cost behind the rest of WPF's window layout and realization time.
        if (RenderSelfTest.ReportPath is null)
        {
            StartSession();
        }
    }

    private void UpdateBlinkTimer()
    {
        var cursor = _terminal.Cursor;
        var blink = _cursorBlinkSetting && cursor.Blinking && cursor.Visible;
        if (blink && IsKeyboardFocused && IsVisible)
            _blinkTimer.Start();
        else
        {
            _blinkTimer.Stop();
            _caretBlinkVisible = true;
        }
    }

    private void DrawCaret()
    {
        var cursor = _terminal.Cursor;
        var hidden = (!_selfTestCaret && _session is null) || !cursor.Visible;
        var row = hidden ? -1 : cursor.Y;
        var col = hidden ? -1 : Math.Min(cursor.X, _cols - 1);
        var blinkOn = !_cursorBlinkSetting || !cursor.Blinking || !IsKeyboardFocused || _caretBlinkVisible;
        var style = (int)cursor.Shape;
        // A cursor above the viewport (scrolled-up scrollback) is hidden.
        var shouldDraw = !hidden && blinkOn && row >= 0 && row < _rows;

        if (_caretCacheValid &&
            _caretCacheShouldDraw == shouldDraw &&
            _caretCacheRow == row &&
            _caretCacheCol == col &&
            _caretCacheStyle == style)
            return;

        _caretCacheValid = true;
        _caretCacheShouldDraw = shouldDraw;
        _caretCacheRow = row;
        _caretCacheCol = col;
        _caretCacheStyle = style;

        using var dc = _caretVisual.RenderOpen();
        if (!shouldDraw)
            return;

        var x = col * _cellWidth;
        var y = row * _cellHeight;

        switch (cursor.Shape)
        {
            case CursorShape.Bar:
                dc.DrawRectangle(_palette.Cursor, null, new Rect(x, y, Math.Max(2, _cellWidth / 6), _cellHeight));
                break;
            case CursorShape.Underline:
                dc.DrawRectangle(_palette.Cursor, null, new Rect(x, y + _cellHeight - 2, _cellWidth, 2));
                break;
            default:
                // Block cursor: reverse-video the cell, as xterm does.
                dc.DrawRectangle(_palette.Cursor, null, new Rect(x, y, _cellWidth, _cellHeight));
                if (row < _terminal.FrameRows.Length)
                {
                    var cells = _terminal.FrameRows[row].Cells;
                    if (col < cells.Length && cells[col].Text.Length > 0)
                    {
                        var text = cells[col].Text;
                        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            _normalTypeface, _fontSize, _palette.Background, _pixelsPerDip);
                        dc.DrawText(formatted, new Point(x, y));
                    }
                }
                break;
        }
    }


    // ---- Scrollbar --------------------------------------------------------

    private static Brush FrozenBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ---- Dispose ------------------------------------------------------------


    public void Dispose()
    {
        StopOutputRedraw();
        _programStatusTimer?.Stop();
        TerminalSession? session;
        lock (_sessionLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            session = _session;
            _session = null;
        }
        StopCopyAnimation();
        ClearLinkHover();
        _pendingSessionInput = null;
        StopClipboardListener();
        ClosePathCompletion();
        _pathSearchCancellation?.Dispose();
        // WPF can deliver focus loss and Unloaded after the view is disposed.
        _pathSearchCancellation = null;
        _pathCompletionIndex?.Dispose();
        _pathCompletionIndex = null;
        _blinkTimer.Stop();
        _scrollbarAnimTimer.Stop();
        _scrollbarHideTimer.Stop();
        if (_resizeSettleTimer is { } settle)
        {
            settle.Stop();
            settle.Tick -= OnResizeSettled;
        }
        AppSettings.Instance.PropertyChanged -= OnSettingsChanged;
        _prewarmLease?.Dispose();
        _prewarmLease = null;
        if (session is not null)
        {
            session.OutputReceived -= OnSessionOutput;
            if (_sessionExitedHandler is { } exitHandler)
                session.Exited -= exitHandler;
            session.Dispose();
        }
        _sessionExitedHandler = null;
        lock (_terminal.SyncRoot)
        {
            _terminal.Dispose();
        }
    }

    // FocusTerminal is called by the workspace when the pane gets activated.
    public void FocusTerminal() => Focus();

    /// <summary>True while an app is running on the alternate screen buffer
    /// (a full-screen TUI); app-level shortcuts yield to the TUI then.</summary>
    public bool IsTuiMode => _terminal.IsAlternateScreen;
}
