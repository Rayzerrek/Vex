using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kero.App.Model;
using Kero.Terminal;
using XtermSharp;

namespace Kero.App.Terminal.Native;

/// <summary>
/// The native terminal surface: XtermSharp emulates the VT stream and this
/// control renders the grid directly in WPF (one DrawingVisual per row,
/// redrawn only when the emulator marks it dirty). No WebView2, no IPC —
/// the fast counterpart of <see cref="TerminalControl"/>, in the spirit of
/// upstream's Alacritty backend.
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

    private TerminalSession? _session;
    private TerminalPalette _palette;
    private FontFamily _fontFamily = new("Cascadia Mono");
    private double _fontSize = 13;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _pixelsPerDip = 1.0;
    private int _cols;
    private int _rows;
    private bool _viewportMoved;
    private bool _needsFullRedraw = true;
    private bool _disposed;

    private readonly object _outputLock = new();
    private readonly List<byte[]> _pendingOutput = new();
    private bool _pumpScheduled;

    private bool _caretBlinkVisible = true;

    public event Action<string>? TitleChanged;
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
        RedrawAll();
    }

    private void ApplySettings()
    {
        var settings = AppSettings.Instance;
        var theme = BuiltInThemes.All.FirstOrDefault(t => t.Name == settings.ThemeName) ?? BuiltInThemes.KeroDark;
        _palette = new TerminalPalette(theme);
        _fontFamily = new FontFamily(settings.FontFamily);
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
            var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, _fontSize, Brushes.White, _pixelsPerDip);
            _cellWidth = Math.Max(1, probe.WidthIncludingTrailingWhitespace);
            _cellHeight = Math.Max(1, Math.Ceiling(probe.Height));
        }

        RecalculateGridSize();
    }

    private void RecalculateGridSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var cols = Math.Max(2, (int)(ActualWidth / _cellWidth));
        var rows = Math.Max(1, (int)(ActualHeight / _cellHeight));
        if (cols == _cols && rows == _rows)
            return;

        _cols = cols;
        _rows = rows;
        EnsureRowVisuals();
        _terminal.Resize(cols, rows);
        _needsFullRedraw = true;
        RedrawAll();

        if (_session is null)
        {
            // Same contract as the WebView2 backend: spawn the shell only once
            // the layout settles at a sane geometry, so prompt redraw logic in
            // shells (nushell) does not thrash on startup sizes.
            if (cols >= 10 && rows >= 2)
                StartSession();
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
        var session = new TerminalSession();
        session.OutputReceived += OnSessionOutput;
        session.Exited += OnSessionExited;
        var shell = AppSettings.Instance.Shell == "Nushell" ? "nu.exe" : TerminalSession.DefaultShell();
        session.Start(_workingDirectory, (short)_cols, (short)_rows, shell);
        _session = session;
    }

    private void OnSessionOutput(byte[] chunk)
    {
        lock (_outputLock)
        {
            if (_pendingOutput.Count < 10000)
                _pendingOutput.Add(chunk);
            if (_pumpScheduled)
                return;
            _pumpScheduled = true;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            byte[] combined;
            lock (_outputLock)
            {
                _pumpScheduled = false;
                if (_pendingOutput.Count == 0)
                    return;
                if (_pendingOutput.Count == 1)
                {
                    combined = _pendingOutput[0];
                }
                else
                {
                    var total = _pendingOutput.Sum(c => c.Length);
                    combined = new byte[total];
                    var offset = 0;
                    foreach (var c in _pendingOutput)
                    {
                        System.Buffer.BlockCopy(c, 0, combined, offset, c.Length);
                        offset += c.Length;
                    }
                }
                _pendingOutput.Clear();
            }

            if (_disposed)
                return;

            _viewportMoved = false;
            _terminal.Feed(combined);
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

    /// <summary>PTY-bound answers from the emulator (DA, DECRQSS, …).</summary>
    private void SendToPty(byte[] data) => _session?.Write(data);

    // ---- Rendering --------------------------------------------------------

    private void FlushRedraw()
    {
        _terminal.GetUpdateRange(out var startY, out var endY);
        _terminal.ClearUpdateRange();

        var buffer = _terminal.Buffer;
        var userScrolled = buffer.YDisp != buffer.YBase;

        if (_needsFullRedraw || _viewportMoved || userScrolled || endY - startY > _rows / 2)
        {
            RedrawAll();
        }
        else if (endY >= startY)
        {
            for (var row = Math.Max(0, startY); row <= Math.Min(_rows - 1, endY); row++)
                RedrawRow(row);
        }
        _needsFullRedraw = false;
        _viewportMoved = false;

        DrawSelection();
        DrawCaret();
        InvalidateVisual(); // background
    }

    private void RedrawAll()
    {
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
        var text = new StringBuilder();

        for (var col = 0; col < _cols; col++)
        {
            var cell = col < line.Length ? line[col] : CharData.Null;
            if (cell.Attribute != runAttr && text.Length > 0)
            {
                FlushRun(dc, text, runAttr, runStart, y);
                runStart = col;
                runAttr = cell.Attribute;
                text.Clear();
            }
            else if (cell.Attribute != runAttr)
            {
                runStart = col;
                runAttr = cell.Attribute;
            }

            if (cell.Width == 0 && cell.Code == 0)
                continue; // trailing half of a wide glyph
            if (cell.Code == 0)
                text.Append(' ');
            else if (cell.Code < 0x10000)
                text.Append((char)cell.Code);
            else
                text.Append(cell.Rune.ToString());
        }

        if (text.Length > 0)
            FlushRun(dc, text, runAttr, runStart, y);

        void FlushRun(DrawingContext context, StringBuilder runText, int attr, int startCol, double rowY)
        {
            _palette.Resolve(attr, out var fg, out var bg, out var flags);
            var x = startCol * _cellWidth;

            if (bg is not null)
                context.DrawRectangle(bg, null, new Rect(x, rowY, runText.Length * _cellWidth, _cellHeight));

            // Trailing whitespace carries no ink; skip the text pass for it.
            var content = runText.ToString().TrimEnd();
            if (content.Length == 0 || fg is null)
                return;

            var style = flags.HasFlag(FLAGS.ITALIC) ? FontStyles.Italic : FontStyles.Normal;
            var weight = flags.HasFlag(FLAGS.BOLD) ? FontWeights.Bold : FontWeights.Normal;
            var face = new Typeface(_fontFamily, style, weight, FontStretches.Normal);
            var formatted = new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                face, _fontSize, fg, _pixelsPerDip);
            context.DrawText(formatted, new Point(x, rowY));

            var pen = new Pen(fg, Math.Max(1, _fontSize / 14));
            if (flags.HasFlag(FLAGS.UNDERLINE))
                context.DrawLine(pen, new Point(x, rowY + _cellHeight - pen.Thickness), new Point(x + formatted.Width, rowY + _cellHeight - pen.Thickness));
            if (flags.HasFlag(FLAGS.CrossedOut))
                context.DrawLine(pen, new Point(x, rowY + _cellHeight / 2), new Point(x + formatted.Width, rowY + _cellHeight / 2));
        }
    }

    private void DrawCaret()
    {
        using var dc = _caretVisual.RenderOpen();
        if (_terminal.CursorHidden || _session is null)
            return;

        var buffer = _terminal.Buffer;
        var row = buffer.Y + buffer.YBase - buffer.YDisp;
        if (row < 0 || row >= _rows)
            return;
        var col = Math.Min(buffer.X, _cols - 1);

        var blinkOn = !_terminal.Options.CursorBlink || !IsKeyboardFocused || _caretBlinkVisible;
        if (!blinkOn)
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
                    if (cell.Code > 0 && cell.Width > 0)
                    {
                        var text = cell.Code < 0x10000 ? ((char)cell.Code).ToString() : cell.Rune.ToString();
                        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                            _fontSize, _palette.Background, _pixelsPerDip);
                        dc.DrawText(formatted, new Point(x, y));
                    }
                }
                break;
        }
    }

    private void DrawSelection()
    {
        using var dc = _selectionVisual.RenderOpen();
        if (!_selection.Active)
            return;

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

        // Workspace shortcuts, mirroring the WebView2 backend's keydown hook.
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
            return;

        _selection.StartSelection(row, col);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed && _selection.Active)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            _selection.DragExtend(row, col);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        PasteClipboard();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
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
            => _owner.TitleChanged?.Invoke(title);
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
