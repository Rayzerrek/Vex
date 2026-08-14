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
public sealed class NativeTerminalControl : FrameworkElement, ITerminalView
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
    private TerminalSession? _session;
    private TerminalPalette _palette = new(BuiltInThemes.VexDark);
    private FontFamily _fontFamily = new("Cascadia Mono");
    private double _fontSize = 13;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _pixelsPerDip = 1.0;
    private Typeface _normalTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private Typeface _boldTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private Typeface _italicTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private Typeface _boldItalicTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
    private int _cols;
    private int _rows;
    private bool _needsFullRedraw = true;
    private bool _disposed;

    // Per-row render caches: the row's last-painted content hash plus the
    // render version it was painted with. RedrawRow skips the DrawingVisual
    // pass when both match, so redundant full redraws cost only the hash
    // pass. A version bump (font/DPI/palette change) invalidates every row
    // at once. The cache is index-aligned with _rowVisuals: rows are never
    // shifted, only re-rendered in place.
    private int _renderVersion = 1;
    private int[] _rowHashes = Array.Empty<int>();
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
    private double _baselineY;

    private readonly object _outputLock = new();
    private List<ArraySegment<byte>> _pendingOutput = new();
    private List<ArraySegment<byte>> _processingOutput = new();
    private bool _pumpScheduled;
    private bool _pumpRunning;

    private bool _caretBlinkVisible = true;
    private bool _selfTestCaret;
    private bool _cursorBlinkSetting = true;
    private bool _selectionActive;
    private bool _selectionDragged;

    private static readonly string? DiagPath = Environment.GetEnvironmentVariable("VEX_DIAG");
    internal static void Diag(string message)
    {
        if (DiagPath is null)
            return;
        try { System.IO.File.AppendAllText(DiagPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n"); }
        catch { }
    }

    // Scrollbar overlay state. The bar hugs the right edge: thin at rest,
    // widening on hover/drag (animated by _scrollbarAnimTimer). It only draws
    // while the buffer has scrollback above the viewport.
    private const double ScrollbarThinWidth = 6;
    private const double ScrollbarWideWidth = 12;
    private const double ScrollbarHitWidth = 16;
    private double _scrollbarWidth = ScrollbarThinWidth;
    private double _scrollbarTargetWidth = ScrollbarThinWidth;
    private bool _scrollbarHovered;
    private bool _scrollbarDragging;
    private double _scrollbarDragOffset;
    private Brush _scrollbarThumbBrush = Brushes.Gray;
    private Brush _scrollbarThumbHoverBrush = Brushes.LightGray;

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
    public event Action? FocusGained;
    public event Action<TerminalCommand>? CommandRequested;

    public NativeTerminalControl(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        Focusable = true;
        Cursor = Cursors.IBeam;
        // Grayscale antialiasing so glyphs blend against the translucent
        // surface; ClearType subpixel AA fringes when a run has no solid
        // background behind it.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        _terminal = new GhosttyTerminal(80, 24);
        _terminal.TitleChanged += title =>
        {
            TitleRawChanged?.Invoke(title);
            TitleChanged?.Invoke(title);
        };
        _terminal.WritePty += data => _session?.Write(data);

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
        _scrollbarAnimTimer.Tick += (_, _) => AnimateScrollbarWidth();

        ApplySettings();
        AppSettings.Instance.PropertyChanged += OnSettingsChanged;

        RenderSelfTest.Run(this);

        Loaded += (_, _) =>
        {
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            RebuildFontMetrics();
        };
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
                var theme = BuiltInThemes.All.FirstOrDefault(t => t.Name == settings.ThemeName) ?? BuiltInThemes.VexDark;
                _palette = PaletteFor(theme);
                ApplyTerminalColors();
                RebuildScrollbarBrushes();
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

    private static TerminalPalette PaletteFor(TerminalTheme theme)
    {
        // All panes share one palette per theme. A TerminalPalette builds 256
        // frozen brushes, so constructing one per pane per settings change
        // multiplied every slider tick by the pane count.
        if (!PaletteCache.TryGetValue(theme.Name, out var palette))
            PaletteCache[theme.Name] = palette = new TerminalPalette(theme);
        return palette;
    }

    private static TypefaceSet TypefacesFor(string familySource)
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
        var theme = BuiltInThemes.All.FirstOrDefault(t => t.Name == settings.ThemeName) ?? BuiltInThemes.VexDark;
        _palette = PaletteFor(theme);
        ApplyTerminalColors();
        RebuildScrollbarBrushes();
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

        var typeface = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, _fontSize, Brushes.White, _pixelsPerDip);
        _baselineY = probe.Baseline;

        if (typeface.TryGetGlyphTypeface(out var glyph))
        {
            var em = _fontSize;
            var map = glyph.CharacterToGlyphMap;
            var advance = map.TryGetValue('M', out var mGlyph) ? glyph.AdvanceWidths[mGlyph] : glyph.AdvanceWidths[0];
            _cellWidth = Math.Max(1, advance * em);
            _cellHeight = Math.Max(1, Math.Ceiling(em * _fontFamily.LineSpacing));
        }
        else
        {
            _cellWidth = Math.Max(1, probe.WidthIncludingTrailingWhitespace);
            _cellHeight = Math.Max(1, Math.Ceiling(probe.Height));
        }

        RecalculateGridSize();
    }

    private void RecalculateGridSize()
    {
        if (!IsLoaded || ActualWidth < 150 || ActualHeight < 80)
            return;

        var cols = Math.Max(2, (int)(ActualWidth / _cellWidth));
        var rows = Math.Max(1, (int)(ActualHeight / _cellHeight));
        if (cols == _cols && rows == _rows && _session is not null)
            return;

        _cols = cols;
        _rows = rows;
        Diag($"grid {cols}x{rows} session={_session is not null}");
        EnsureRowVisuals();
        _terminal.Resize(cols, rows, (int)_cellWidth, (int)_cellHeight);
        // After resize, snap viewport to bottom so shells like nushell
        // don't appear scrolled up due to buffer reflow.
        _terminal.ScrollToBottom();
        _needsFullRedraw = true;
        // UpdateFrame must run before the repaint so FrameRows reflects the
        // new geometry; RedrawAll against the stale frame reads out of bounds
        // when the grid grows (e.g. maximizing the window).
        FlushRedraw();

        if (_session is null)
        {
            // Spawn the shell only once the layout settles at a sane geometry
            // so prompt redraw logic in shells does not thrash on startup sizes.
            // Self-test runs feed the emulator directly instead.
            if (cols >= 20 && rows >= 5 && RenderSelfTest.ReportPath is null)
            {
                StartSession();
                _terminal.ScrollToBottom();
            }
        }
        else
        {
            _session.Resize((short)cols, (short)rows);
        }
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
            _rowHashes = new int[_rows];
            _rowVersions = new int[_rows];
        }
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
        // cell size does not, so the cached row pixels are stale.
        _renderVersion++;
        _needsFullRedraw = true;
        RedrawAll();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_palette.Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    // ---- Session ----------------------------------------------------------

    /// <summary>Shell override for live self-test scenarios; null uses the
    /// configured default.</summary>
    internal string? SelfTestShell;

    private void StartSession()
    {
        if (_session is not null)
            return;

        var session = new TerminalSession();
        session.OutputReceived += OnSessionOutput;
        session.Exited += OnSessionExited;
        var shell = SelfTestShell ?? AppSettings.Instance.Shell switch
        {
            "Nushell" => "nu.exe",
            "PowerShell" => TerminalSession.PowerShell(),
            _ => TerminalSession.DefaultShell()
        };
        session.Start(_workingDirectory, (short)_cols, (short)_rows, shell);
        _session = session;
    }

    /// <summary>PID of the ConPTY shell process; null before the session starts.</summary>
    public int? ProcessId => _session?.ProcessId;

    private void OnSessionOutput(ArraySegment<byte> chunk)
    {
        if (DiagPath is not null && chunk.Array is { } diagBuffer)
            Diag($"chunk {Convert.ToBase64String(diagBuffer, chunk.Offset, chunk.Count)}");
        lock (_outputLock)
        {
            if (_pendingOutput.Count < 10000)
                _pendingOutput.Add(chunk);
            else if (chunk.Array is { } droppedBuffer)
                System.Buffers.ArrayPool<byte>.Shared.Return(droppedBuffer);
            if (_pumpScheduled)
                return;
            _pumpScheduled = true;
        }

        ScheduleOutputPump();
    }

    private void ScheduleOutputPump()
    {
        // Defer to the next idle frame (below Render priority) and coalesce
        // all chunks that arrive during that frame into a single feed batch.
        // This bounds emulation cost to one pass per rendered frame and keeps
        // typing/scroll responsive even under heavy output.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_pumpRunning)
                return; // a pump is already draining; it will re-queue if needed
            _pumpRunning = true;
            try
            {
                PumpOutput();
            }
            finally
            {
                _pumpRunning = false;
            }
        });
    }

    private void PumpOutput()
    {
        List<ArraySegment<byte>> toProcess;
        lock (_outputLock)
        {
            toProcess = _pendingOutput;
            _pendingOutput = _processingOutput;
            _processingOutput = toProcess;
        }

        if (_disposed)
        {
            foreach (var c in toProcess)
            {
                if (c.Array is { } buffer)
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
            toProcess.Clear();
            lock (_outputLock)
            {
                _pumpScheduled = false;
            }
            return;
        }

        foreach (var c in toProcess)
        {
            if (c.Array is not { } buffer)
                continue;
            try
            {
                _terminal.Feed(buffer, c.Offset, c.Count);
            }
            catch (Exception e)
            {
                // Guard against malformed input states. Keep draining the
                // batch so every pooled buffer is returned and schedule a
                // full redraw.
                var tail = Convert.ToHexString(buffer, Math.Max(0, c.Count - 24), Math.Min(24, c.Count));
                Diag($"feed-EXCEPTION {e.GetType().Name}: {e.Message} chunk={c.Count} tail={tail}");
                _needsFullRedraw = true;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        toProcess.Clear();
        FlushRedraw();

        var scheduleAgain = false;
        lock (_outputLock)
        {
            if (_pendingOutput.Count > 0)
                scheduleAgain = true;
            else
                _pumpScheduled = false;
        }
        if (scheduleAgain)
            ScheduleOutputPump();
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

    // ---- Self-test hooks (see RenderSelfTest) -----------------------------

    internal int SelfTestCols => _cols;
    internal int SelfTestRows => _rows;
    internal double SelfTestCellWidth => _cellWidth;
    internal double SelfTestCellHeight => _cellHeight;

    /// <summary>The visible frame row for a viewport row, or null.</summary>
    internal FrameRow? SelfTestLine(int row)
    {
        if (row < 0 || row >= _terminal.FrameRows.Length)
            return null;
        return _terminal.FrameRows[row];
    }

    /// <summary>Viewport cell currently occupied by the drawn caret, or null.</summary>
    internal (int Row, int Col)? SelfTestCaretCell()
    {
        var cursor = _terminal.Cursor;
        if (!cursor.Visible || cursor.Y < 0 || cursor.Y >= _rows)
            return null;
        return (cursor.Y, Math.Min(cursor.X, _cols - 1));
    }

    /// <summary>Colors a cell renders with: the resolved background brush
    /// color, or the theme background when the cell carries no background
    /// (the row leaves the base layer visible).</summary>
    internal (System.Windows.Media.Color? Background, System.Windows.Media.Color Base) SelfTestResolveCell(in CellInfo cell)
    {
        _palette.Resolve(cell.FgTag, cell.FgValue, cell.BgTag, cell.BgValue, cell.Flags, out _, out var bg);
        var baseColor = _palette.Background is SolidColorBrush baseBrush ? baseBrush.Color : default;
        return (bg is SolidColorBrush brush ? brush.Color : (System.Windows.Media.Color?)null, baseColor);
    }

    internal void SelfTestFeed(string text)
    {
        _terminal.Feed(text);
        FlushRedraw();
    }

    /// <summary>Feed several chunks then flush once, mirroring the output
    /// pump's drain-then-repaint pattern.</summary>
    internal void SelfTestFeedBatch(params string[] chunks)
    {
        foreach (var chunk in chunks)
            _terminal.Feed(chunk);
        FlushRedraw();
    }

    /// <summary>Starts a real ConPTY session even in selftest mode (where the
    /// normal startup path is disabled), sized to the current grid.</summary>
    internal void SelfTestStartSession()
    {
        if (_session is not null)
            return;
        StartSession();
        _session?.Resize((short)_cols, (short)_rows);
    }

    /// <summary>Writes bytes to the live session's PTY input, as typed keys
    /// would; output flows back through the normal async pump.</summary>
    internal void SelfTestType(string text)
    {
        if (_session is null)
            return;
        var bytes = Encoding.UTF8.GetBytes(text);
        Diag($"type '{text.Replace("\r", "<CR>").Replace("\x1b", "<ESC>")}'");
        _session.Write(bytes);
    }

    /// <summary>Feed raw bytes in fixed-size chunks (like ConPTY delivery),
    /// one flush at the end — the pump's exact drain pattern.</summary>
    internal void SelfTestFeedBytes(byte[] data, int chunkSize = 64)
    {
        for (var off = 0; off < data.Length; off += chunkSize)
        {
            var n = Math.Min(chunkSize, data.Length - off);
            _terminal.Feed(data, off, n);
        }
        FlushRedraw();
    }

    internal void SelfTestScroll(int lines)
    {
        _terminal.ScrollBy(lines);
        FlushRedraw();
    }

    /// <summary>Runs the exact press-drag-release sequence the mouse handlers
    /// use, so the selection gesture (and its grid_ref calls) is testable
    /// without real pointer input. Ghostty includes a cell only when the
    /// pointer passes its 60%-width threshold, so the press lands left of it
    /// and the drag right of it (like a real left-to-right drag).</summary>
    internal void SelfTestSelect(int pressCol, int pressRow, int dragCol, int dragRow)
    {
        _selectionActive = true;
        _selectionDragged = true;
        var pressX = pressCol * _cellWidth + _cellWidth * 0.2;
        var pressY = pressRow * _cellHeight + _cellHeight * 0.5;
        var dragX = dragCol * _cellWidth + _cellWidth * 0.8;
        var dragY = dragRow * _cellHeight + _cellHeight * 0.5;
        _terminal.SelectionPress(pressCol, pressRow, pressX, pressY);
        _terminal.SelectionDrag(dragCol, dragRow, dragX, dragY);
        _terminal.SelectionRelease(dragCol, dragRow);
        FlushRedraw();
    }

    /// <summary>Plain text of the active selection, or null when there is none.</summary>
    internal string? SelfTestSelectedText() => _terminal.HasSelection ? _terminal.GetSelectedText() : null;

    internal string SelfTestScrollInfo()
    {
        var sb = _terminal.Scrollbar;
        return $"scrollbar total={sb.Total} offset={sb.Offset} len={sb.Len}";
    }

    internal string SelfTestCursorInfo()
    {
        var cursor = _terminal.Cursor;
        return $"cursor x={cursor.X} y={cursor.Y} visible={cursor.Visible} blink={cursor.Blinking} shape={cursor.Shape}";
    }

    internal string SelfTestRowText(int row)
    {
        if (row < 0 || row >= _terminal.FrameRows.Length)
            return "no-row";
        var sb = new StringBuilder();
        foreach (var cell in _terminal.FrameRows[row].Cells)
            sb.Append(cell.Text.Length > 0 ? cell.Text : " ");
        return sb.ToString().TrimEnd();
    }

    internal void SelfTestStabilizeCaret()
    {
        _selfTestCaret = true;
        _cursorBlinkSetting = false;
        UpdateBlinkTimer();
        DrawCaret();
    }

    // ---- Rendering --------------------------------------------------------

    private void FlushRedraw()
    {
        try
        {
            _terminal.UpdateFrame();
            var dirty = _terminal.FrameDirty;
            var scrollbar = _terminal.Scrollbar;
            var viewportMoved = scrollbar.Offset != _lastScrollOffset;
            _lastScrollOffset = scrollbar.Offset;
            Diag($"flush dirty={dirty} full={_needsFullRedraw} scroll={viewportMoved} offset={scrollbar.Offset}/{scrollbar.Total} rows={_rows} cols={_cols}");

            if (_needsFullRedraw || dirty == FrameDirty.Full || viewportMoved)
            {
                RedrawAll(force: true);
            }
            else if (dirty == FrameDirty.Partial)
            {
                // Redraw exactly the rows libghostty marked dirty. Unchanged
                // rows skip: the per-row hash covers every input the run pass
                // reads, so a match means identical pixels.
                for (var row = 0; row < Math.Min(_rows, _terminal.FrameRows.Length); row++)
                {
                    if (_terminal.FrameRows[row].Dirty)
                        RedrawRow(row);
                }
                // The cells under the block cursor were just repainted, so
                // the cursor overlay must redraw even if it did not move.
                InvalidateCaretCache();
            }

            _needsFullRedraw = false;

            DrawSelection();
            DrawCaret();
            DrawScrollbar();
        }
        catch (Exception e)
        {
            // Guard against emulator state issues; schedule a full redraw on
            // the next pump cycle.
            Diag($"flush-EXCEPTION {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n')[0]}");
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
        if (row >= frameRows.Length)
        {
            _rowVersions[row] = 0;
            using var clearDc = _rowVisuals[row].RenderOpen();
            return;
        }

        var frameRow = frameRows[row];

        // Skip the re-render when this row's cells are unchanged since it was
        // last drawn — the common case after a redundant full redraw.
        if (!force && _rowVersions[row] == _renderVersion && _rowHashes[row] == RowHash(frameRow))
            return;
        _rowVersions[row] = _renderVersion;
        _rowHashes[row] = RowHash(frameRow);

        var visual = _rowVisuals[row];
        using var dc = visual.RenderOpen();

        var y = row * _cellHeight;
        var cells = frameRow.Cells;
        if (cells.Length == 0)
        {
            _rowVersions[row] = 0;
            using var clearDc = _rowVisuals[row].RenderOpen();
            return;
        }
        ref readonly var first = ref cells[0];
        var runFgTag = first.FgTag;
        var runFg = first.FgValue;
        var runBgTag = first.BgTag;
        var runBg = first.BgValue;
        var runFlags = first.Flags;
        var runStart = 0;
        _runBuilder.Clear();
        EnsureTextRunCapacity(Math.Max(1, _cols));
        EnsureRunWidthCapacity(Math.Max(1, _cols * 2));
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
            var pen = new Pen(run.Foreground ?? _palette.Foreground, 1);
            pen.Freeze();
            var rowY = y;
            if (run.Underline)
                dc.DrawLine(pen, new Point(runX, rowY + _cellHeight - pen.Thickness), new Point(runX + runWidth, rowY + _cellHeight - pen.Thickness));
            if (run.CrossedOut)
                dc.DrawLine(pen, new Point(runX, rowY + _cellHeight / 2), new Point(runX + runWidth, rowY + _cellHeight / 2));
        }

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
            // default background is already filled by the control's base
            // layer, so re-filling here would darken text rows against the
            // frosted surface and expose a visible band.
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
            var syntheticBold = flags.HasFlag(CellFlags.Bold) && !ReferenceEquals(glyphFace, _boldGlyph);

            bool canUseGlyphRun = glyphFace != null;
            var contentLength = contentEnd;
            // GlyphRun uses the collection lengths as the glyph count. The
            // row-sized pools cannot be passed directly: WPF's retained-mode
            // DrawingContext keeps a reference to the glyph/advance arrays
            // until the render thread consumes the visual, so reusing them
            // across runs corrupts already-queued runs (stale characters,
            // jumbled glyphs). Per-run arrays are required.
            var glyphIndices = new ushort[contentLength];
            var advanceWidths = new double[contentLength];
            // Read the trimmed run directly from the run builder; no
            // per-run copy, so the map loop and FormattedText below index
            // runText up to contentEnd.
            var trimmedText = runText;

            if (canUseGlyphRun)
            {
                var map = glyphFace!.CharacterToGlyphMap;
                for (var i = 0; i < contentLength; i++)
                {
                    if (map.TryGetValue(trimmedText[i], out var glyphIndex))
                    {
                        glyphIndices[i] = glyphIndex;
                        // True cell advance: a wide glyph consumes two columns,
                        // so the next cell starts where the grid places it.
                        advanceWidths[i] = widths[i] * _cellWidth;
                    }
                    else
                    {
                        canUseGlyphRun = false;
                        break;
                    }
                }
            }

            if (canUseGlyphRun)
            {
                var glyphRun = new GlyphRun(
                    glyphFace!,
                    0,
                    false,
                    _fontSize,
                    (float)_pixelsPerDip,
                    glyphIndices,
                    new Point(x, rowY + _baselineY),
                    advanceWidths,
                    null, null, null, null, null, null);
                context.DrawGlyphRun(fg, glyphRun);
                if (syntheticBold)
                {
                    // Second pass offset by ~1px (fractional for subpixel
                    // crispness), the classic cheap fake-bold.
                    var boldRun = new GlyphRun(
                        glyphFace!,
                        0,
                        false,
                        _fontSize,
                        (float)_pixelsPerDip,
                        glyphIndices,
                        new Point(x + Math.Max(1, _pixelsPerDip), rowY + _baselineY),
                        advanceWidths,
                        null, null, null, null, null, null);
                    context.DrawGlyphRun(fg, boldRun);
                }
            }
            else
            {
                // Read only the trimmed portion: StringBuilder.ToString(start,
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
            run.HasDecoration = flags.HasFlag(CellFlags.Underline) || flags.HasFlag(CellFlags.Strikethrough);
            run.Underline = flags.HasFlag(CellFlags.Underline);
            run.CrossedOut = flags.HasFlag(CellFlags.Strikethrough);
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

    /// <summary>
    /// FNV-1a over the cells RedrawRow renders: (text, width, flags, fg, bg)
    /// per column, mirroring the render loop's cell selection so equal hashes
    /// guarantee equal pixels for the current metrics.
    /// </summary>
    private int RowHash(FrameRow row)
    {
        ulong hash = 14695981039346656037;
        var cells = row.Cells;
        for (var col = 0; col < _cols && col < cells.Length; col++)
        {
            ref readonly var cell = ref cells[col];
            hash = (hash ^ (ulong)(uint)(cell.Text.Length == 0 ? 0 : cell.Text[0])) * 1099511628211;
            hash = (hash ^ (ulong)(uint)(cell.Text.Length)) * 1099511628211;
            hash = (hash ^ (ulong)(cell.Wide ? 1u : 0u)) * 1099511628211;
            hash = (hash ^ (ulong)(byte)cell.Flags) * 1099511628211;
            hash = (hash ^ (ulong)(uint)cell.FgValue ^ (uint)cell.FgTag) * 1099511628211;
            hash = (hash ^ (ulong)(uint)cell.BgValue ^ (uint)cell.BgTag) * 1099511628211;
        }
        return (int)(hash ^ (hash >> 32));
    }

    private Typeface ResolveTypeface(CellFlags flags)
    {
        var bold = flags.HasFlag(CellFlags.Bold);
        var italic = flags.HasFlag(CellFlags.Italic);
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
        var bold = flags.HasFlag(CellFlags.Bold);
        var italic = flags.HasFlag(CellFlags.Italic);
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

    /// <summary>True while there is scrollback above the viewport to scroll into.</summary>
    private bool IsScrollbarVisible()
    {
        var scrollbar = _terminal.Scrollbar;
        return scrollbar.Total > scrollbar.Len && scrollbar.Len > 0;
    }

    private bool IsOverScrollbar(Point point)
        => IsScrollbarVisible() && point.X >= ActualWidth - ScrollbarHitWidth;

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
        if (!TryGetScrollbarGeometry(out var thumbY, out var thumbH, out _))
            return;

        var brush = _scrollbarHovered || _scrollbarDragging ? _scrollbarThumbHoverBrush : _scrollbarThumbBrush;
        var x = ActualWidth - _scrollbarWidth;
        var radius = _scrollbarWidth / 2;
        dc.DrawRoundedRectangle(brush, null, new Rect(x, thumbY, _scrollbarWidth, thumbH), radius, radius);
    }

    private void SetScrollbarHovered(bool hovered)
    {
        if (_scrollbarHovered == hovered)
            return;
        _scrollbarHovered = hovered;
        Cursor = hovered ? Cursors.Arrow : Cursors.IBeam;
        _scrollbarTargetWidth = hovered || _scrollbarDragging ? ScrollbarWideWidth : ScrollbarThinWidth;
        if (Math.Abs(_scrollbarTargetWidth - _scrollbarWidth) < 0.2)
            DrawScrollbar();
        else if (!_scrollbarAnimTimer.IsEnabled)
            _scrollbarAnimTimer.Start();
    }

    private void AnimateScrollbarWidth()
    {
        var delta = _scrollbarTargetWidth - _scrollbarWidth;
        if (Math.Abs(delta) < 0.2)
        {
            _scrollbarWidth = _scrollbarTargetWidth;
            _scrollbarAnimTimer.Stop();
            DrawScrollbar();
            return;
        }
        _scrollbarWidth += delta * 0.3;
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
            FlushRedraw();
        }
    }

    // ---- Input ------------------------------------------------------------

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        UpdateBlinkTimer();
        DrawCaret();
        FocusGained?.Invoke();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
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

        // Workspace shortcuts are handled before terminal key translation.
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            var command = key switch
            {
                Key.Right => TerminalCommand.SplitRight,
                Key.Down => TerminalCommand.SplitDown,
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
        }

        // Ctrl+C copies when a selection exists, otherwise sends ETX.
        if (key == Key.C && mods == ModifierKeys.Control && _selectionActive)
        {
            CopySelection();
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
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Control && key == Key.End)
            {
                _terminal.ScrollToBottom();
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Shift && key == Key.PageUp)
            {
                _terminal.ScrollBy(-_rows);
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Shift && key == Key.PageDown)
            {
                _terminal.ScrollBy(_rows);
                FlushRedraw();
                e.Handled = true;
                return;
            }
        }

        if (TerminalKeyMap.Map(key, mods, _terminal.ApplicationCursor) is { } bytes)
        {
            _session.Write(bytes);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        Diag($"text '{e.Text.Replace("\r", "<CR>")}' session={_session is not null}");
        if (_session is null || string.IsNullOrEmpty(e.Text))
            return;
        _session.Write(Encoding.UTF8.GetBytes(e.Text));
        e.Handled = true;
    }

    private void CopySelection()
    {
        if (!_selectionActive && !_terminal.HasSelection)
            return;
        var text = _terminal.GetSelectedText();
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    private void ClearSelection()
    {
        if (_selectionActive || _terminal.HasSelection)
        {
            _selectionActive = false;
            _terminal.ClearSelection();
            FlushRedraw();
        }
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

    // ---- Mouse ------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_session is null)
            return;

        var pos = e.GetPosition(this);

        // The scrollbar overlays the right edge; clicks there scroll instead
        // of selecting or forwarding to the app's mouse mode.
        if (IsOverScrollbar(pos) && TryGetScrollbarGeometry(out var thumbY, out var thumbH, out _))
        {
            if (pos.Y >= thumbY && pos.Y <= thumbY + thumbH)
            {
                _scrollbarDragging = true;
                _scrollbarDragOffset = pos.Y - thumbY;
                _scrollbarWidth = _scrollbarTargetWidth = ScrollbarWideWidth;
                CaptureMouse();
                DrawScrollbar();
            }
            else
            {
                _terminal.ScrollBy(pos.Y < thumbY ? -_rows : _rows);
                FlushRedraw();
            }
            e.Handled = true;
            return;
        }

        var (col, row) = CellFromPoint(pos);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _terminal.MouseTracking)
        {
            // Shift overrides app mouse capture, as in xterm; it starts a
            // fresh selection at the pointer instead of extending.
            _selectionActive = true;
            _selectionDragged = false;
            _terminal.SelectionPress(col, row, pos.X, pos.Y);
            CaptureMouse();
            FlushRedraw();
            e.Handled = true;
            return;
        }

        // Applications that capture the mouse get their events instead of
        // selection.
        if (_terminal.MouseTracking)
        {
            SendMouse(0, col, row, release: false, motion: false);
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            // The gesture's own click-count state (fed by press timestamps)
            // derives word selection on the second press.
            _selectionActive = true;
            _selectionDragged = false;
            _terminal.SelectionPress(col, row, pos.X, pos.Y);
            CaptureMouse();
            FlushRedraw();
            e.Handled = true;
            return;
        }

        _selectionActive = true;
        _selectionDragged = false;
        _terminal.SelectionPress(col, row, pos.X, pos.Y);
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

        SetScrollbarHovered(IsOverScrollbar(pos));

        if (_terminal.MouseTracking && e.LeftButton == MouseButtonState.Pressed)
        {
            var (col, row) = CellFromPoint(pos);
            SendMouse(0, col, row, release: false, motion: true);
            return;
        }
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed && _selectionActive)
        {
            var (col, row) = CellFromPoint(pos);
            _selectionDragged = true;
            _terminal.SelectionDrag(col, row, pos.X, pos.Y);
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

        if (_terminal.MouseTracking)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            SendMouse(0, col, row, release: true, motion: false);
            e.Handled = true;
        }
        else if (_selectionActive)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            _terminal.SelectionRelease(col, row);
            if (!_selectionDragged)
                ClearSelection();
            else
                FlushRedraw();
            _selectionDragged = false;
        }
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_terminal.MouseTracking)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            SendMouse(2, col, row, release: false, motion: false);
            e.Handled = true;
            return;
        }
        PasteClipboard();
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
        if (_terminal.MouseTracking)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            SendMouse(e.Delta > 0 ? 4 : 5, col, row, release: false, motion: false);
            e.Handled = true;
            return;
        }
        if (_terminal.IsAlternateScreen)
            return; // viewport scrollback does not exist on the alt screen
        var lines = Math.Max(1, SystemParameters.WheelScrollLines) * (e.Delta / 120);
        if (lines != 0)
            _terminal.ScrollBy(-lines);
        FlushRedraw();
        e.Handled = true;
    }

    /// <summary>SGR mouse report (1006): ESC [ &lt; b ; x+1 ; y+1 M/m.</summary>
    private void SendMouse(int button, int col, int row, bool release, bool motion)
    {
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        var code = button;
        if (motion)
            code += 32;
        else if (button >= 4)
            code += 64; // wheel
        if (shift)
            code += 4;
        if (alt)
            code += 8;
        if (ctrl)
            code += 16;

        var sequence = $"\x1b[<{code};{col + 1};{row + 1}{(release ? 'm' : 'M')}";
        _session?.Write(Encoding.ASCII.GetBytes(sequence));
    }

    private (int Col, int Row) CellFromPoint(Point point)
    {
        var col = Math.Clamp((int)(point.X / _cellWidth), 0, Math.Max(0, _cols - 1));
        var row = Math.Clamp((int)(point.Y / _cellHeight), 0, Math.Max(0, _rows - 1));
        return (col, row);
    }

    // ---- Dispose ------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _blinkTimer.Stop();
        _scrollbarAnimTimer.Stop();
        AppSettings.Instance.PropertyChanged -= OnSettingsChanged;
        _session?.Dispose();
        _terminal.Dispose();
    }

    // FocusTerminal is called by the workspace when the pane gets activated.
    public void FocusTerminal() => Focus();

    /// <summary>True while an app is running on the alternate screen buffer
    /// (a full-screen TUI); app-level shortcuts yield to the TUI then.</summary>
    public bool IsTuiMode => _terminal.IsAlternateScreen;
}
