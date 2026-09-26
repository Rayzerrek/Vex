using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly DispatcherTimer _scrollbarAnimTimer;
    private readonly StringBuilder _runBuilder = new();
    private int[] _runWidths = Array.Empty<int>();

    // Per-row run caches: one glyph-index array per row, sized to the row
    // width, reused across redraws.  A row can have at most (cols+1)/2 runs
    // (alternating background runs), so the text-run pool is sized to that.
    private volatile TerminalSession? _session;
    private TerminalSessionPrewarmer.Lease? _prewarmLease;
    private bool _sessionStarting;
    private TerminalPalette _palette = new(BuiltInThemes.VexDark);
    private FontFamily _fontFamily = new("Cascadia Mono");
    private double _fontSize = 13;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _pixelsPerDip = 1.0;
    private int _nativeCellWidth = 8;
    private int _nativeCellHeight = 16;
    private Typeface _normalTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private Typeface _boldTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private Typeface _italicTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private Typeface _boldItalicTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
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

    // Detected-URL state. Rows are scanned once per painted content (keyed by
    // the same hash the row cache uses); hit-testing and underline drawing
    // read the cached spans between redraws. Source-generated: no Regex
    // construction at startup and no per-row match-object overhead.
    [GeneratedRegex(@"[a-z][a-z0-9+.\-]*://[^\s<>\u0000-\u001f""']+|www\.[^\s<>\u0000-\u001f""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();
    private ulong[] _rowLinkHashes = Array.Empty<ulong>();
    private LinkSpan[][] _rowLinks = Array.Empty<LinkSpan[]>();
    private string?[] _rowLinkTexts = Array.Empty<string?>();
    private char[] _linkText = Array.Empty<char>();
    private int[] _linkCharToCol = Array.Empty<int>();
    private LinkSpan[] _linkSpans = Array.Empty<LinkSpan>();
    private Pen _linkPen = new(Brushes.Blue, 1);

    // Last viewport offset seen after UpdateFrame; a change means the
    // viewport moved (wheel, PgUp/Dn, autoscroll) and every visible row
    // maps to different content.
    private ulong _lastScrollOffset;
    private bool _resizeScheduled;
    private readonly Dictionary<Key, TerminalKeyModifiers> _terminalKeysDown = new();
    private PendingTextKey? _pendingTextKey;

    // After a burst of size changes (window drag, sidebar), one settle pass
    // re-syncs ConPTY and forces a full cell read so wrapped lines do not
    // stay broken if an intermediate resize failed or raced the paint.
    private DispatcherTimer? _resizeSettleTimer;
    private short _pendingSessionCols;
    private short _pendingSessionRows;
    private bool _sessionResizePending;

    private readonly record struct PendingTextKey(Key WpfKey, TerminalKey Key, TerminalKeyAction Action, TerminalKeyModifiers Modifiers);

    // While the sidebar animates, the window width changes every frame and
    // each size event would resize the VT emulator (buffer reflow) plus the
    // ConPTY session. MainWindow suspends resizes for the animation duration
    // and calls ResumeResizes() when it settles.
    internal static bool ResizeSuspended { get; private set; }

    /// <summary>Raised on the UI thread when resize suspension ends. Every
    /// live control then recalculates its grid exactly once — necessary
    /// because the suspension swallows all intermediate size events and the
    /// final width usually equals the last animated frame, which fires no
    /// SizeChanged at all. Without this the grid (and the ConPTY size behind
    /// it) stays at the pre-animation dimensions: a fullscreen TUI like nvim
    /// then covers only part of the control and the rest renders as bare
    /// background.</summary>
    private static event Action? ResizeSuspensionEnded;

    internal static void SuspendResizes() => ResizeSuspended = true;

    internal static void ResumeResizes()
    {
        if (!ResizeSuspended)
            return;
        ResizeSuspended = false;
        ResizeSuspensionEnded?.Invoke();
    }

    private GlyphTypeface? _normalGlyph;
    private GlyphTypeface? _boldGlyph;
    private GlyphTypeface? _italicGlyph;
    private GlyphTypeface? _boldItalicGlyph;
    private double _baselineY;

    private readonly object _outputLock = new();
    private bool _redrawScheduled;

    private bool _caretBlinkVisible = true;
    private bool _cursorBlinkSetting = true;
    private bool _selectionActive;
    private bool _selectionDragged;
    private bool _selectionGestureActive;
    private int _selectionClickCount;
    private bool _mouseSelectionOverride;
    private bool _mouseTracking;
    private bool _kbSelectionActive;
    private int _kbAnchorCol, _kbAnchorRow;
    private int _kbFocusCol, _kbFocusRow;
    private bool _wasAlternateScreen;

    private static readonly string? DiagPath = Environment.GetEnvironmentVariable("VEX_DIAG");
    internal static void Diag(string message)
    {
        if (DiagPath is null)
            return;
        try { System.IO.File.AppendAllText(DiagPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n"); }
        catch { }
    }

    // Scrollbar overlay state. The bar hugs the right edge and stays hidden
    // until the user scrolls, drags, or hovers it — then fades out after a
    // short idle. Hover/drag also widens the thumb (animated).
    private const double ScrollbarThinWidth = 6;
    private const double ScrollbarWideWidth = 12;
    private const double ScrollbarHitWidth = 16;
    private const double ScrollbarHideDelayMs = 900;
    private double _scrollbarWidth = ScrollbarThinWidth;
    private double _scrollbarTargetWidth = ScrollbarThinWidth;
    private double _scrollbarOpacity;
    private double _scrollbarTargetOpacity;
    private bool _scrollbarHovered;
    private bool _scrollbarDragging;
    private double _scrollbarDragOffset;
    private Brush _scrollbarThumbBrush = Brushes.Gray;
    private Brush _scrollbarThumbHoverBrush = Brushes.LightGray;
    private readonly DispatcherTimer _scrollbarHideTimer;

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

    public NativeTerminalControl(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        MouseDown += OnTerminalMouseDown;
        MouseUp += OnTerminalMouseUp;
        // Grayscale antialiasing keeps glyph edges stable against opaque cell
        // backgrounds; ClearType subpixel AA fringes on the dark terminal.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        _terminal = new GhosttyTerminal(80, 24);
        _terminal.TitleChanged += title =>
        {
            // Feed now runs on the PTY reader thread, so titles arrive off
            // the UI thread; titles feed data-bound properties and must hop.
            if (Dispatcher.CheckAccess())
            {
                TitleRawChanged?.Invoke(title);
                TitleChanged?.Invoke(title);
            }
            else
            {
                var snapshot = title;
                _ = Dispatcher.BeginInvoke(() =>
                {
                    TitleRawChanged?.Invoke(snapshot);
                    TitleChanged?.Invoke(snapshot);
                });
            }
        };
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
        // "Did not detect DSR response" timeout. Responses go through the
        // session's WriteResponse, not a raw Write: ConPTY parses each input
        // write as one key encoding and drops ESC-prefixed chunks that are
        // not valid keys (DSR, OSC/DA reports), so they are fragmented.
        _terminal.WritePty += (data, len) =>
        {
            var session = _session;
            session?.WriteResponse(data.AsSpan(0, len));
        };

        _children = new VisualCollection(this)
        {
            _selectionVisual,
            _scrollbarVisual,
            _caretVisual,
        };

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) =>
        {
            _caretBlinkVisible = !_caretBlinkVisible;
            DrawCaret();
        };

        _scrollbarAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _scrollbarAnimTimer.Tick += (_, _) => AnimateScrollbarVisual();
        _scrollbarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollbarHideDelayMs) };
        _scrollbarHideTimer.Tick += (_, _) =>
        {
            _scrollbarHideTimer.Stop();
            if (!_scrollbarHovered && !_scrollbarDragging)
                SetScrollbarOpacity(0);
        };

        ApplySettings();
        AppSettings.Instance.PropertyChanged += OnSettingsChanged;
        // Suspension end (sidebar animation settle) must force one grid
        // recalculation even when no SizeChanged follows — see the event
        // declaration above.
        ResizeSuspensionEnded += OnResizeSuspensionEnded;

        RenderSelfTest.Run(this);

        Loaded += (_, _) =>
        {
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            RebuildFontMetrics();
        };
        // Start the shell immediately instead of waiting for layout to finish
        // calculating the exact grid size. This hides the 50-200ms ConPTY startup
        // cost behind the rest of WPF's window layout and realization time.
        if (RenderSelfTest.ReportPath is null)
        {
            StartSession();
        }
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Rebuild only what the changed setting actually affects. Settings
        // that no pane renders (sidebar, shell) used to trigger a full
        // palette/typeface rebuild plus a full redraw in every terminal.
        var settings = AppSettings.Instance;
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ThemeName):
                var theme = BuiltInThemes.Resolve(settings.ThemeName);
                _palette = PaletteFor(theme);
                ApplyTerminalColors();
                RebuildScrollbarBrushes();
                RebuildLinkPen();
                _renderVersion++;
                _needsFullRedraw = true;
                InvalidateVisual(); // background brush changed
                FlushRedraw();
                break;
            case nameof(AppSettings.FontFamily):
                ApplyTypefaces(settings.FontFamily);
                RebuildFontMetrics();
                _needsFullRedraw = true;
                FlushRedraw();
                break;
            case nameof(AppSettings.FontSize):
                _fontSize = settings.FontSize;
                RebuildFontMetrics();
                _needsFullRedraw = true;
                FlushRedraw();
                break;
            case nameof(AppSettings.CursorBlink):
                _cursorBlinkSetting = settings.CursorBlink;
                UpdateBlinkTimer();
                DrawCaret();
                break;
        }
    }

    /// <summary>A family's four typeface/glyph pairs, cached per process:
    /// TryGetGlyphTypeface parses font files, so rebuilding it on every
    /// settings touch (even sidebar toggles) is wasteful.</summary>
    private sealed record TypefaceSet(
        Typeface Normal, Typeface Bold, Typeface Italic, Typeface BoldItalic,
        GlyphTypeface? NormalGlyph, GlyphTypeface? BoldGlyph, GlyphTypeface? ItalicGlyph, GlyphTypeface? BoldItalicGlyph);

    private static readonly Dictionary<string, TerminalPalette> PaletteCache = new();
    private static readonly Dictionary<string, TypefaceSet> TypefaceCache = new();

    /// <summary>
    /// Prewarms the theme palette (frozen brushes) and OpenType typeface/glyph caches
    /// on a background task during startup so the first terminal pane realizes instantly
    /// on the UI thread with zero font-parsing latency.
    /// </summary>
    public static void Prewarm(string? themeName, string? fontFamily)
    {
        try
        {
            var theme = BuiltInThemes.Resolve(themeName);
            PaletteFor(theme);
            TypefacesFor(fontFamily ?? "Cascadia Mono");
            // Touch the native VT library off the UI thread so the first
            // GhosttyTerminal does not pay LoadLibrary inside the first frame.
            System.Runtime.InteropServices.NativeLibrary.TryLoad("ghostty-vt", out _);
        }
        catch
        {
            // Best-effort prewarm
        }
    }

    private static TerminalPalette PaletteFor(TerminalTheme theme)
    {
        // All panes share one palette per theme. A TerminalPalette builds 256
        // frozen brushes, so constructing one per pane per settings change
        // multiplied every slider tick by the pane count.
        lock (PaletteCache)
        {
            if (!PaletteCache.TryGetValue(theme.Name, out var palette))
                PaletteCache[theme.Name] = palette = new TerminalPalette(theme);
            return palette;
        }
    }

    /// <summary>Evicts a theme's cached palette so the next render rebuilds it
    /// from the current theme values. Used by the theme editor after mutating
    /// <see cref="BuiltInThemes.Custom"/>.</summary>
    public static void InvalidatePaletteCache(string themeName)
    {
        lock (PaletteCache)
            PaletteCache.Remove(themeName);
    }

    private static TypefaceSet TypefacesFor(string familySource)
    {
        lock (TypefaceCache)
        {
            if (TypefaceCache.TryGetValue(familySource, out var set))
                return set;
            var family = new FontFamily(familySource);
            var normal = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var italic = new Typeface(family, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
            var boldItalic = new Typeface(family, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
            normal.TryGetGlyphTypeface(out var normalGlyph);
            bold.TryGetGlyphTypeface(out var boldGlyph);
            italic.TryGetGlyphTypeface(out var italicGlyph);
            boldItalic.TryGetGlyphTypeface(out var boldItalicGlyph);
            set = new TypefaceSet(normal, bold, italic, boldItalic, normalGlyph, boldGlyph, italicGlyph, boldItalicGlyph);
            TypefaceCache[familySource] = set;
            return set;
        }
    }

    private void ApplyTypefaces(string familySource)
    {
        _fontFamily = new FontFamily(familySource);
        var set = TypefacesFor(familySource);
        _normalTypeface = set.Normal;
        _boldTypeface = set.Bold;
        _italicTypeface = set.Italic;
        _boldItalicTypeface = set.BoldItalic;
        _normalGlyph = set.NormalGlyph;
        _boldGlyph = set.BoldGlyph;
        _italicGlyph = set.ItalicGlyph;
        _boldItalicGlyph = set.BoldItalicGlyph;
    }

    private void ApplySettings()
    {
        var settings = AppSettings.Instance;
        var theme = BuiltInThemes.Resolve(settings.ThemeName);
        _palette = PaletteFor(theme);
        ApplyTerminalColors();
        RebuildScrollbarBrushes();
        RebuildLinkPen();
        ApplyTypefaces(settings.FontFamily);
        _fontSize = settings.FontSize;
        _cursorBlinkSetting = settings.CursorBlink;
        UpdateBlinkTimer();
    }

    /// <summary>Pushes the theme's default colors into the emulator so ANSI
    /// and OSC color queries resolve against them.</summary>
    private void ApplyTerminalColors()
    {
        var fg = ((SolidColorBrush)_palette.Foreground).Color;
        var bg = ((SolidColorBrush)_palette.Background).Color;
        var cursor = ((SolidColorBrush)_palette.Cursor).Color;
        _terminal.SetDefaultColors(
            new GhosttyColorRgb(fg.R, fg.G, fg.B),
            new GhosttyColorRgb(bg.R, bg.G, bg.B),
            new GhosttyColorRgb(cursor.R, cursor.G, cursor.B),
            _palette.PaletteRgb());
    }

    private void UpdateBlinkTimer()
    {
        var cursor = _terminal.Cursor;
        var blink = _cursorBlinkSetting && cursor.Blinking;
        if (blink && IsKeyboardFocused)
            _blinkTimer.Start();
        else
        {
            _blinkTimer.Stop();
            _caretBlinkVisible = true;
        }
    }

    // ---- Font / cell geometry --------------------------------------------

    private void RebuildFontMetrics()
    {
        // Cell geometry is part of every row's pixels; any rebuild invalidates
        // all row caches so the next pass repaints the surface.
        _renderVersion++;

        // The glyph face was already resolved (and the font file parsed) by
        // ApplyTypefaces; reusing it here avoids a second font-file parse on
        // every rebuild.
        var typeface = _normalTypeface;
        _baselineY = _fontSize * _fontFamily.Baseline;

        if (_normalGlyph is { } glyph)
        {
            var em = _fontSize;
            var map = glyph.CharacterToGlyphMap;
            var advance = map.TryGetValue('M', out var mGlyph) ? glyph.AdvanceWidths[mGlyph] : glyph.AdvanceWidths[0];
            _cellWidth = Math.Max(1, advance * em);
            _cellHeight = Math.Max(1, Math.Ceiling(em * _fontFamily.LineSpacing));
        }
        else
        {
            var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, _fontSize, Brushes.White, _pixelsPerDip);
            _baselineY = probe.Baseline;
            _cellWidth = Math.Max(1, probe.WidthIncludingTrailingWhitespace);
            _cellHeight = Math.Max(1, Math.Ceiling(probe.Height));
        }

        RecalculateGridSize();
    }

    private void RecalculateGridSize()
    {
        if (!IsLoaded || ActualWidth < 150 || ActualHeight < 80)
            return;
        // Coalesced away while the sidebar animates: reflowing the VT buffer
        // and resizing ConPTY once per animation frame guarantees jank. The
        // resume event forces exactly one recalc after the animation settles.
        if (ResizeSuspended)
            return;

        var cols = Math.Max(2, (int)(ActualWidth / _cellWidth));
        var rows = Math.Max(1, (int)(ActualHeight / _cellHeight));
        var nativeCellWidth = Math.Max(1, (int)Math.Round(_cellWidth * _pixelsPerDip));
        var nativeCellHeight = Math.Max(1, (int)Math.Round(_cellHeight * _pixelsPerDip));
        var gridChanged = cols != _cols || rows != _rows;
        if (!gridChanged && nativeCellWidth == _nativeCellWidth && nativeCellHeight == _nativeCellHeight
            && (_session is not null || _sessionStarting))
            return;

        // Preserve the user's scrollback pin: only snap to bottom when they
        // were already there. Unconditional ScrollToBottom after every reflow
        // is what made resize feel like the buffer was reloading and jumping.
        var wasAtBottom = !gridChanged || IsPinnedToBottom(_terminal.Scrollbar);

        _cols = cols;
        _rows = rows;
        _nativeCellWidth = nativeCellWidth;
        _nativeCellHeight = nativeCellHeight;
        Diag($"grid {cols}x{rows} session={_session is not null}");
        EnsureRowVisuals();
        if (gridChanged)
            InvalidateRowPaintCaches();
        _terminal.Resize(cols, rows, nativeCellWidth, nativeCellHeight);
        if (gridChanged && wasAtBottom)
            _terminal.ScrollToBottom();
        _needsFullRedraw = true;

        if (_session is null)
        {
            // Start the shell as soon as this pane has a grid — not on
            // keyboard focus. Focus arrives only after the first frame plus
            // a ContextIdle dispatch, which used to delay the prompt by
            // hundreds of milliseconds; starting here warms the shell up
            // behind the first painted frame like Windows Terminal. Only
            // the selected tab's panes are realized, so this cannot spawn
            // shells for background tabs.
            // UpdateFrame must run before the repaint so FrameRows reflects
            // the new geometry before StartSession paints the first frame.
            FlushRedraw();
            StartSessionIfReady();
        }
        else if (gridChanged)
        {
            // Resize ConPTY before painting so the child and VT agree on the
            // new grid for this frame. Failures are retried on settle.
            ApplySessionResize((short)cols, (short)rows);
            FlushRedraw();
            ScheduleResizeSettle();
        }
        else
        {
            FlushRedraw();
        }
    }

    private static bool IsPinnedToBottom((ulong Total, ulong Offset, ulong Len) sb)
    {
        if (sb.Len == 0 || sb.Total <= sb.Len)
            return true;
        return sb.Offset + sb.Len >= sb.Total;
    }

    private void ApplySessionResize(short cols, short rows)
    {
        var session = _session;
        if (session is null)
            return;
        _pendingSessionCols = cols;
        _pendingSessionRows = rows;
        // In-band resize (DEC 2048) reports must follow a *successful* ConPTY
        // resize: under ConPTY the child has no other way to learn the new
        // grid, and libghostty-vt answers the app's DECRQM probe with
        // "supported", which promised the report. The emulator's mode state
        // already reflects ?2048h by the time a resize happens, because apps
        // enable the mode during startup queries.
        var report = _terminal.InBandResize;
        if (!session.Resize(cols, rows, report))
        {
            _sessionResizePending = true;
            Diag($"conpty-resize-failed {cols}x{rows}");
        }
        else
        {
            _sessionResizePending = false;
            if (report)
                Diag($"in-band-resize-report {cols}x{rows}");
        }
    }

    private void ScheduleResizeSettle()
    {
        _resizeSettleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _resizeSettleTimer.Tick -= OnResizeSettled;
        _resizeSettleTimer.Tick += OnResizeSettled;
        _resizeSettleTimer.Stop();
        _resizeSettleTimer.Start();
    }

    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _resizeSettleTimer?.Stop();
        if (_disposed)
            return;

        // Heal ConPTY desync and any frame that painted mid-reflow with a
        // stale cell cache — the common "every line looks broken" after a
        // fast window drag.
        if (_session is not null)
            ApplySessionResize((short)_cols, (short)_rows);
        _terminal.InvalidateCellCache();
        _needsFullRedraw = true;
        FlushRedraw();

        if (_sessionResizePending)
            Diag($"conpty-resize-still-pending {_cols}x{_rows}");
    }

    private void StartSessionIfReady()
    {
        if (_session is not null || _sessionStarting || RenderSelfTest.ReportPath is not null)
            return;
        StartSession();
        _terminal.ScrollToBottom();
    }

    private void EnsureRowVisuals()
    {
        while (_rowVisuals.Count > _rows)
        {
            var last = _rowVisuals[^1];
            _rowVisuals.RemoveAt(_rowVisuals.Count - 1);
            _children.Remove(last);
        }
        while (_rowVisuals.Count < _rows)
        {
            var visual = new DrawingVisual();
            _rowVisuals.Add(visual);
            // Rows sit between the selection overlay and the scrollbar; the
            // caret stays on top. The scrollbar is the second-to-last child.
            _children.Insert(_children.Count - 2, visual);
        }
        if (_rowHashes.Length != _rows)
        {
            // Version 0 never matches the current _renderVersion, so the
            // cells are all repainted on the next pass after a resize.
            _rowHashes = new ulong[_rows];
            _rowVersions = new int[_rows];
            _rowLinks = new LinkSpan[_rows][];
            _rowLinkHashes = new ulong[_rows];
            _rowLinkTexts = new string?[_rows];
        }
    }

    /// <summary>Drops per-row paint caches after a width change so wrapped
    /// glyphs cannot reuse pre-resize hashes.</summary>
    private void InvalidateRowPaintCaches()
    {
        if (_rowHashes.Length == 0)
            return;
        Array.Clear(_rowHashes);
        Array.Clear(_rowVersions);
        Array.Clear(_rowLinkHashes);
        Array.Clear(_rowLinkTexts);
        for (var i = 0; i < _rowLinks.Length; i++)
            _rowLinks[i] = Array.Empty<LinkSpan>();
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size availableSize)
        => double.IsInfinity(availableSize.Width) || double.IsInfinity(availableSize.Height)
            ? new Size(0, 0)
            : availableSize;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // Coalesce to one pass per rendered frame: while the window edge is
        // being dragged, size events can fire several times per frame and each
        // one would resize the emulator (buffer reflow) and the ConPTY session.
        if (_resizeScheduled)
            return;
        _resizeScheduled = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _resizeScheduled = false;
            RecalculateGridSize();
        });
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _pixelsPerDip = newDpi.PixelsPerDip;
        // Glyph rasterization scales with pixels-per-dip even when the DIP
        // cell size does not, so the cached row pixels and native mouse
        // geometry are stale.
        _renderVersion++;
        _needsFullRedraw = true;
        RecalculateGridSize();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_palette.Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    // ---- Session ----------------------------------------------------------

    private void StartSession()
    {
        if (_session is not null || _sessionStarting)
            return;

        _sessionStarting = true;
        // New tabs should not create a second, short-lived prewarm. The
        // prewarmer is single-use and only belongs to the initial restored
        // pane; subsequent panes start directly with their actual geometry.
        var cols = _cols >= 20 ? (short)_cols : (short)80;
        var rows = _rows >= 5 ? (short)_rows : (short)24;
        var workingDirectory = _workingDirectory;
        var shellId = AppSettings.Instance.ShellId;

        var prewarmLease = TerminalSessionPrewarmer.Take(workingDirectory, shellId);
        _prewarmLease = prewarmLease;
        if (prewarmLease != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var prewarmed = await prewarmLease.SessionTask.ConfigureAwait(false);
                    prewarmLease.AttachOutputHandler(OnSessionOutput);
                    prewarmLease.Dispose();
                    _prewarmLease = null;
                    prewarmed.Exited += OnSessionExited;
                    _session = prewarmed;
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        _sessionStarting = false;
                        if (_disposed)
                        {
                            _session = null;
                            prewarmed.Dispose();
                            return;
                        }
                        if (_cols != cols || _rows != rows)
                            ApplySessionResize((short)_cols, (short)_rows);
                    });
                }
                catch
                {
                    prewarmLease.Dispose();
                    _prewarmLease = null;
                    _sessionStarting = false;
                    _ = Dispatcher.BeginInvoke(StartSession);
                }
            });
            return;
        }

        _ = Task.Run(() =>
        {
            var resolved = ShellRegistry.Resolve(shellId);
            var shellProgram = SelfTestShell ?? resolved?.Program ?? TerminalSession.DefaultShell();
            var shellArguments = SelfTestShell is null ? resolved?.Arguments : null;

            TerminalSession session;
            try
            {
                session = CreateAndStartSession(workingDirectory, cols, rows, shellProgram, shellArguments);
            }
            catch (Win32Exception)
            {
                session = CreateAndStartSession(workingDirectory, cols, rows, TerminalSession.DefaultShell(), null);
            }
            StartupMark.Note("terminal spawn end");
            if (_disposed)
            {
                session.Dispose();
                _ = Dispatcher.BeginInvoke(() => _sessionStarting = false);
                return;
            }
            _session = session;
            _ = Dispatcher.BeginInvoke(() =>
            {
                _sessionStarting = false;
                if (_disposed)
                {
                    _session = null;
                    session.Dispose();
                    return;
                }
                if (_cols != cols || _rows != rows)
                    ApplySessionResize((short)_cols, (short)_rows);
            });
        });
    }

    private TerminalSession CreateAndStartSession(string workingDirectory, short cols, short rows, string? shell, string? arguments)
    {
        var session = new TerminalSession();
        session.OutputReceived += OnSessionOutput;
        session.Exited += OnSessionExited;
        session.OutputReceived += data =>
        {
            if (Interlocked.Exchange(ref _firstOutputNoted, 1) == 0)
                StartupMark.Note("first terminal output");
        };
        session.Start(workingDirectory, cols, rows, shell, arguments);
        return session;
    }

    private int _firstOutputNoted;

    /// <summary>PID of the ConPTY shell process; null before the session starts.</summary>
    public int? ProcessId => _session?.ProcessId;

    private void OnSessionOutput(ArraySegment<byte> chunk)
    {
        if (chunk.Array is not { } buffer)
            return;
        try
        {
            // Feed on this PTY reader thread, not on the UI thread. The
            // emulator answers queries (DSR, OSC color reports, DA) through
            // WritePty synchronously inside Feed, so responses reach ConPTY
            // in microseconds instead of waiting behind a UI-thread pump and
            // a full render pass — the wait Neovim's 100 ms
            // "Did not detect DSR response" timeout tripped on. Order is
            // preserved: chunks arrive here in pipe order and Feed serializes
            // them through the terminal lock.
            lock (_terminal.SyncRoot)
            {
                if (_disposed)
                    return;
                try
                {
                    _terminal.Feed(buffer, chunk.Offset, chunk.Count);
                }
                catch (Exception e)
                {
                    // Guard against malformed input states; the next redraw
                    // repaints everything from the emulator state.
                    var tail = Convert.ToHexString(buffer, Math.Max(0, chunk.Count - 24), Math.Min(24, chunk.Count));
                    Diag($"feed-EXCEPTION {e.GetType().Name}: {e.Message} chunk={chunk.Count} tail={tail}");
                    _needsFullRedraw = true;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        ScheduleRedraw();
    }

    private void ScheduleRedraw()
    {
        // Coalesce all output that arrives during a frame into one render
        // pass. Feeding already happened above, so a redundant pass only
        // repaints rows the emulator marked dirty since the last snapshot.
        lock (_outputLock)
        {
            if (_redrawScheduled)
                return;
            _redrawScheduled = true;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            lock (_outputLock)
            {
                _redrawScheduled = false;
            }
            // Output that arrives during the flush sets the flag again and
            // schedules its own pass; the emulator state it fed is picked up
            // there, so no update is lost. Render priority keeps long agent
            // streams visually in lockstep with the frame instead of lagging
            // behind Background work.
            if (!_disposed)
                FlushRedraw();
        });
    }

    private void OnSessionExited(int exitCode)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
                return;
            _terminal.Feed($"\r\n\x1b[2m[process exited with code {exitCode}]\x1b[m\r\n");
            FlushRedraw();
            ProcessExited?.Invoke(exitCode);
        });
    }

    /// <summary>
    /// Writes one emulator response (DSR, OSC/DA report, ...) into ConPTY.
    /// ConPTY parses each input write as a single key encoding and drops
    /// ESC-prefixed chunks that are not valid keys — an atomically written
    /// <c>ESC[0n</c> never reaches the child, which is exactly Neovim's
    /// "Did not detect DSR response" warning. Fragmenting at every ESC makes
    /// each chunk decode to plain key events (a lone ESC, then ordinary
    /// characters) that the child reassembles into the response bytes.
    /// Keystroke/mouse/paste input must stay atomic (arrows are <c>ESC[A</c>)
    /// and never goes through here.
    /// </summary>
    private void WriteResponseToPty(byte[] data, int length)
    {
        _session?.WriteResponse(data.AsSpan(0, length));
    }

    // ---- Rendering --------------------------------------------------------

    private void FlushRedraw()
    {
        // Copy the static so the null state of the stopwatch matches the
        // guard below without flow-tracking a static field read.
        var diagPath = DiagPath;
        var flushStarted = diagPath is null ? null : System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _terminal.UpdateFrame();
            var dirty = _terminal.FrameDirty;
            _mouseTracking = _terminal.MouseTracking;
            var scrollbar = _terminal.Scrollbar;
            var viewportMoved = scrollbar.Offset != _lastScrollOffset;
            _lastScrollOffset = scrollbar.Offset;

            // Detect alternate-screen transitions (TUI start/exit) and notify
            // the pane model so it can update the state indicator. Force a
            // full rebuild: alt enter/exit rematerializes every row and dirty
            // bits alone have under-reported the swap.
            var isAlt = _terminal.IsAlternateScreen;
            if (isAlt != _wasAlternateScreen)
            {
                _wasAlternateScreen = isAlt;
                _needsFullRedraw = true;
                _terminal.InvalidateCellCache();
                TuiModeChanged?.Invoke(isAlt);
            }

            if (flushStarted is not null)
                Diag($"flush dirty={dirty} full={_needsFullRedraw} scroll={viewportMoved} offset={scrollbar.Offset}/{scrollbar.Total} rows={_rows} cols={_cols} ms={flushStarted.Elapsed.TotalMilliseconds:F4}");

            // Synchronized output (DEC 2026) publishes one atomic frame, and
            // the emulator already exposes the rows the application has
            // written so far. Painting mid-block shows a torn screen: new text
            // in the rows reached so far, the previous frame in the rest — the
            // duplicated/ghost lines seen while a TUI or an agent streams.
            // Defer instead; the flag forces a complete repaint the moment the
            // block closes, so nothing half-applied is ever shown.
            if (_terminal.SynchronizedOutput)
            {
                var now = Environment.TickCount64;
                if (_syncDeferSince == 0)
                    _syncDeferSince = now;
                // One bounded deferral per continuous block: an application
                // that leaves the mode set paints normally from here on
                // instead of batching every frame behind the limit.
                if (now - _syncDeferSince < SyncDeferralLimitMs)
                {
                    _needsFullRedraw = true;
                    if (diagPath is not null)
                        Diag("flush sync-deferred");
                    return;
                }
            }
            else
            {
                _syncDeferSince = 0;
            }

            if (_needsFullRedraw || dirty == FrameDirty.Full || viewportMoved)
            {
                RedrawAll(force: true);
            }
            else if (dirty == FrameDirty.Partial)
            {
                // Scan all rows: unchanged rows skip in microseconds via RowHash;
                // any row that shifted, scrolled, or updated repaints cleanly.
                // Do not trust FrameRow.Dirty alone — it under-reports shifts.
                for (var row = 0; row < Math.Min(_rows, _terminal.FrameRows.Length); row++)
                {
                    RedrawRow(row, force: false);
                }
                InvalidateCaretCache();
            }

            _needsFullRedraw = false;

            DrawSelection();
            DrawCaret();
            if (!IsScrollbarVisible() && _scrollbarTargetOpacity > 0)
                SetScrollbarOpacity(0);
            else
                DrawScrollbar();
        }
        catch (Exception e)
        {
            // Guard against emulator state issues; schedule a full redraw on
            // the next pump cycle.
            Diag($"flush-EXCEPTION {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n')[0]}");
            _renderFailures++;
            _needsFullRedraw = true;
        }
    }

    private void RedrawAll(bool force = false)
    {
        // A full redraw repaints every cell, including the one under the
        // block cursor, so the caret overlay must redraw with it (also covers
        // font metrics/DPI/resize changes that move the caret geometry).
        InvalidateCaretCache();
        for (var row = 0; row < _rowVisuals.Count; row++)
            RedrawRow(row, force);
        DrawScrollbar();
    }

    private void RedrawRow(int row, bool force = false)
    {
        if (row < 0 || row >= _rowVisuals.Count)
            return;

        var frameRows = _terminal.FrameRows;
        if (row >= frameRows.Length || frameRows[row].Cells.Length == 0)
        {
            _rowVersions[row] = 0;
            _rowLinks[row] = Array.Empty<LinkSpan>();
            _rowLinkTexts[row] = null;
            using var clearDc = _rowVisuals[row].RenderOpen();
            return;
        }

        var frameRow = frameRows[row];

        // Skip the re-render when this row's cells are unchanged since it was
        // last drawn — the common case after a redundant full redraw.
        var hash = RowHash(frameRow, _cols);
        if (!force && _rowVersions[row] == _renderVersion && _rowHashes[row] == hash)
            return;

        // The link scan is keyed by the same hash: equal hash means the row
        // content is identical, so the spans stay valid between redraws.
        var links = _rowLinkHashes[row] == hash ? _rowLinks[row] : (_rowLinks[row] = ComputeRowLinks(frameRow, row));

        using var dc = _rowVisuals[row].RenderOpen();

        var y = row * _cellHeight;
        var cells = frameRow.Cells;
        ref readonly var first = ref cells[0];
        var runFgTag = first.FgTag;
        var runFg = first.FgValue;
        var runBgTag = first.BgTag;
        var runBg = first.BgValue;
        var runFlags = first.Flags;
        var runStart = 0;
        _runBuilder.Clear();
        EnsureTextRunCapacity(Math.Max(1, _cols));
        // One width entry per UTF-16 unit the run loop copies, so a row of
        // multi-codepoint clusters needs more than the old 2*cols guess. An
        // undersized buffer threw IndexOutOfRange mid-row, and because every
        // retry aborted at the same row, everything below it stopped
        // repainting — stale/duplicated text until the row's content changed.
        EnsureRunWidthCapacity(Math.Max(1, RowTextUnits(cells, _cols)));
        var textRuns = 0;
        var runWidthCount = 0;

        var colCount = Math.Min(_cols, cells.Length);
        for (var col = 0; col < colCount; col++)
        {
            ref readonly var cell = ref cells[col];

            var cellChanged = cell.FgTag != runFgTag || cell.FgValue != runFg ||
                              cell.BgTag != runBgTag || cell.BgValue != runBg ||
                              cell.Flags != runFlags;
            if (cellChanged && _runBuilder.Length > 0)
            {
                textRuns = FlushRun(dc, _runBuilder, _runWidths, runWidthCount, runStart, y, runFgTag, runFg, runBgTag, runBg, runFlags, textRuns);
                runStart = col;
                runFgTag = cell.FgTag;
                runFg = cell.FgValue;
                runBgTag = cell.BgTag;
                runBg = cell.BgValue;
                runFlags = cell.Flags;
                _runBuilder.Clear();
                runWidthCount = 0;
            }
            else if (cellChanged)
            {
                runStart = col;
                runFgTag = cell.FgTag;
                runFg = cell.FgValue;
                runBgTag = cell.BgTag;
                runBg = cell.BgValue;
                runFlags = cell.Flags;
            }

            if (cell.Tail)
                continue; // wide glyph stub: the base cell owns the advance
            if (cell.Text.Length == 0)
            {
                _runBuilder.Append(' ');
                _runWidths[runWidthCount++] = 1;
            }
            else
            {
                // One width entry per UTF-16 unit: the first char of a cell
                // carries the cell advance, continuation chars carry zero so
                // the glyph cluster advances once (astral and ZWJ clusters).
                _runBuilder.Append(cell.Text);
                _runWidths[runWidthCount++] = cell.Wide ? 2 : 1;
                for (var i = 1; i < cell.Text.Length; i++)
                    _runWidths[runWidthCount++] = 0;
            }
        }

        if (_runBuilder.Length > 0)
            textRuns = FlushRun(dc, _runBuilder, _runWidths, runWidthCount, runStart, y, runFgTag, runFg, runBgTag, runBg, runFlags, textRuns);

        // Draw underlines for runs that have text; the pooled glyph arrays
        // index into the textRuns we recorded.
        for (var i = 0; i < textRuns; i++)
        {
            ref readonly var run = ref _textRuns[i];
            if (!run.HasDecoration)
                continue;
            var runX = run.StartCol * _cellWidth;
            var runWidth = run.Length * _cellWidth;
            var pen = _palette.GetPen(run.Foreground ?? _palette.Foreground);
            var rowY = y;
            if (run.Underline)
                dc.DrawLine(pen, new Point(runX, rowY + _cellHeight - pen.Thickness), new Point(runX + runWidth, rowY + _cellHeight - pen.Thickness));
            if (run.CrossedOut)
                dc.DrawLine(pen, new Point(runX, rowY + _cellHeight / 2), new Point(runX + runWidth, rowY + _cellHeight / 2));
        }

        // Detected URLs get their own underline, independent of SGR styles.
        foreach (var link in links)
        {
            var linkX = link.StartCol * _cellWidth;
            var linkWidth = (link.EndCol - link.StartCol + 1) * _cellWidth;
            dc.DrawLine(_linkPen, new Point(linkX, y + _cellHeight - _linkPen.Thickness), new Point(linkX + linkWidth, y + _cellHeight - _linkPen.Thickness));
        }

        // Only now is the row painted in full, so only now may it be cached:
        // marking it earlier remembered a half-drawn row as up to date, which
        // left the missing pixels on screen until something else changed.
        _rowVersions[row] = _renderVersion;
        _rowHashes[row] = hash;
        _rowLinkHashes[row] = hash;

        int FlushRun(DrawingContext context, StringBuilder runText, int[] widths, int cellCount, int startCol, double rowY,
            ColorTag fgTag, int fgValue, ColorTag bgTag, int bgValue, CellFlags flags, int textRunIndex)
        {
            _palette.Resolve(fgTag, fgValue, bgTag, bgValue, flags, out var fg, out var bg);
            var x = startCol * _cellWidth;

            // One width entry per advance-consuming char (wide glyph stubs
            // are already skipped), so the column extent is the width sum.
            var cellsSpanned = 0;
            for (var i = 0; i < cellCount; i++)
                cellsSpanned += widths[i];

            // Only paint cells that carry their own background color. The
            // opaque base layer already covers default cells; filling every
            // run again adds work and can expose seams at fractional widths.
            if (bg is not null)
                context.DrawRectangle(bg, null, new Rect(x, rowY, cellsSpanned * _cellWidth, _cellHeight));

            // Trailing whitespace carries no ink; skip the text pass for it.
            var contentEnd = runText.Length;
            while (contentEnd > 0 && runText[contentEnd - 1] == ' ')
                contentEnd--;
            if (contentEnd == 0 || fg is null)
            {
                RecordRun(textRunIndex, startCol, cellsSpanned, fg, flags);
                return textRunIndex + 1;
            }

            var face = ResolveTypeface(flags);
            var glyphFace = ResolveGlyphTypeface(flags);
            // When BOLD is requested but the family has no bold face (glyph
            // resolution fell back to the regular face), emulate bold by
            // double-drawing the run with a 1px horizontal offset. Note: a
            // null glyphFace (FormattedText fallback) also needs this, so the
            // check is "did we NOT get a real bold face".
            var syntheticBold = (flags & CellFlags.Bold) != 0 && !ReferenceEquals(glyphFace, _boldGlyph);

            var contentLength = contentEnd;
            // Read the trimmed run directly from the run builder; no per-run
            // copy, so the segment loop and FormattedText below index runText
            // up to contentEnd.
            var trimmedText = runText;

            // Walk the trimmed run cell by cell. A cell whose every codepoint
            // has a glyph in the face joins a shared GlyphRun segment; any
            // other cell (emoji, symbols outside the family) is drawn alone
            // through FormattedText — whose shaping and font fallback can
            // find a glyph in another family — pinned to its exact grid
            // column. Falling back for the WHOLE run used natural font
            // advances, drifting every later character off the grid: text
            // slid under the next run's background and read as doubled or
            // overlapping letters whenever an emoji sat mid-line.
            var map = glyphFace?.CharacterToGlyphMap;
            var colCursor = startCol;

            if (map is not null)
            {
                // Pending GlyphRun segment state. The flushed per-segment
                // copies below must stay freshly allocated: WPF's
                // retained-mode DrawingContext keeps a reference to the
                // glyph/advance arrays until the render thread consumes the
                // visual, so reusing those across runs corrupts already-queued
                // runs (stale characters, jumbled glyphs). The staging area
                // here is internal to this run and safe to reuse.
                EnsureSegScratch(contentLength);
                var segIndices = _segIndicesScratch;
                var segAdvances = _segAdvancesScratch;
                var segUnits = 0;
                var segStartCol = startCol;

                void FlushSegment()
                {
                    if (segUnits == 0)
                        return;
                    var indices = new ushort[segUnits];
                    var advances = new double[segUnits];
                    Array.Copy(segIndices, indices, segUnits);
                    Array.Copy(segAdvances, advances, segUnits);
                    DrawGlyph(indices, advances, segStartCol * _cellWidth);
                    segUnits = 0;
                }

                void DrawGlyph(ushort[] indices, double[] advances, double originX)
                {
                    var run = new GlyphRun(
                        glyphFace!,
                        0,
                        false,
                        _fontSize,
                        (float)_pixelsPerDip,
                        indices,
                        new Point(originX, rowY + _baselineY),
                        advances,
                        null, null, null, null, null, null);
                    context.DrawGlyphRun(fg, run);
                    if (!syntheticBold)
                        return;
                    // Second pass offset by 1 physical pixel (fractional in DIPs
                    // for subpixel crispness), the classic cheap fake-bold.
                    var boldRun = new GlyphRun(
                        glyphFace!,
                        0,
                        false,
                        _fontSize,
                        (float)_pixelsPerDip,
                        indices,
                        new Point(originX + (1.0 / _pixelsPerDip), rowY + _baselineY),
                        advances,
                        null, null, null, null, null, null);
                    context.DrawGlyphRun(fg, boldRun);
                }

                var i = 0;
                while (i < contentLength)
                {
                    // A cell spans one width-consuming unit plus its
                    // zero-width continuation units.
                    var unitStart = i;
                    i++;
                    while (i < contentLength && widths[i] == 0)
                        i++;

                    var firstGlyph = segUnits;
                    if (segUnits == 0)
                        segStartCol = colCursor;
                    var cellOk = true;
                    var u = unitStart;
                    while (u < i)
                    {
                        var ch = trimmedText[u];
                        int cp;
                        var span = 1;
                        if (char.IsHighSurrogate(ch) && u + 1 < i && char.IsLowSurrogate(trimmedText[u + 1]))
                        {
                            cp = char.ConvertToUtf32(ch, trimmedText[u + 1]);
                            span = 2;
                        }
                        else
                        {
                            cp = ch;
                        }
                        if (!map.TryGetValue(cp, out var glyphIndex))
                        {
                            cellOk = false;
                            break;
                        }
                        segIndices[segUnits] = glyphIndex;
                        segAdvances[segUnits] = 0;
                        segUnits++;
                        u += span;
                    }
                    if (cellOk)
                    {
                        // True cell advance: a wide glyph consumes two
                        // columns, so the next cell starts where the grid
                        // places it.
                        segAdvances[firstGlyph] = widths[unitStart] * _cellWidth;
                    }
                    else
                    {
                        segUnits = firstGlyph;
                        FlushSegment();
                        var formatted = new FormattedText(
                            trimmedText.ToString(unitStart, i - unitStart),
                            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            face, _fontSize, fg, _pixelsPerDip);
                        var fx = colCursor * _cellWidth;
                        context.DrawText(formatted, new Point(fx, rowY));
                        if (syntheticBold)
                            context.DrawText(formatted, new Point(fx + (1.0 / _pixelsPerDip), rowY));
                    }
                    colCursor += widths[unitStart];
                }
                FlushSegment();
            }
            else
            {
                // No usable face at all: shape the whole trimmed run through
                // FormattedText's font fallback. StringBuilder.ToString(start,
                // length) avoids copying the trailing-whitespace tail.
                var formatted = new FormattedText(trimmedText.ToString(0, contentLength), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    face, _fontSize, fg, _pixelsPerDip);
                context.DrawText(formatted, new Point(x, rowY));
            }
            RecordRun(textRunIndex, startCol, cellsSpanned, fg, flags);
            return textRunIndex + 1;
        }

        void RecordRun(int index, int startCol, int cellsSpanned, Brush? fg, CellFlags flags)
        {
            ref var run = ref _textRuns[index];
            run.StartCol = startCol;
            run.Length = cellsSpanned;
            run.Foreground = fg;
            run.HasDecoration = (flags & (CellFlags.Underline | CellFlags.Strikethrough)) != 0;
            run.Underline = (flags & CellFlags.Underline) != 0;
            run.CrossedOut = (flags & CellFlags.Strikethrough) != 0;
        }
    }

    private void EnsureTextRunCapacity(int required)
    {
        if (_textRuns.Length >= required)
            return;
        Array.Resize(ref _textRuns, required);
    }

    private void EnsureRunWidthCapacity(int required)
    {
        if (_runWidths.Length >= required)
            return;
        Array.Resize(ref _runWidths, required);
    }

    private struct TextRun
    {
        public int StartCol;
        public int Length;
        public bool HasDecoration;
        public bool Underline;
        public bool CrossedOut;
        public Brush? Foreground;
    }

    private TextRun[] _textRuns = Array.Empty<TextRun>();

    // Staging buffers for the pending GlyphRun segment inside FlushRun. Only
    // the flushed per-segment copies are handed to the DrawingContext (which
    // retains them); the staging area itself is reused across runs to avoid
    // two array allocations per text run per row per frame.
    private ushort[] _segIndicesScratch = Array.Empty<ushort>();
    private double[] _segAdvancesScratch = Array.Empty<double>();

    private void EnsureSegScratch(int required)
    {
        if (_segIndicesScratch.Length < required)
        {
            _segIndicesScratch = new ushort[required];
            _segAdvancesScratch = new double[required];
        }
    }

    private readonly record struct LinkSpan(int StartCol, int EndCol, int TextStart, int TextLength);

    /// <summary>
    /// Characters a row contributes to the pooled text and width buffers: one
    /// per cell, or one per UTF-16 unit for multi-codepoint grapheme clusters
    /// (emoji ZWJ sequences, combining marks), which the render loop copies
    /// verbatim. Wide-glyph spacer tails contribute nothing.
    /// </summary>
    private static int RowTextUnits(CellInfo[] cells, int cols)
    {
        var units = 0;
        var colCount = Math.Min(cols, cells.Length);
        for (var col = 0; col < colCount; col++)
        {
            ref readonly var cell = ref cells[col];
            if (cell.Tail)
                continue;
            var text = cell.Text;
            units += text is null || text.Length == 0 ? 1 : text.Length;
        }
        return units;
    }

    /// <summary>
    /// FNV-1a over the cells RedrawRow renders: (text, width, flags, fg, bg)
    /// per column, mirroring the render loop's cell selection so equal hashes
    /// guarantee equal pixels for the current metrics. Every character of a
    /// cell's text is hashed so two different clusters of the same length
    /// cannot collide.
    /// </summary>
    internal static ulong RowHash(FrameRow row, int cols)
    {
        const ulong prime = 1099511628211;
        ulong hash = 14695981039346656037;
        var cells = row.Cells;
        for (var col = 0; col < cols && col < cells.Length; col++)
        {
            ref readonly var cell = ref cells[col];
            var text = cell.Text ?? "";
            for (var i = 0; i < text.Length; i++)
                hash = (hash ^ text[i]) * prime;
            hash = (hash ^ (ulong)(uint)text.Length) * prime;
            hash = (hash ^ (ulong)(cell.Wide ? 1u : 0u)) * prime;
            hash = (hash ^ (ulong)(cell.Tail ? 1u : 0u)) * prime;
            hash = (hash ^ (ulong)(byte)cell.Flags) * prime;
            hash = (hash ^ (ulong)(uint)cell.FgValue) * prime;
            hash = (hash ^ (ulong)(uint)cell.FgTag) * prime;
            hash = (hash ^ (ulong)(uint)cell.BgValue) * prime;
            hash = (hash ^ (ulong)(uint)cell.BgTag) * prime;
        }
        return hash;
    }

    /// <summary>
    /// Scans a row's cells for URLs (scheme:// and www. forms). The row text
    /// is assembled into a pooled char buffer with a parallel char→column
    /// map, so wide glyphs and empty cells keep regex offsets aligned with
    /// grid columns. Span URIs are stored as offsets into a per-row text
    /// snapshot: rows without links allocate nothing, rows with links
    /// allocate one string regardless of link count.
    /// </summary>
    private LinkSpan[] ComputeRowLinks(FrameRow row, int rowIndex)
    {
        var cells = row.Cells;
        var colCount = Math.Min(_cols, cells.Length);
        // Sized to the row's UTF-16 units, not a 2*cols guess: a cluster-dense
        // row overflowed the pooled buffer right here, before the URL gate
        // below could even decide the row had no links.
        var capacity = Math.Max(1, RowTextUnits(cells, _cols));
        if (_linkText.Length < capacity)
        {
            _linkText = new char[capacity];
            _linkCharToCol = new int[capacity];
        }

        var len = 0;
        for (var col = 0; col < colCount; col++)
        {
            ref readonly var cell = ref cells[col];
            if (cell.Tail)
                continue; // wide stub: the base cell owns the glyph
            var text = cell.Text ?? "";
            if (text.Length == 0)
            {
                _linkText[len] = ' ';
                _linkCharToCol[len] = col;
                len++;
            }
            else
            {
                for (var i = 0; i < text.Length; i++)
                {
                    _linkText[len] = text[i];
                    _linkCharToCol[len] = col;
                    len++;
                }
            }
        }

        var span = _linkText.AsSpan(0, len);

        // Gate: a URL must contain either ":" (scheme) or "www". Two
        // vectorized scans replace a full regex pass for the common
        // link-free row.
        if (span.IndexOf(':') < 0 && span.IndexOf("www", StringComparison.Ordinal) < 0)
        {
            _rowLinkTexts[rowIndex] = null;
            return Array.Empty<LinkSpan>();
        }

        var count = 0;
        foreach (var match in LinkPattern().EnumerateMatches(span))
        {
            var start = match.Index;
            var end = TrimLinkEnd(span, start, match.Index + match.Length);
            if (end - start < 3)
                continue;
            if (_linkSpans.Length <= count)
                Array.Resize(ref _linkSpans, Math.Max(8, _linkSpans.Length * 2));
            var lastCharCol = _linkCharToCol[end - 1];
            _linkSpans[count] = new LinkSpan
            {
                StartCol = _linkCharToCol[start],
                EndCol = Math.Min(colCount - 1, lastCharCol + (cells[lastCharCol].Wide ? 1 : 0)),
                TextStart = start,
                TextLength = end - start,
            };
            count++;
        }
        if (count == 0)
        {
            _rowLinkTexts[rowIndex] = null;
            return Array.Empty<LinkSpan>();
        }
        _rowLinkTexts[rowIndex] = new string(span[..len]);
        var result = new LinkSpan[count];
        Array.Copy(_linkSpans, result, count);
        return result;
    }

    /// <summary>Strips trailing punctuation from a URL match. Closing
    /// brackets are only stripped when unmatched inside the URL, so
    /// wikipedia-style "(foo)" links survive.</summary>
    private static int TrimLinkEnd(ReadOnlySpan<char> text, int start, int end)
    {
        while (end > start)
        {
            var c = text[end - 1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"')
            {
                end--;
                continue;
            }
            var open = c switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
            if (open != '\0' && !text[start..(end - 1)].Contains(open))
            {
                end--;
                continue;
            }
            break;
        }
        return end;
    }

    /// <summary>The URL under the given viewport cell, or null. Materializes
    /// the substring, so hot paths use <see cref="IsOverLink"/> instead.</summary>
    private string? LinkUriAt(int col, int row)
    {
        if (!TryFindLink(col, row, out var link, out var rowText) || rowText is null)
            return null;
        return rowText.Substring(link.TextStart, link.TextLength);
    }

    /// <summary>True when a detected URL covers the viewport cell. Called on
    /// every mouse move, so it must not allocate.</summary>
    private bool IsOverLink(int col, int row)
        => TryFindLink(col, row, out _, out _);

    private bool TryFindLink(int col, int row, out LinkSpan link, out string? rowText)
    {
        link = default;
        rowText = null;
        if (row < 0 || row >= _rowLinks.Length || _rowLinks[row] is not { Length: > 0 } links)
            return false;
        foreach (var span in links)
        {
            if (col >= span.StartCol && col <= span.EndCol)
            {
                link = span;
                rowText = _rowLinkTexts[row];
                return rowText is not null;
            }
        }
        return false;
    }

    private static void OpenLink(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Diag($"open-link-EXCEPTION {e.GetType().Name}: {e.Message}");
        }
    }

    private Typeface ResolveTypeface(CellFlags flags)
    {
        var bold = (flags & CellFlags.Bold) != 0;
        var italic = (flags & CellFlags.Italic) != 0;
        return (bold, italic) switch
        {
            (true, true) => _boldItalicTypeface,
            (true, false) => _boldTypeface,
            (false, true) => _italicTypeface,
            _ => _normalTypeface,
        };
    }

    private GlyphTypeface? ResolveGlyphTypeface(CellFlags flags)
    {
        var bold = (flags & CellFlags.Bold) != 0;
        var italic = (flags & CellFlags.Italic) != 0;
        if (bold && _boldGlyph is { } boldFace)
        {
            // Bold falls back to the regular face when the family lacks a
            // bold face (Cascadia Mono has one, but user-chosen families may
            // not); the caller then emulates bold via a thicker pen.
            if (!italic)
                return boldFace;
            return _boldItalicGlyph ?? boldFace;
        }
        if (italic)
            return _italicGlyph;
        return _normalGlyph;
    }

    private void DrawCaret()
    {
        var cursor = _terminal.Cursor;
        var hidden = (!_selfTestCaret && _session is null) || !cursor.Visible;
        var row = hidden ? -1 : cursor.Y;
        var col = hidden ? -1 : Math.Min(cursor.X, _cols - 1);
        var blinkOn = !_cursorBlinkSetting || !IsKeyboardFocused || _caretBlinkVisible;
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

    private bool _selectionVisualDrawn;

    private void DrawSelection()
    {
        var anyRowSelected = false;
        foreach (var frameRow in _terminal.FrameRows)
        {
            if (frameRow.HasSelection)
            {
                anyRowSelected = true;
                break;
            }
        }
        if (!_selectionActive && !anyRowSelected)
        {
            // Clear the overlay once when the selection deactivates; after
            // that, pumps with no selection skip the RenderOpen entirely.
            if (_selectionVisualDrawn)
            {
                using var clearDc = _selectionVisual.RenderOpen();
                _selectionVisualDrawn = false;
            }
            return;
        }

        _selectionVisualDrawn = true;
        using var dc = _selectionVisual.RenderOpen();

        for (var row = 0; row < _rows && row < _terminal.FrameRows.Length; row++)
        {
            var frameRow = _terminal.FrameRows[row];
            if (!frameRow.HasSelection)
                continue;

            var fromCol = frameRow.SelectionStart;
            var toCol = Math.Min(frameRow.SelectionEnd, _cols - 1);
            if (toCol < fromCol)
                continue;

            dc.DrawRectangle(_palette.Selection, null,
                new Rect(fromCol * _cellWidth, row * _cellHeight, (toCol - fromCol + 1) * _cellWidth, _cellHeight));
        }
    }

    // ---- Scrollbar --------------------------------------------------------

    private static Brush FrozenBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void RebuildScrollbarBrushes()
    {
        var fg = ((SolidColorBrush)_palette.Foreground).Color;
        _scrollbarThumbBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0x50, fg.R, fg.G, fg.B));
        _scrollbarThumbHoverBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0xA0, fg.R, fg.G, fg.B));
    }

    private void RebuildLinkPen()
    {
        var pen = new Pen(_palette.Link, 1);
        pen.Freeze();
        _linkPen = pen;
    }

    /// <summary>True while there is scrollback above the viewport to scroll into.</summary>
    private bool IsScrollbarVisible()
    {
        var scrollbar = _terminal.Scrollbar;
        return scrollbar.Total > scrollbar.Len && scrollbar.Len > 0;
    }

    private bool IsOverScrollbar(Point point)
        => ScrollbarOwnsPointer && IsScrollbarVisible() && point.X >= ActualWidth - ScrollbarHitWidth;

    /// <summary>Whether the overlay scrollbar may act on the pointer. It must
    /// stand down for an app that captured the mouse, otherwise a TUI loses
    /// clicks and motion in its rightmost columns; Shift overrides the app,
    /// matching xterm and Windows Terminal.</summary>
    private bool ScrollbarOwnsPointer
        => !_mouseTracking || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    /// <summary>
    /// Thumb geometry. The scrollable extent is the scrollback above the
    /// viewport plus the viewport itself; the viewport offset maps linearly
    /// onto the thumb's travel along the track.
    /// </summary>
    private bool TryGetScrollbarGeometry(out double thumbY, out double thumbH, out double travel)
    {
        thumbY = thumbH = travel = 0;
        if (!IsScrollbarVisible())
            return false;

        var scrollbar = _terminal.Scrollbar;
        var trackHeight = ActualHeight;
        thumbH = Math.Min(trackHeight, Math.Max(24, trackHeight * scrollbar.Len / Math.Max(1, scrollbar.Total)));
        travel = Math.Max(0, trackHeight - thumbH);
        var scrollable = scrollbar.Total - scrollbar.Len;
        thumbY = travel * scrollbar.Offset / Math.Max(1, scrollable);
        return true;
    }

    private void DrawScrollbar()
    {
        using var dc = _scrollbarVisual.RenderOpen();
        if (_scrollbarOpacity < 0.01 || !TryGetScrollbarGeometry(out var thumbY, out var thumbH, out _))
            return;

        var brush = _scrollbarHovered || _scrollbarDragging ? _scrollbarThumbHoverBrush : _scrollbarThumbBrush;
        var x = ActualWidth - _scrollbarWidth;
        var radius = _scrollbarWidth / 2;
        dc.PushOpacity(_scrollbarOpacity);
        dc.DrawRoundedRectangle(brush, null, new Rect(x, thumbY, _scrollbarWidth, thumbH), radius, radius);
        dc.Pop();
    }

    /// <summary>Shows the overlay scrollbar after user-driven scroll or hover,
    /// then schedules a fade-out unless the pointer is still on it.</summary>
    private void RevealScrollbar()
    {
        if (!IsScrollbarVisible())
        {
            SetScrollbarOpacity(0);
            _scrollbarHideTimer.Stop();
            return;
        }

        SetScrollbarOpacity(1);
        if (_scrollbarHovered || _scrollbarDragging)
            _scrollbarHideTimer.Stop();
        else
            ScheduleScrollbarHide();
    }

    private void ScheduleScrollbarHide()
    {
        _scrollbarHideTimer.Stop();
        _scrollbarHideTimer.Start();
    }

    private void SetScrollbarOpacity(double opacity)
    {
        if (Math.Abs(_scrollbarTargetOpacity - opacity) < 0.01)
            return;
        _scrollbarTargetOpacity = opacity;
        if (Math.Abs(_scrollbarTargetOpacity - _scrollbarOpacity) < 0.01)
            DrawScrollbar();
        else if (!_scrollbarAnimTimer.IsEnabled)
            _scrollbarAnimTimer.Start();
    }

    private void SetScrollbarHovered(bool hovered)
    {
        var targetWidth = hovered || _scrollbarDragging ? ScrollbarWideWidth : ScrollbarThinWidth;
        if (_scrollbarHovered == hovered && _scrollbarTargetWidth == targetWidth)
            return;
        _scrollbarHovered = hovered;
        Cursor = hovered ? Cursors.Arrow : Cursors.IBeam;
        _scrollbarTargetWidth = targetWidth;

        if (hovered && IsScrollbarVisible())
        {
            SetScrollbarOpacity(1);
            _scrollbarHideTimer.Stop();
        }
        else if (!_scrollbarDragging)
        {
            ScheduleScrollbarHide();
        }

        if (Math.Abs(_scrollbarTargetWidth - _scrollbarWidth) < 0.2
            && Math.Abs(_scrollbarTargetOpacity - _scrollbarOpacity) < 0.01)
            DrawScrollbar();
        else if (!_scrollbarAnimTimer.IsEnabled)
            _scrollbarAnimTimer.Start();
    }

    private void AnimateScrollbarVisual()
    {
        var widthDelta = _scrollbarTargetWidth - _scrollbarWidth;
        var opacityDelta = _scrollbarTargetOpacity - _scrollbarOpacity;
        var widthDone = Math.Abs(widthDelta) < 0.2;
        var opacityDone = Math.Abs(opacityDelta) < 0.01;

        if (widthDone)
            _scrollbarWidth = _scrollbarTargetWidth;
        else
            _scrollbarWidth += widthDelta * 0.3;

        if (opacityDone)
            _scrollbarOpacity = _scrollbarTargetOpacity;
        else
            _scrollbarOpacity += opacityDelta * 0.35;

        if (widthDone && opacityDone)
            _scrollbarAnimTimer.Stop();

        DrawScrollbar();
    }

    private void DragScrollbarThumb(Point pos)
    {
        if (!TryGetScrollbarGeometry(out _, out var thumbH, out var travel) || travel <= 0)
            return;
        var scrollbar = _terminal.Scrollbar;
        var scrollable = scrollbar.Total - scrollbar.Len;
        var desired = Math.Clamp(pos.Y - _scrollbarDragOffset, 0, travel);
        var offset = (ulong)Math.Round(desired * scrollable / travel);
        if (offset != scrollbar.Offset)
        {
            _terminal.ScrollToRow(offset);
            RevealScrollbar();
            FlushRedraw();
        }
    }

    // ---- Input ------------------------------------------------------------

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        StartSessionIfReady();
        UpdateBlinkTimer();
        DrawCaret();
        FocusGained?.Invoke();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_session is not null)
        {
            foreach (var (key, modifiers) in _terminalKeysDown)
            {
                if (TerminalKeyMap.TryMap(key, out var terminalKey))
                    SendTerminalKey(key, terminalKey, TerminalKeyAction.Release, modifiers, ReadOnlySpan<byte>.Empty);
            }
        }
        _terminalKeysDown.Clear();
        _pendingTextKey = null;
        UpdateBlinkTimer();
        DrawCaret();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // Modifier-only presses are the first half of chords like Ctrl+C used
        // to copy the selection; clearing here would tear the selection down
        // before the chord completes.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        // Word/line-wise keyboard selection owns the Ctrl+Shift+arrow chords
        // (Windows Terminal/Ghostty style): arrows step a whole word, Home/
        // End jump to the line edges. Selection outranks pane management, so
        // splitting moved to Ctrl+Shift+R / Ctrl+Shift+D below.
        if (!_terminal.IsAlternateScreen &&
            mods == (ModifierKeys.Control | ModifierKeys.Shift) &&
            key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            ExtendKeyboardSelection(key, byWord: true);
            e.Handled = true;
            return;
        }

        // Full-screen terminal applications own Ctrl+Shift chords too. They
        // use these combinations for navigation and command palettes just as
        // often as ordinary Ctrl chords.
        if (!_terminal.IsAlternateScreen && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            var command = key switch
            {
                Key.R => TerminalCommand.SplitRight,
                Key.D => TerminalCommand.SplitDown,
                Key.T => TerminalCommand.NewTab,
                Key.W => TerminalCommand.ClosePane,
                _ => (TerminalCommand?)null,
            };
            if (command is { } requested)
            {
                CommandRequested?.Invoke(requested);
                e.Handled = true;
                return;
            }
            if (key == Key.C)
            {
                CopySelection();
                e.Handled = true;
                return;
            }
            if (key == Key.V)
            {
                PasteClipboard();
                e.Handled = true;
                return;
            }
            if (key == Key.X)
            {
                CutSelection();
                e.Handled = true;
                return;
            }
        }

        // Ctrl+C copies when a selection exists, otherwise sends ETX.
        if (key == Key.C && mods == ModifierKeys.Control && (_selectionActive || _terminal.HasSelection))
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        // Keyboard selection (Shift+Arrow) drives the same selection gesture
        // the mouse uses, with a virtual caret independent of the shell
        // cursor. Only in the primary screen: full-screen apps own these keys.
        if (mods == ModifierKeys.Shift && !_terminal.IsAlternateScreen &&
            key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            ExtendKeyboardSelection(key, byWord: false);
            e.Handled = true;
            return;
        }

        ClearSelection();

        // Viewport scroll keybindings (the alternate screen has no scrollback).
        if (!_terminal.IsAlternateScreen)
        {
            if (mods == ModifierKeys.Control && key == Key.Home)
            {
                _terminal.ScrollToTop();
                RevealScrollbar();
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Control && key == Key.End)
            {
                _terminal.ScrollToBottom();
                RevealScrollbar();
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Shift && key == Key.PageUp)
            {
                _terminal.ScrollBy(-_rows);
                RevealScrollbar();
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Shift && key == Key.PageDown)
            {
                _terminal.ScrollBy(_rows);
                RevealScrollbar();
                FlushRedraw();
                e.Handled = true;
                return;
            }
        }

        if (!TerminalKeyMap.TryMap(key, out var terminalKey))
            return;

        var terminalModifiers = CurrentTerminalModifiers(mods);
        var action = e.IsRepeat ? TerminalKeyAction.Repeat : TerminalKeyAction.Press;
        var isTextKey = TerminalKeyMap.IsTextKey(key);
        var reportAllKeys = (_terminal.KittyKeyboardFlags & 0b01000) != 0;

        // WPF delivers layout/IME text after KeyDown. In Kitty's report-all
        // mode defer text-producing keys to TextInput so the native encoder
        // can attach the actual composed UTF-8 text instead of guessing from
        // a physical key on a potentially non-US layout.
        if (isTextKey && reportAllKeys &&
            (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            _pendingTextKey = new PendingTextKey(key, terminalKey, action, terminalModifiers);
            return;
        }

        // In legacy mode ordinary text still belongs to TextInput. Ctrl/Alt
        // chords are key events, however, because the encoder must apply the
        // terminal's disambiguation and meta-prefix modes.
        if (isTextKey && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            return;

        // Preserve Vex's shell-friendly Ctrl+Backspace behavior in legacy
        // mode. Kitty has an unambiguous Backspace+Ctrl representation and
        // must go through the native encoder instead.
        if (key == Key.Back && mods == ModifierKeys.Control && _terminal.KittyKeyboardFlags == 0)
        {
            ReadOnlySpan<byte> ctrlBackspace = stackalloc byte[] { 0x17 };
            _session.Write(ctrlBackspace);
            e.Handled = true;
            return;
        }

        if (SendTerminalKey(key, terminalKey, action, terminalModifiers, ReadOnlySpan<byte>.Empty))
            e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_session is null)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!_terminalKeysDown.Remove(key, out var modifiers) ||
            !TerminalKeyMap.TryMap(key, out var terminalKey))
            return;

        if (SendTerminalKey(key, terminalKey, TerminalKeyAction.Release, modifiers, ReadOnlySpan<byte>.Empty))
            e.Handled = true;
    }

    private TerminalKeyModifiers CurrentTerminalModifiers(ModifierKeys modifiers)
    {
        var result = TerminalKeyMap.MapModifiers(modifiers);
        if (Keyboard.IsKeyToggled(Key.CapsLock)) result |= TerminalKeyModifiers.CapsLock;
        if (Keyboard.IsKeyToggled(Key.NumLock)) result |= TerminalKeyModifiers.NumLock;
        if (Keyboard.IsKeyDown(Key.RightShift)) result |= TerminalKeyModifiers.RightShift;
        if (Keyboard.IsKeyDown(Key.RightCtrl)) result |= TerminalKeyModifiers.RightControl;
        if (Keyboard.IsKeyDown(Key.RightAlt)) result |= TerminalKeyModifiers.RightAlt;
        if (Keyboard.IsKeyDown(Key.RWin)) result |= TerminalKeyModifiers.RightSuper;
        return result;
    }

    private bool SendTerminalKey(Key wpfKey, TerminalKey key, TerminalKeyAction action,
        TerminalKeyModifiers modifiers, ReadOnlySpan<byte> utf8)
    {
        var session = _session;
        if (session is null)
            return false;

        Span<byte> output = stackalloc byte[512];
        var written = _terminal.EncodeKey(
            key, action, modifiers, utf8, TerminalKeyMap.UnshiftedCodepoint(wpfKey), output);
        if (written == 0)
            return false;

        session.Write(output[..written]);
        if (action is TerminalKeyAction.Press or TerminalKeyAction.Repeat)
            _terminalKeysDown[wpfKey] = modifiers;
        return true;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (DiagPath is not null)
            Diag($"text '{e.Text.Replace("\r", "<CR>")}' session={_session is not null}");
        if (_session is null || string.IsNullOrEmpty(e.Text))
            return;

        if (_pendingTextKey is { } pending)
        {
            _pendingTextKey = null;
            if (e.Text.Length <= 32)
            {
                Span<byte> text = stackalloc byte[Encoding.UTF8.GetMaxByteCount(e.Text.Length)];
                var textLength = Encoding.UTF8.GetBytes(e.Text, text);
                SendTerminalKey(pending.WpfKey, pending.Key, pending.Action, pending.Modifiers, text[..textLength]);
            }
            else
            {
                SendTerminalKey(pending.WpfKey, pending.Key, pending.Action, pending.Modifiers, Encoding.UTF8.GetBytes(e.Text));
            }
            e.Handled = true;
            return;
        }

        // Short single-line text (the overwhelmingly common case) encodes
        // into a stack buffer; the pooled fallback covers multi-line pastes
        // via IME. Avoids a byte[] allocation per keystroke.
        if (e.Text.Length <= 32 && e.Text.IndexOf('\n') < 0)
        {
            Span<byte> buffer = stackalloc byte[Encoding.UTF8.GetMaxByteCount(e.Text.Length)];
            var written = Encoding.UTF8.GetBytes(e.Text, buffer);
            _session.Write(buffer[..written]);
        }
        else
        {
            _session.Write(Encoding.UTF8.GetBytes(e.Text));
        }
        e.Handled = true;
    }

    private void CopySelection()
    {
        if (!_selectionActive && !_terminal.HasSelection)
            return;
            
        var unionRect = Rect.Empty;
        for (var row = 0; row < _rows && row < _terminal.FrameRows.Length; row++)
        {
            var frameRow = _terminal.FrameRows[row];
            if (!frameRow.HasSelection) continue;

            var fromCol = frameRow.SelectionStart;
            var toCol = Math.Min(frameRow.SelectionEnd, _cols - 1);
            if (toCol < fromCol) continue;

            var rect = new Rect(fromCol * _cellWidth, row * _cellHeight, (toCol - fromCol + 1) * _cellWidth, _cellHeight);
            unionRect.Union(rect);
        }

        var text = _terminal.GetSelectedText();
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            if (!unionRect.IsEmpty)
                StartCopyAnimation(unionRect);
        }
        // Copied text is deselected, matching the copy-then-clear convention.
        ClearSelection();
    }

    private void ClearSelection()
    {
        _kbSelectionActive = false;
        if (_selectionActive || _terminal.HasSelection)
        {
            _selectionActive = false;
            _terminal.ClearSelection();
            FlushRedraw();
        }
    }

    private DrawingVisual? _copyAnimVisual;
    private TimeSpan? _copyAnimStartTime;
    private Rect _copyAnimRect;

    private void StartCopyAnimation(Rect rect)
    {
        if (_copyAnimVisual == null)
        {
            _copyAnimVisual = new DrawingVisual();
            _children.Add(_copyAnimVisual);
        }

        _copyAnimRect = rect;
        _copyAnimStartTime = TimeSpan.Zero;
        CompositionTarget.Rendering -= OnCopyAnimFrame;
        CompositionTarget.Rendering += OnCopyAnimFrame;
    }

    private void OnCopyAnimFrame(object? sender, EventArgs e)
    {
        if (_copyAnimVisual == null) return;

        var renderingEventArgs = (RenderingEventArgs)e;
        if (_copyAnimStartTime == TimeSpan.Zero)
        {
            _copyAnimStartTime = renderingEventArgs.RenderingTime;
        }

        var elapsed = _copyAnimStartTime.HasValue 
            ? (renderingEventArgs.RenderingTime - _copyAnimStartTime.Value).TotalMilliseconds 
            : 0;
            
        var duration = 1200.0;
        
        if (elapsed >= duration)
        {
            CompositionTarget.Rendering -= OnCopyAnimFrame;
            using (var dc = _copyAnimVisual!.RenderOpen()) { } // clear
            return;
        }

        var t = elapsed / duration;
        var opacity = t < 0.6 ? 1.0 : 1.0 - (t - 0.6) / 0.4;
        
        var popEaseOut = 1 - Math.Pow(1 - Math.Min(1.0, t * 5), 4);
        var yOffset = 16 * (1 - popEaseOut); 
        
        var flashOpacity = 1.0 - Math.Min(1.0, t * 4); // Fades out in first 25% of animation
        
        using (var dc = _copyAnimVisual!.RenderOpen())
        {
            dc.PushOpacity(opacity);

            var fgColor = ((SolidColorBrush)_palette.Foreground).Color;
            var glowAlpha = (byte)(0x40 + 0x80 * flashOpacity);
            var glowBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(glowAlpha, fgColor.R, fgColor.G, fgColor.B));
            var framePen = new Pen(_palette.Foreground, 1.5 + 1.5 * flashOpacity);
            var glowPen = new Pen(glowBrush, 5.0 + 8.0 * flashOpacity);

            dc.DrawRectangle(null, glowPen, _copyAnimRect);
            dc.DrawRectangle(null, framePen, _copyAnimRect);

            var textBrush = _palette.Background;
            var bgBrush = _palette.Foreground;
            
            var text = new FormattedText("Copied", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 
                12, textBrush, _pixelsPerDip);
                
            var toastWidth = text.Width + 24;
            var toastHeight = text.Height + 12;
            
            var paddingX = 16.0;
            if (_scrollbarOpacity > 0.2)
                paddingX += _scrollbarWidth;
            
            var x = ActualWidth - paddingX - toastWidth;
            var y = 16.0 - yOffset;
            
            var rectToast = new Rect(x, y, toastWidth, toastHeight);
            
            dc.DrawRoundedRectangle(bgBrush, null, rectToast, 6, 6);
            dc.DrawText(text, new Point(rectToast.X + 12, rectToast.Y + 6));
            
            dc.Pop();
        }
    }

    /// <summary>
    /// Moves the keyboard-selection caret and re-drives the selection
    /// gesture. Every key replays the full press-drag-release sequence:
    /// ghostty only commits the selection snapshot on release, so a bare
    /// press+drag (no pointer button ever releases here) leaves nothing
    /// selectable. The caret wraps at line boundaries but stays inside the
    /// viewport; <paramref name="byWord"/> steps a whole word (left) or is
    /// reserved for the Home/End line jumps.
    /// </summary>
    private void ExtendKeyboardSelection(Key key, bool byWord)
    {
        if (!_kbSelectionActive)
        {
            var cursor = _terminal.Cursor;
            _kbAnchorCol = Math.Clamp(cursor.X, 0, _cols - 1);
            _kbAnchorRow = Math.Clamp(cursor.Y, 0, _rows - 1);
            _kbFocusCol = _kbAnchorCol;
            _kbFocusRow = _kbAnchorRow;
            _kbSelectionActive = true;
        }

        switch (key)
        {
            case Key.Left:
                if (byWord) _kbFocusCol = WordBoundaryCol(_kbFocusRow, _kbFocusCol, forward: false);
                else if (_kbFocusCol > 0) _kbFocusCol--;
                else if (_kbFocusRow > 0) { _kbFocusRow--; _kbFocusCol = _cols - 1; }
                break;
            case Key.Right:
                if (byWord) _kbFocusCol = WordBoundaryCol(_kbFocusRow, _kbFocusCol, forward: true);
                else if (_kbFocusCol < _cols - 1) _kbFocusCol++;
                else if (_kbFocusRow < _rows - 1) { _kbFocusRow++; _kbFocusCol = 0; }
                break;
            case Key.Up:
                if (_kbFocusRow > 0) _kbFocusRow--;
                break;
            case Key.Down:
                if (_kbFocusRow < _rows - 1) _kbFocusRow++;
                break;
            case Key.Home:
                _kbFocusCol = 0;
                break;
            case Key.End:
                _kbFocusCol = _cols - 1;
                break;
        }

        var pressX = _kbAnchorCol * _cellWidth + _cellWidth * 0.2;
        var rowY = _kbAnchorRow * _cellHeight + _cellHeight * 0.5;
        var focusX = _kbFocusCol * _cellWidth + _cellWidth * 0.8;
        var focusY = _kbFocusRow * _cellHeight + _cellHeight * 0.5;
        var nativePress = NativePoint(new Point(pressX, rowY));
        var nativeFocus = NativePoint(new Point(focusX, focusY));
        _terminal.SelectionPress(_kbAnchorCol, _kbAnchorRow, nativePress.X, nativePress.Y);
        _terminal.SelectionDrag(_kbFocusCol, _kbFocusRow, nativeFocus.X, nativeFocus.Y);
        _terminal.SelectionRelease(_kbFocusCol, _kbFocusRow);
        FlushRedraw();
    }

    /// <summary>
    /// Nearest word boundary at or next to <paramref name="col"/> in the
    /// viewport row. Backward skips separators then the word's characters, so
    /// a caret inside "foo|bar" lands on "foo" first and "bar" on the next
    /// press — the readline word-left behavior; forward is the mirror image.
    /// </summary>
    private int WordBoundaryCol(int row, int col, bool forward)
    {
        var frameRows = _terminal.FrameRows;
        if ((uint)row >= (uint)frameRows.Length)
            return col;
        var cells = frameRows[row].Cells;
        var last = Math.Max(0, cells.Length - 1);

        static bool IsSeparator(in CellInfo cell)
        {
            var text = cell.Text.TrimEnd('\0');
            return text.Length == 0 || char.IsWhiteSpace(text[0]);
        }

        if ((uint)col >= (uint)cells.Length)
            return last;
        if (forward)
        {
            while (col < last && IsSeparator(cells[col]))
                col++;
            while (col < last && !IsSeparator(cells[col + 1]))
                col++;
            return col;
        }
        while (col > 0 && IsSeparator(cells[col]))
            col--;
        while (col > 0 && !IsSeparator(cells[col - 1]))
            col--;
        return col;
    }

    private void PasteClipboard()
    {
        if (_session is null || !Clipboard.ContainsText())
            return;
        var text = Clipboard.GetText().Replace("\r\n", "\r").Replace("\n", "\r");
        if (_terminal.BracketedPaste)
            text = "\x1b[200~" + text + "\x1b[201~";
        _session.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Copies the selection and, when it is single-line input ending exactly
    /// at the shell cursor (a leftward keyboard selection from the prompt),
    /// deletes it with backspaces — the only text a terminal can truly "cut"
    /// is unsubmitted line input.
    /// </summary>
    private void CutSelection()
    {
        if (!_selectionActive && !_terminal.HasSelection)
            return;
        var text = _terminal.GetSelectedText();
        if (string.IsNullOrEmpty(text))
            return;
        Clipboard.SetText(text);

        var cursor = _terminal.Cursor;
        var nearestCursorCol = _kbFocusRow == _kbAnchorRow
            ? Math.Max(_kbAnchorCol, _kbFocusCol)
            : -1;
        var deletable = _kbSelectionActive
            && _kbFocusRow == _kbAnchorRow
            && _kbFocusRow == cursor.Y
            && nearestCursorCol == cursor.X;
        ClearSelection();

        if (deletable && _session is not null)
        {
            var backspaces = new byte[text.Length];
            Array.Fill(backspaces, (byte)0x7f);
            _session.Write(backspaces);
        }
    }

    // ---- Mouse ------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_session is null)
            return;

        var pos = e.GetPosition(this);

        // The scrollbar overlays the right edge. It yields to an app that
        // captured the mouse (Shift still reaches it) so a TUI never loses
        // clicks in its rightmost columns.
        if (IsOverScrollbar(pos) && TryGetScrollbarGeometry(out var thumbY, out var thumbH, out _))
        {
            if (pos.Y >= thumbY && pos.Y <= thumbY + thumbH)
            {
                _scrollbarDragging = true;
                _scrollbarDragOffset = pos.Y - thumbY;
                _scrollbarWidth = _scrollbarTargetWidth = ScrollbarWideWidth;
                CaptureMouse();
                RevealScrollbar();
                DrawScrollbar();
            }
            else
            {
                _terminal.ScrollBy(pos.Y < thumbY ? -_rows : _rows);
                RevealScrollbar();
                FlushRedraw();
            }
            e.Handled = true;
            return;
        }

        var (col, row) = CellFromPoint(pos);

        // Ctrl+click on a detected URL opens it in the browser instead of
        // selecting or forwarding the click to the app's mouse mode.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && LinkUriAt(col, row) is { } linkUri)
        {
            OpenLink(linkUri);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _mouseTracking)
        {
            // Shift overrides app mouse capture, as in xterm; it starts a
            // fresh selection at the pointer instead of extending.
            _selectionActive = true;
            _selectionDragged = false;
            _selectionGestureActive = true;
            _mouseSelectionOverride = true;
            var nativePos = NativePoint(pos);
            _selectionClickCount = _terminal.SelectionPress(col, row, nativePos.X, nativePos.Y);
            CaptureMouse();
            FlushRedraw();
            e.Handled = true;
            return;
        }

        // Applications that capture the mouse get their events instead of
        // selection.
        if (_mouseTracking)
        {
            SetReportedButton(MouseInputButton.Left, true);
            SendMouse(MouseInputAction.Press, MouseInputButton.Left, pos);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        _selectionActive = true;
        _selectionDragged = false;
        _selectionGestureActive = true;
        var selectionPos = NativePoint(pos);
        _selectionClickCount = _terminal.SelectionPress(col, row, selectionPos.X, selectionPos.Y);
        CaptureMouse();
        FlushRedraw();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var pos = e.GetPosition(this);

        if (_scrollbarDragging)
        {
            DragScrollbarThumb(pos);
            e.Handled = true;
            return;
        }

        var overScrollbar = IsOverScrollbar(pos);
        SetScrollbarHovered(overScrollbar);

        // Hand cursor over detected links, except while dragging a selection
        // or the scrollbar thumb (where the pointer means something else).
        if (!_scrollbarDragging && !overScrollbar && e.LeftButton != MouseButtonState.Pressed)
        {
            var (hoverCol, hoverRow) = CellFromPoint(pos);
            Cursor = IsOverLink(hoverCol, hoverRow) && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                ? Cursors.Hand
                : _mouseTracking && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                    ? Cursors.Arrow
                    : Cursors.IBeam;
        }

        // The overlay scrollbar owns unbuttoned hover at the right edge. A
        // drag that started in the terminal remains captured by the TUI.
        if (overScrollbar && _reportedMouseButtons == 0 && !_mouseSelectionOverride)
        {
            e.Handled = true;
            return;
        }

        if (_mouseTracking && !_mouseSelectionOverride)
        {
            SendMouse(MouseInputAction.Motion, PressedButton(e), pos);
            e.Handled = true;
            return;
        }
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed && _selectionActive)
        {
            var (col, row) = CellFromPoint(pos);
            _selectionDragged = true;
            var nativePos = NativePoint(pos);
            _terminal.SelectionDrag(col, row, nativePos.X, nativePos.Y);
            FlushRedraw();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_scrollbarDragging)
        {
            _scrollbarDragging = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            SetScrollbarHovered(IsOverScrollbar(e.GetPosition(this)));
            DrawScrollbar();
            e.Handled = true;
            return;
        }

        if (_mouseSelectionOverride)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            _selectionGestureActive = false;
            _terminal.SelectionRelease(col, row);

            bool madeSelection = _selectionDragged || _selectionClickCount > 1;
            if (!madeSelection)
                FinishClickSelection();
            else
                CopySelection();

            _selectionDragged = false;
            _mouseSelectionOverride = false;
        }
        else if (_mouseTracking || IsReportedButton(MouseInputButton.Left))
        {
            var pos = e.GetPosition(this);
            SetReportedButton(MouseInputButton.Left, false);
            SendMouse(MouseInputAction.Release, MouseInputButton.Left, pos);
            e.Handled = true;
        }
        else if (_selectionActive)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            _selectionGestureActive = false;
            _terminal.SelectionRelease(col, row);

            bool madeSelection = _selectionDragged || _selectionClickCount > 1;
            if (!madeSelection)
                FinishClickSelection();
            else
                CopySelection();

            _selectionDragged = false;
        }
        ReleaseMouseIfNoButtons();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        var position = Mouse.GetPosition(this);
        if (_selectionGestureActive)
        {
            var (col, row) = CellFromPoint(position);
            var wasDragged = _selectionDragged;
            var clickCount = _selectionClickCount;
            _selectionGestureActive = false;
            _terminal.SelectionRelease(col, row);
            _mouseSelectionOverride = false;
            _selectionDragged = false;

            bool madeSelection = wasDragged || clickCount > 1;
            if (!madeSelection)
                FinishClickSelection();
            else
                CopySelection();
        }

        _scrollbarDragging = false;
        SetScrollbarHovered(false);
        foreach (var button in new[] { MouseInputButton.Left, MouseInputButton.Middle, MouseInputButton.Right })
        {
            if (!IsReportedButton(button))
                continue;
            SetReportedButton(button, false);
            SendMouse(MouseInputAction.Release, button, position);
        }
        DrawScrollbar();
    }

    private void FinishClickSelection()
    {
        if (_selectionClickCount > 1)
        {
            FlushRedraw();
            return;
        }

        _selectionActive = false;
        // Keep Ghostty's click history so the next nearby press can become a
        // word or line selection, while a lone click leaves no selected cell.
        _terminal.ClearSelection(resetGesture: false);
        FlushRedraw();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        if (_mouseTracking)
        {
            var pos = e.GetPosition(this);
            SetReportedButton(MouseInputButton.Right, true);
            SendMouse(MouseInputAction.Press, MouseInputButton.Right, pos);
            CaptureMouse();
            e.Handled = true;
            return;
        }
        PasteClipboard();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (!_mouseTracking && !IsReportedButton(MouseInputButton.Right))
            return;
        SetReportedButton(MouseInputButton.Right, false);
        SendMouse(MouseInputAction.Release, MouseInputButton.Right, e.GetPosition(this));
        ReleaseMouseIfNoButtons();
        e.Handled = true;
    }

    private void OnTerminalMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        Focus();
        if (!_mouseTracking)
            return;
        SetReportedButton(MouseInputButton.Middle, true);
        SendMouse(MouseInputAction.Press, MouseInputButton.Middle, e.GetPosition(this));
        CaptureMouse();
        e.Handled = true;
    }

    private void OnTerminalMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        if (!_mouseTracking && !IsReportedButton(MouseInputButton.Middle))
            return;
        SetReportedButton(MouseInputButton.Middle, false);
        SendMouse(MouseInputAction.Release, MouseInputButton.Middle, e.GetPosition(this));
        ReleaseMouseIfNoButtons();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetScrollbarHovered(false);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        var steps = ConsumeWheelSteps(e.Delta);

        // Shift always addresses Vex's own scrollback, matching xterm and
        // Windows Terminal. Without the override an app that captured the
        // mouse owns the wheel outright, leaving no way to scroll back into
        // output the app has already scrolled past.
        var shiftScrollsLocally = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_mouseTracking && !shiftScrollsLocally)
        {
            var button = steps > 0 ? MouseInputButton.WheelUp : MouseInputButton.WheelDown;
            for (var i = 0; i < Math.Abs(steps); i++)
            {
                SendMouse(MouseInputAction.Press, button, e.GetPosition(this));
                SendMouse(MouseInputAction.Release, button, e.GetPosition(this));
            }
            e.Handled = true;
            return;
        }

        if (_terminal.IsAlternateScreen)
            return; // viewport scrollback does not exist on the alt screen

        // The system setting uses -1 for "scroll a page at a time".
        var configured = SystemParameters.WheelScrollLines;
        var lines = (configured < 0 ? Math.Max(1, _rows - 1) : Math.Max(1, configured)) * steps;
        if (lines != 0)
        {
            _terminal.ScrollBy(-lines);
            RevealScrollbar();
            FlushRedraw();
        }
        e.Handled = true;
    }

    private readonly byte[] _mouseReport = new byte[128];
    private int _reportedMouseButtons;
    private int _wheelDeltaRemainder;

    private int ConsumeWheelSteps(int delta)
    {
        _wheelDeltaRemainder += delta;
        var steps = _wheelDeltaRemainder / 120;
        _wheelDeltaRemainder %= 120;
        return steps;
    }

    private void SendMouse(MouseInputAction action, MouseInputButton? button, Point position)
    {
        var keyboard = Keyboard.Modifiers;
        var modifiers = MouseInputModifiers.None;
        if (keyboard.HasFlag(ModifierKeys.Shift)) modifiers |= MouseInputModifiers.Shift;
        if (keyboard.HasFlag(ModifierKeys.Control)) modifiers |= MouseInputModifiers.Control;
        if (keyboard.HasFlag(ModifierKeys.Alt)) modifiers |= MouseInputModifiers.Alt;

        // The native encoder takes integer geometry while WPF renders in
        // fractional DIPs. Scale into the same coordinate space supplied to
        // ghostty_terminal_resize so cell-edge clicks cannot drift a column.
        var nativePosition = NativePoint(position);
        var len = _terminal.EncodeMouse(action, button, modifiers,
            nativePosition.X, nativePosition.Y, _reportedMouseButtons != 0, _mouseReport);
        if (len > 0)
            _session?.Write(_mouseReport.AsSpan(0, len));
    }

    private void SetReportedButton(MouseInputButton button, bool pressed)
    {
        var bit = 1 << (int)button;
        if (pressed)
            _reportedMouseButtons |= bit;
        else
            _reportedMouseButtons &= ~bit;
    }

    private bool IsReportedButton(MouseInputButton button)
        => (_reportedMouseButtons & (1 << (int)button)) != 0;

    private MouseInputButton? PressedButton(MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) return MouseInputButton.Left;
        if (e.MiddleButton == MouseButtonState.Pressed) return MouseInputButton.Middle;
        if (e.RightButton == MouseButtonState.Pressed) return MouseInputButton.Right;
        return null;
    }

    private void ReleaseMouseIfNoButtons()
    {
        if (_reportedMouseButtons == 0 && IsMouseCaptured)
            ReleaseMouseCapture();
    }

    private Point NativePoint(Point point)
        => new(point.X * _nativeCellWidth / _cellWidth,
            point.Y * _nativeCellHeight / _cellHeight);

    private (int Col, int Row) CellFromPoint(Point point)
    {
        var col = Math.Clamp((int)(point.X / _cellWidth), 0, Math.Max(0, _cols - 1));
        var row = Math.Clamp((int)(point.Y / _cellHeight), 0, Math.Max(0, _rows - 1));
        return (col, row);
    }

    // ---- Dispose ------------------------------------------------------------

    private void OnResizeSuspensionEnded()
    {
        if (_disposed)
            return;
        if (Dispatcher.CheckAccess())
            RecalculateGridSize();
        else
            _ = Dispatcher.BeginInvoke(RecalculateGridSize);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        // Publish first: the reader thread checks this under the terminal
        // lock and stops feeding, so the emulator below cannot be freed
        // mid-feed.
        _disposed = true;
        _blinkTimer.Stop();
        _scrollbarAnimTimer.Stop();
        _scrollbarHideTimer.Stop();
        if (_resizeSettleTimer is { } settle)
        {
            settle.Stop();
            settle.Tick -= OnResizeSettled;
        }
        AppSettings.Instance.PropertyChanged -= OnSettingsChanged;
        ResizeSuspensionEnded -= OnResizeSuspensionEnded;
        _prewarmLease?.Dispose();
        _prewarmLease = null;
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.OutputReceived -= OnSessionOutput;
            session.Exited -= OnSessionExited;
            session.Dispose();
        }
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
