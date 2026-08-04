using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vex.App.Model;
using Vex.Terminal;
using XtermSharp;

namespace Vex.App.Terminal.Native;

/// <summary>
/// The native terminal surface: XtermSharp emulates the VT stream and this
/// control renders the grid directly in WPF (one DrawingVisual per row,
/// redrawn only when the emulator marks it dirty). No browser bridge, no IPC —
/// the app's fast path in the spirit of upstream's Alacritty backend.
/// </summary>
public sealed class NativeTerminalControl : FrameworkElement, ITerminalView
{
    private readonly string _workingDirectory;
    private readonly XtermSharp.Terminal _terminal;
    private readonly SelectionService _selection;
    private readonly VisualCollection _children;
    private readonly DrawingVisual _selectionVisual = new();
    private readonly DrawingVisual _caretVisual = new();
    private readonly List<DrawingVisual> _rowVisuals = new();
    private readonly DispatcherTimer _blinkTimer;
    private readonly StringBuilder _runBuilder = new();
    private readonly StringBuilder _trimmedRun = new();

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
    private bool _viewportMoved;
    private bool _needsFullRedraw = true;
    private bool _disposed;

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

    // Cached caret draw state: the overlay is only reopened when the caret's
    // position, visibility, style or blink phase changed. The cell beneath the
    // cursor is repainted by the row pass, so a stationary caret needs no
    // overlay work on pumps that do not touch its row.
    private bool _caretCacheValid;
    private bool _caretCacheShouldDraw;
    private int _caretCacheRow;
    private int _caretCacheCol;
    private int _caretCacheStyle;
    private readonly ushort[] _caretGlyphIndex = new ushort[1];
    private readonly double[] _caretAdvanceWidths = new double[1];

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

        _terminal = new XtermSharp.Terminal(new DelegateBridge(this), new TerminalOptions
        {
            Cols = 80,
            Rows = 24,
            TermName = "xterm-256color",
        });
        _terminal.Scrolled += (_, _) => _viewportMoved = true;
        _terminal.Buffers.Activated += (_, _) => _viewportMoved = true;

        _selection = new SelectionService(_terminal);
        _selection.SelectionChanged += () =>
        {
            DrawSelection();
            DrawCaret();
        };

