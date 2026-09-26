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

    // Last viewport offset seen after UpdateFrame; a change means the
    // viewport moved (wheel, PgUp/Dn, autoscroll) and every visible row
    // maps to different content.
    private ulong _lastScrollOffset;
    private bool _resizeScheduled;

    // After a burst of size changes (window drag, sidebar), one settle pass
    // re-syncs ConPTY and forces a full cell read so wrapped lines do not
    // stay broken if an intermediate resize failed or raced the paint.
    private DispatcherTimer? _resizeSettleTimer;
    private short _pendingSessionCols;
    private short _pendingSessionRows;
    private bool _sessionResizePending;


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
            _scrollbarVisual,
            _caretVisual,
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
                    _session = prewarmed;
                    prewarmed.Exited += OnSessionExited;
                    prewarmLease.AttachOutputHandler(OnSessionOutput);
                    prewarmLease.Dispose();
                    _prewarmLease = null;
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
                    _session = null;
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
                _session = null;
                session.Dispose();
                _ = Dispatcher.BeginInvoke(() => _sessionStarting = false);
                return;
            }
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
        _session = session;
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


    // ---- Scrollbar --------------------------------------------------------

    private static Brush FrozenBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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