        _children = new VisualCollection(this)
        {
            _selectionVisual,
            _caretVisual,
        };

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) =>
        {
            _caretBlinkVisible = !_caretBlinkVisible;
            DrawCaret();
        };

        ApplySettings();
        AppSettings.Instance.PropertyChanged += OnSettingsChanged;

        Loaded += (_, _) =>
        {
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            RebuildFontMetrics();
        };
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        ApplySettings();
        RebuildFontMetrics();
        _needsFullRedraw = true;
        InvalidateVisual();
        FlushRedraw();
    }

    private void ApplySettings()
    {
        var settings = AppSettings.Instance;
        var theme = BuiltInThemes.All.FirstOrDefault(t => t.Name == settings.ThemeName) ?? BuiltInThemes.VexDark;
        _palette = new TerminalPalette(theme);
        _fontFamily = new FontFamily(settings.FontFamily);
        _normalTypeface = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _boldTypeface = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _italicTypeface = new Typeface(_fontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        _boldItalicTypeface = new Typeface(_fontFamily, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
        _normalTypeface.TryGetGlyphTypeface(out _normalGlyph);
        _boldTypeface.TryGetGlyphTypeface(out _boldGlyph);
        _italicTypeface.TryGetGlyphTypeface(out _italicGlyph);
        _boldItalicTypeface.TryGetGlyphTypeface(out _boldItalicGlyph);
        _fontSize = settings.FontSize;
        _terminal.Options.CursorBlink = settings.CursorBlink;
        UpdateBlinkTimer();
    }

    private void UpdateBlinkTimer()
    {
        var blink = _terminal.Options.CursorBlink && !_terminal.Options.CursorStyle.ToString().Contains("Steady");
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
        EnsureRowVisuals();
        _terminal.Resize(cols, rows);
        // After resize, snap viewport to bottom so shells like nushell
        // don't appear scrolled up due to buffer reflow.
        _terminal.Buffer.YDisp = _terminal.Buffer.YBase;
        _needsFullRedraw = true;
        RedrawAll();

        if (_session is null)
        {
            // Spawn the shell only once the layout settles at a sane geometry
            // so prompt redraw logic in shells does not thrash on startup sizes.
            if (cols >= 20 && rows >= 5)
            {
                StartSession();
                _terminal.Buffer.YDisp = 0;
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
            // Rows sit between the selection overlay and the caret.
            _children.Insert(_children.Count - 1, visual);
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
        RecalculateGridSize();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _pixelsPerDip = newDpi.PixelsPerDip;
        _needsFullRedraw = true;
        RedrawAll();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_palette.Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    // ---- Session ----------------------------------------------------------

    private void StartSession()
    {
        if (_session is not null)
            return;

        var session = new TerminalSession();
        session.OutputReceived += OnSessionOutput;
        session.Exited += OnSessionExited;
        var shell = AppSettings.Instance.Shell switch
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

        _viewportMoved = false;
        foreach (var c in toProcess)
        {
            if (c.Array is not { } buffer)
                continue;
            try
            {
                _terminal.Feed(buffer, c.Count);
            }
            catch (Exception)
            {
                // XtermSharp may throw on malformed or unsupported VT
                // sequences. Keep draining the batch so every pooled buffer
                // is returned and schedule a full redraw.
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

    /// <summary>PTY-bound answers from the emulator (DA, DECRQSS, …).</summary>
    private void SendToPty(byte[] data) => _session?.Write(data);

    // ---- Rendering --------------------------------------------------------

    private void FlushRedraw()
    {
        try
        {
            _terminal.GetUpdateRange(out var startY, out var endY);
            _terminal.ClearUpdateRange();

            var buffer = _terminal.Buffer;
            var userScrolled = buffer.YDisp != buffer.YBase;
            var hasUpdate = startY <= endY;

            // An empty update range means the batch only moved the cursor or
            // produced no cell writes; redrawing every row then would run the
            // whole run-analysis pass for nothing on each such pump. Scrolls
            // are covered separately via _viewportMoved.
            if (_needsFullRedraw || _viewportMoved || userScrolled ||
                (hasUpdate && endY - startY > _rows / 2))
            {
                RedrawAll();
            }
            else if (hasUpdate)
            {
                for (var row = Math.Max(0, startY); row <= Math.Min(_rows - 1, endY); row++)
                    RedrawRow(row);
                // The cells under the block cursor were just repainted, so the
                // cursor overlay must redraw even if it did not move.
                InvalidateCaretCache();
            }
            _needsFullRedraw = false;
            _viewportMoved = false;

            DrawSelection();
            DrawCaret();
        }
        catch (Exception)
        {
            // Guard against XtermSharp internal state issues (e.g. buffer
            // reflow during alternate-screen apps like nvim). Schedule a
            // full redraw on the next pump cycle.
            _needsFullRedraw = true;
        }
    }

    private void RedrawAll()
    {
        // A full redraw repaints every cell, including the one under the
        // block cursor, so the caret overlay must redraw with it (also covers
        // font metrics/DPI/resize changes that move the caret geometry).
        InvalidateCaretCache();
        for (var row = 0; row < _rowVisuals.Count; row++)
            RedrawRow(row);
    }

    private void RedrawRow(int row)
    {
        if (row < 0 || row >= _rowVisuals.Count)
            return;

        var visual = _rowVisuals[row];
        using var dc = visual.RenderOpen();

        var buffer = _terminal.Buffer;
        var lineIndex = buffer.YDisp + row;
        if (lineIndex < 0 || lineIndex >= buffer.Lines.Length)
            return;
        var line = buffer.Lines[lineIndex];
        if (line is null)
            return;

        var y = row * _cellHeight;
        var runAttr = line.Length > 0 ? line[0].Attribute : CharData.DefaultAttr;
        var runStart = 0;
        _runBuilder.Clear();
        EnsureTextRunCapacity(Math.Max(1, _cols));
        var textRuns = 0;

        for (var col = 0; col < _cols; col++)
        {
            var cell = col < line.Length ? line[col] : CharData.Null;
            if (cell.Attribute != runAttr && _runBuilder.Length > 0)
            {
                textRuns = FlushRun(dc, _runBuilder, runAttr, runStart, y, textRuns);
                runStart = col;
                runAttr = cell.Attribute;
                _runBuilder.Clear();
            }
            else if (cell.Attribute != runAttr)
            {
                runStart = col;
                runAttr = cell.Attribute;
            }

            if (cell.Width == 0 && cell.Code == 0)
                continue; // trailing half of a wide glyph
            if (cell.Code == 0)
                _runBuilder.Append(' ');
            else if (cell.Code < 0x10000)
                _runBuilder.Append((char)cell.Code);
            else
                _runBuilder.Append(cell.Rune.ToString());
        }

        if (_runBuilder.Length > 0)
            textRuns = FlushRun(dc, _runBuilder, runAttr, runStart, y, textRuns);

        // Draw underlines for runs that have text; the pooled glyph arrays
        // index into the textRuns we recorded.
        for (var i = 0; i < textRuns; i++)
        {
            ref readonly var run = ref _textRuns[i];
            if (run.Attr == 0 || !run.HasDecoration) continue;
            var runX = run.StartCol * _cellWidth;
            var runWidth = run.Length * _cellWidth;
            var pen = _decorationPen;
            var rowY = y;
            if (run.Underline)
                dc.DrawLine(pen, new Point(runX, rowY + _cellHeight - pen.Thickness), new Point(runX + runWidth, rowY + _cellHeight - pen.Thickness));
            if (run.CrossedOut)
                dc.DrawLine(pen, new Point(runX, rowY + _cellHeight / 2), new Point(runX + runWidth, rowY + _cellHeight / 2));
        }

        int FlushRun(DrawingContext context, StringBuilder runText, int attr, int startCol, double rowY, int textRunIndex)
        {
            _palette.Resolve(attr, out var fg, out var bg, out var flags);
            var x = startCol * _cellWidth;

            // ClearType needs a solid background in the same DrawingVisual to blend correctly.
            // If background is transparent (default), it causes weird color fringing.
            bg ??= _palette.Background;

            if (bg is not null)
                context.DrawRectangle(bg, null, new Rect(x, rowY, runText.Length * _cellWidth, _cellHeight));

            // Trailing whitespace carries no ink; skip the text pass for it.
            _trimmedRun.Clear();
            var contentEnd = runText.Length;
            while (contentEnd > 0 && runText[contentEnd - 1] == ' ')
                contentEnd--;
            if (contentEnd == 0 || fg is null)
                return textRunIndex;
            _trimmedRun.Append(runText, 0, contentEnd);

            var face = ResolveTypeface(flags);
            var glyphFace = ResolveGlyphTypeface(flags);

            bool canUseGlyphRun = glyphFace != null;
            var contentLength = _trimmedRun.Length;
            // GlyphRun uses the collection lengths as the glyph count. The
            // row-sized pools cannot be passed directly: stale entries after
            // this run would be rendered as extra characters.
            var glyphIndices = new ushort[contentLength];
            var advanceWidths = new double[contentLength];

            if (canUseGlyphRun)
            {
                var map = glyphFace!.CharacterToGlyphMap;
                for (var i = 0; i < contentLength; i++)
                {
                    if (map.TryGetValue(_trimmedRun[i], out var glyphIndex))
                    {
                        glyphIndices[i] = glyphIndex;
                        advanceWidths[i] = _cellWidth;
                    }
                    else
                    {
                        canUseGlyphRun = false;
                        break;
                    }
                }
            }

            double runWidth = 0;
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
                runWidth = contentLength * _cellWidth;
            }
            else
            {
                var formatted = new FormattedText(_trimmedRun.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    face, _fontSize, fg, _pixelsPerDip);
                context.DrawText(formatted, new Point(x, rowY));
                runWidth = formatted.Width;
            }

            // Record run metadata for the decoration pass after all runs.
            ref var run = ref _textRuns[textRunIndex];
            run.StartCol = startCol;
            run.Length = runText.Length;
            run.Attr = attr;
            run.HasDecoration = flags.HasFlag(FLAGS.UNDERLINE) || flags.HasFlag(FLAGS.CrossedOut);
            run.Underline = flags.HasFlag(FLAGS.UNDERLINE);
            run.CrossedOut = flags.HasFlag(FLAGS.CrossedOut);
            return textRunIndex + 1;
        }
    }

    private void EnsureTextRunCapacity(int required)
    {
        if (_textRuns.Length >= required)
            return;
        Array.Resize(ref _textRuns, required);
    }

    private struct TextRun
    {
        public int StartCol;
        public int Length;
        public bool HasDecoration;
        public bool Underline;
        public bool CrossedOut;
        public int Attr;
    }

    private TextRun[] _textRuns = Array.Empty<TextRun>();
    private readonly Pen _decorationPen = new Pen(Brushes.Transparent, 0);

    private Typeface ResolveTypeface(FLAGS flags)
    {
        var bold = flags.HasFlag(FLAGS.BOLD);
        var italic = flags.HasFlag(FLAGS.ITALIC);
        return (bold, italic) switch
        {
            (true, true) => _boldItalicTypeface,
            (true, false) => _boldTypeface,
            (false, true) => _italicTypeface,
            _ => _normalTypeface,
        };
    }

    private GlyphTypeface? ResolveGlyphTypeface(FLAGS flags)
    {
        var bold = flags.HasFlag(FLAGS.BOLD);
        var italic = flags.HasFlag(FLAGS.ITALIC);
        return (bold, italic) switch
        {
            (true, true) => _boldItalicGlyph,
            (true, false) => _boldGlyph,
            (false, true) => _italicGlyph,
            _ => _normalGlyph,
        };
    }

    private void DrawCaret()
    {
        var buffer = _terminal.Buffer;
        var hidden = _session is null || _terminal.CursorHidden;
        var row = hidden ? -1 : buffer.Y + buffer.YBase - buffer.YDisp;
        var col = hidden ? -1 : Math.Min(buffer.X, _cols - 1);
        var blinkOn = !_terminal.Options.CursorBlink || !IsKeyboardFocused || _caretBlinkVisible;
        var style = (int)_terminal.Options.CursorStyle;
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

        switch (_terminal.Options.CursorStyle)
        {
            case CursorStyle.BlinkingBar:
            case CursorStyle.SteadyBar:
                dc.DrawRectangle(_palette.Cursor, null, new Rect(x, y, Math.Max(2, _cellWidth / 6), _cellHeight));
                break;
            case CursorStyle.BlinkUnderline:
            case CursorStyle.SteadyUnderline:
                dc.DrawRectangle(_palette.Cursor, null, new Rect(x, y + _cellHeight - 2, _cellWidth, 2));
                break;
            default:
                // Block cursor: reverse-video the cell, as xterm does.
                dc.DrawRectangle(_palette.Cursor, null, new Rect(x, y, _cellWidth, _cellHeight));
                var lineIndex = buffer.Y + buffer.YBase;
                if (lineIndex < buffer.Lines.Length)
                {
                    var line = buffer.Lines[lineIndex];
                    var cell = col < line.Length ? line[col] : CharData.Null;
                    if (cell.Code > 0 && cell.Width > 0 && _normalGlyph is { } glyphFace &&
                        glyphFace.CharacterToGlyphMap.TryGetValue(cell.Code < 0x10000 ? (char)cell.Code : (char)cell.Rune, out var glyphIndex))
                    {
                        // Block cursor: draw the cell glyph with the palette's
                        // background color via a cached single-glyph run, no
                        // FormattedText or per-blink array allocation.
                        _caretGlyphIndex[0] = glyphIndex;
                        _caretAdvanceWidths[0] = _cellWidth;
                        var glyphRun = new GlyphRun(
                            glyphFace,
                            0,
                            false,
                            _fontSize,
                            (float)_pixelsPerDip,
                            _caretGlyphIndex,
                            new Point(x, y + _baselineY),
                            _caretAdvanceWidths,
                            null, null, null, null, null, null);
                        dc.DrawGlyphRun(_palette.Background, glyphRun);
                    }
                }
                break;
        }
    }

    private bool _selectionVisualDrawn;

    private void DrawSelection()
    {
        if (!_selection.Active)
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

        var (start, end) = OrderSelection();
        if (start == end)
            return;

        var yDisp = _terminal.Buffer.YDisp;
        for (var row = 0; row < _rows; row++)
        {
            var bufferRow = yDisp + row;
            if (bufferRow < start.Y || bufferRow > end.Y)
                continue;

            var fromCol = bufferRow == start.Y ? start.X : 0;
            var toCol = bufferRow == end.Y ? end.X : _cols - 1;
            if (toCol < fromCol)
                continue;

            dc.DrawRectangle(_palette.Selection, null,
                new Rect(fromCol * _cellWidth, row * _cellHeight, (toCol - fromCol + 1) * _cellWidth, _cellHeight));
        }
    }

    private (System.Drawing.Point Start, System.Drawing.Point End) OrderSelection()
    {
        var start = _selection.Start;
        var end = _selection.End;
        if (start.Y > end.Y || (start.Y == end.Y && start.X > end.X))
            (start, end) = (end, start);
        return (start, end);
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
        if (key == Key.C && mods == ModifierKeys.Control && _selection.Active)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        _selection.Active = false;

        // Viewport scroll keybindings (normal buffer only — alt screen has no scrollback).
        if (!_terminal.Buffers.IsAlternateBuffer)
        {
            if (mods == ModifierKeys.Control && key == Key.Home)
            {
                _terminal.ScrollLines(-_terminal.Buffer.YDisp);
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Control && key == Key.End)
            {
                _terminal.ScrollLines(_terminal.Buffer.YBase - _terminal.Buffer.YDisp);
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Shift && key == Key.PageUp)
            {
                _terminal.ScrollLines(-_rows);
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Shift && key == Key.PageDown)
            {
                _terminal.ScrollLines(_rows);
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
        if (_session is null || string.IsNullOrEmpty(e.Text))
            return;
        _session.Write(Encoding.UTF8.GetBytes(e.Text));
        e.Handled = true;
    }

    private void CopySelection()
    {
        if (!_selection.Active)
            return;
        var text = _selection.GetSelectedText();
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    private void PasteClipboard()
    {
        if (_session is null || !Clipboard.ContainsText())
            return;
        var text = Clipboard.GetText().Replace("\r\n", "\r").Replace("\n", "\r");
        if (_terminal.BracketedPasteMode)
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

        var (col, row) = CellFromPoint(e.GetPosition(this));

        if (e.ClickCount == 2)
        {
            _selection.SelectWordOrExpression(col, row);
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _selection.ShiftExtend(row, col);
            return;
        }

        // Applications that capture the mouse get their events instead of
        // selection (Shift above overrides, as in xterm).
        if (_terminal.MouseMode != MouseMode.Off)
        {
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var cb = _terminal.EncodeMouseButton(0, release: false, shift: shift, meta: alt, control: ctrl);
            _terminal.SendEvent(cb, col, row);
            e.Handled = true;
            return;
        }

        _selection.StartSelection(row, col);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_terminal.MouseMode != MouseMode.Off && e.LeftButton == MouseButtonState.Pressed)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var cb = _terminal.EncodeMouseButton(0, release: false, shift: shift, meta: alt, control: ctrl);
            _terminal.SendMouseMotion(cb, col, row);
            return;
        }
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed && _selection.Active)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            _selection.DragExtend(row, col);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_terminal.MouseMode != MouseMode.Off)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var cb = _terminal.EncodeMouseButton(0, release: true, shift: shift, meta: alt, control: ctrl);
            _terminal.SendEvent(cb, col, row);
            e.Handled = true;
        }
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_terminal.MouseMode != MouseMode.Off)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var cb = _terminal.EncodeMouseButton(2, release: false, shift: shift, meta: alt, control: ctrl);
            _terminal.SendEvent(cb, col, row);
            e.Handled = true;
            return;
        }
        PasteClipboard();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_terminal.MouseMode != MouseMode.Off)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            var button = e.Delta > 0 ? 4 : 5;
            var cb = _terminal.EncodeMouseButton(button, release: false, shift: false, meta: false, control: false);
            _terminal.SendEvent(cb, col, row);
            e.Handled = true;
            return;
        }
        if (_terminal.Buffers.IsAlternateBuffer)
            return; // viewport scrollback does not exist on the alt screen
        var lines = Math.Max(1, SystemParameters.WheelScrollLines) * (e.Delta / 120);
        if (lines != 0)
            _terminal.ScrollLines(-lines);
        FlushRedraw();
        e.Handled = true;
    }

    private (int Col, int Row) CellFromPoint(Point point)
    {
        var col = Math.Clamp((int)(point.X / _cellWidth), 0, Math.Max(0, _cols - 1));
        var row = Math.Clamp((int)(point.Y / _cellHeight), 0, Math.Max(0, _rows - 1));
        return (col, row);
    }

    // ---- Emulator delegate -------------------------------------------------

    private sealed class DelegateBridge : SimpleTerminalDelegate
    {
        private readonly NativeTerminalControl _owner;

        public DelegateBridge(NativeTerminalControl owner) => _owner = owner;

        public override void Send(byte[] data) => _owner.SendToPty(data);

        public override void SetTerminalTitle(XtermSharp.Terminal source, string title)
        {
            _owner.TitleRawChanged?.Invoke(title);
            _owner.TitleChanged?.Invoke(title);
        }
    }

    // ---- Dispose ------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _blinkTimer.Stop();
        AppSettings.Instance.PropertyChanged -= OnSettingsChanged;
        _session?.Dispose();
    }

    // FocusTerminal is called by the workspace when the pane gets activated.
    public void FocusTerminal() => Focus();
}
