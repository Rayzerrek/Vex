using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private readonly DrawingVisual _pathCompletionVisual = new();
    private PathCompletionIndex? _pathCompletionIndex;
    private CancellationTokenSource? _pathSearchCancellation;
    private bool _pathCompletionOpen;
    private string _pathCompletionDirectory = "";
    private string _pathCompletionQuery = "";
    private string _sessionShellId = "system";
    private PathCompletionIndex.PathEntry[] _pathCompletionMatches = [];
    private int _pathCompletionSelected;
    private int _pathCompletionUpdateQueued;
    private int _pathSearchVersion;
    private string _pathMatchedQuery = "";
    private bool _pathCompletionMouseDown;
    private Rect _pathCompletionBounds;
    private double _pathCompletionRowHeight;
    private int _pathCompletionFirstVisible;
    private (int X, int Y, int RenderVersion, double Width, double Height) _pathCompletionPaintState;

    private void OpenPathCompletion()
    {
        if (_pathCompletionOpen)
        {
            ClosePathCompletion();
            return;
        }
        if (_session is null && RenderSelfTest.ReportPath is null)
            return;
        ClearSelection();
        _pendingTextKey = null;
        _pathCompletionOpen = true;
        if (_pathCompletionIndex is null)
        {
            _pathCompletionIndex = new PathCompletionIndex();
            _pathCompletionIndex.SnapshotChanged += QueuePathCompletionUpdate;
        }
        ChangePathCompletionDirectory(GetPathCompletionDirectory());
    }

    private string GetPathCompletionDirectory()
    {
        if (_terminal.WorkingDirectory is { } reported)
            return reported;
        if (!_terminal.IsAlternateScreen)
        {
            _terminal.UpdateFrame();
            var cursor = _terminal.Cursor;
            for (var row = Math.Min(cursor.Y, _terminal.FrameRows.Length - 1); row >= Math.Max(0, cursor.Y - 20); row--)
            {
                var line = string.Concat(_terminal.FrameRows[row].Cells.Select(cell => cell.Text));
                if (PathCompletionText.ReadPromptDirectory(line, _sessionShellId) is { } directory)
                    return directory;
            }
        }
        return _workingDirectory;
    }

    private void ClosePathCompletion()
    {
        _pathCompletionOpen = false;
        _pathSearchVersion++;
        _pathSearchCancellation?.Cancel();
        _pathCompletionIndex?.CacheDirectory(_pathCompletionDirectory);
        using var dc = _pathCompletionVisual.RenderOpen();
    }

    private void ChangePathCompletionDirectory(string directory)
    {
        _pathCompletionIndex!.CacheDirectory(_pathCompletionDirectory);
        _pathCompletionDirectory = directory;
        _pathCompletionQuery = "";
        _pathCompletionSelected = 0;
        _pathCompletionMatches = [];
        _pathCompletionIndex.ScanDirectory(directory);
        RefreshPathCompletion();
    }

    private void QueuePathCompletionUpdate()
    {
        if (Interlocked.Exchange(ref _pathCompletionUpdateQueued, 1) != 0)
            return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _pathCompletionUpdateQueued, 0);
            if (!_disposed && _pathCompletionOpen)
                RefreshPathCompletion(preserveSelection: true);
        }, DispatcherPriority.Background);
    }

    private async void RefreshPathCompletion(bool preserveSelection = false)
    {
        _pathSearchCancellation?.Cancel();
        _pathSearchCancellation?.Dispose();
        _pathSearchCancellation = new CancellationTokenSource();
        var cancellation = _pathSearchCancellation.Token;
        var version = ++_pathSearchVersion;
        var entries = _pathCompletionIndex!.Entries;
        var query = _pathCompletionQuery;
        DrawPathCompletion();
        try
        {
            // Ranking a large snapshot must never compete with terminal painting on the dispatcher.
            var matches = await Task.Run(() => PathCompletionIndex.SearchPaths(entries, query, 50, cancellation), cancellation);
            if (_disposed || !_pathCompletionOpen || version != _pathSearchVersion)
                return;
            var selectedPath = preserveSelection ? _pathCompletionMatches.ElementAtOrDefault(_pathCompletionSelected)?.FullPath : null;
            _pathCompletionMatches = matches;
            _pathMatchedQuery = query;
            _pathCompletionSelected = Math.Max(0, Array.FindIndex(matches, entry => entry.FullPath == selectedPath));
            DrawPathCompletion();
        }
        catch (OperationCanceledException) { }
    }

    private bool HandlePathCompletionKey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.F && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenPathCompletion();
            return true;
        }
        if (!_pathCompletionOpen)
            return false;
        if (key == Key.Escape)
            ClosePathCompletion();
        else if (key == Key.Enter && (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Control))
            InsertPathCompletion(raw: modifiers == ModifierKeys.Control);
        else if (key is Key.Up or Key.Down or Key.PageUp or Key.PageDown && modifiers == ModifierKeys.None)
        {
            var delta = key switch { Key.Up => -1, Key.Down => 1, Key.PageUp => -8, _ => 8 };
            _pathCompletionSelected = Math.Clamp(_pathCompletionSelected + delta, 0, Math.Max(0, _pathCompletionMatches.Length - 1));
            DrawPathCompletion();
        }
        else if (key == Key.Tab && modifiers == ModifierKeys.None || key == Key.Right && modifiers == ModifierKeys.None)
        {
            if (_pathMatchedQuery == _pathCompletionQuery && _pathCompletionMatches.ElementAtOrDefault(_pathCompletionSelected) is { IsDirectory: true } directory)
                ChangePathCompletionDirectory(directory.FullPath);
            else if (key == Key.Tab)
                InsertPathCompletion(raw: false);
        }
        else if ((key == Key.Tab && modifiers == ModifierKeys.Shift) || (key == Key.Left && modifiers == ModifierKeys.Alt) ||
                 (key == Key.Back && modifiers == ModifierKeys.None && _pathCompletionQuery.Length == 0))
        {
            var parent = Path.GetDirectoryName(_pathCompletionDirectory);
            if (parent is not null)
                ChangePathCompletionDirectory(parent);
        }
        else if (key == Key.Back && modifiers == ModifierKeys.None && _pathCompletionQuery.Length > 0)
        {
            var starts = StringInfo.ParseCombiningCharacters(_pathCompletionQuery);
            _pathCompletionQuery = _pathCompletionQuery[..starts[^1]];
            _pathCompletionSelected = 0;
            RefreshPathCompletion();
        }
        else if (key == Key.Back && modifiers == ModifierKeys.Control)
        {
            _pathCompletionQuery = "";
            RefreshPathCompletion();
        }
        else if (key == Key.V && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (TryGetClipboardText(out var text))
                TypePathCompletionQuery(text);
        }
        else if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 &&
                 !(TerminalKeyMap.IsTextKey(key) && Keyboard.IsKeyDown(Key.RightAlt)))
        {
            ClosePathCompletion();
            return false;
        }
        // Ordinary text arrives through WPF TextInput, including composed/IME characters.
        return !TerminalKeyMap.IsTextKey(key);
    }

    private void TypePathCompletionQuery(string text)
    {
        if (text.Any(char.IsControl))
            return;
        _pathCompletionQuery += text;
        var separator = _pathCompletionQuery.LastIndexOfAny(['/', '\\']);
        if (separator >= 0)
        {
            try
            {
                var prefix = _pathCompletionQuery[..(separator + 1)];
                var query = _pathCompletionQuery[(separator + 1)..];
                ChangePathCompletionDirectory(PathCompletionText.ResolveDirectory(_pathCompletionDirectory, prefix));
                _pathCompletionQuery = query;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Keep malformed input editable; no path has been sent to the shell.
            }
        }
        _pathCompletionSelected = 0;
        RefreshPathCompletion();
    }

    private void InsertPathCompletion(bool raw)
    {
        if (_pathMatchedQuery != _pathCompletionQuery || _pathCompletionMatches.ElementAtOrDefault(_pathCompletionSelected) is not { } entry)
            return;
        string text;
        try
        {
            text = PathCompletionText.FormatPath(entry.FullPath, _sessionShellId, raw || _terminal.IsAlternateScreen);
        }
        catch (ArgumentException) { return; }
        ClosePathCompletion();
        // Paste, without Enter, preserves the application's current input and lets its editor handle the path atomically.
        if (_terminal.BracketedPaste)
            text = "\x1b[200~" + text + "\x1b[201~";
        WriteUserInput(Encoding.UTF8.GetBytes(text));
    }

    private void DrawPathCompletion(bool trackCursorOnly = false)
    {
        var cursor = _terminal.Cursor;
        var paintState = (cursor.X, cursor.Y, _renderVersion, ActualWidth, ActualHeight);
        if (trackCursorOnly && paintState == _pathCompletionPaintState)
            return;
        _pathCompletionPaintState = paintState;
        using var dc = _pathCompletionVisual.RenderOpen();
        if (!_pathCompletionOpen || ActualWidth < 120 || ActualHeight < 96)
            return;
        _pathCompletionRowHeight = Math.Max(22, _cellHeight + 5);
        var count = Math.Min(Math.Max(1, _pathCompletionMatches.Length), Math.Min(10, Math.Max(1, (int)((ActualHeight - 68) / _pathCompletionRowHeight))));
        var height = count * _pathCompletionRowHeight + 62;
        var width = Math.Min(520, ActualWidth - 12);
        var x = Math.Clamp(cursor.X * _cellWidth, 6, Math.Max(6, ActualWidth - width - 6));
        var below = (cursor.Y + 1) * _cellHeight + 4;
        var y = below + height < ActualHeight ? below : Math.Max(4, cursor.Y * _cellHeight - height - 4);
        _pathCompletionBounds = new Rect(x, y, width, height);
        dc.DrawRoundedRectangle(_palette.Background, new Pen(_palette.Link, 1), _pathCompletionBounds, 7, 7);
        dc.PushClip(new RectangleGeometry(_pathCompletionBounds));
        DrawPathCompletionText(dc, _pathCompletionQuery.Length == 0 ? "Search paths…" : "› " + _pathCompletionQuery, x + 12, y + 7, width - 24, _palette.Foreground);
        dc.DrawLine(new Pen(_palette.Selection, 1), new Point(x + 8, y + 31), new Point(x + width - 8, y + 31));
        _pathCompletionFirstVisible = Math.Clamp(_pathCompletionSelected - count + 1, 0, Math.Max(0, _pathCompletionMatches.Length - count));
        for (var row = 0; row < count; row++)
        {
            var index = _pathCompletionFirstVisible + row;
            var rowY = y + 34 + row * _pathCompletionRowHeight;
            if (index >= _pathCompletionMatches.Length)
            {
                var message = _pathCompletionIndex?.Error is not null ? "Cannot read this directory" : _pathCompletionIndex?.IsScanning == true ? "Indexing paths…" : "No matching paths";
                DrawPathCompletionText(dc, message, x + 12, rowY, width - 24, _palette.Foreground);
                break;
            }
            if (index == _pathCompletionSelected)
                dc.DrawRoundedRectangle(_palette.Selection, null, new Rect(x + 5, rowY, width - 10, _pathCompletionRowHeight), 4, 4);
            var entry = _pathCompletionMatches[index];
            DrawPathCompletionText(dc, entry.IsDirectory ? "▸" : "·", x + 12, rowY + 2, 16, _palette.Link);
            DrawPathCompletionText(dc, entry.RelativePath + (entry.IsDirectory ? "\\" : ""), x + 30, rowY + 2, width - 42, _palette.Foreground, _pathCompletionQuery);
        }
        var status = _pathCompletionIndex?.IsScanning == true ? "  • indexing" : _pathCompletionIndex?.IsTruncated == true ? "  • 100k limit" : "";
        DrawPathCompletionText(dc, _pathCompletionDirectory + status, x + 12, y + height - 23, width - 24, _palette.Link);
        dc.Pop();
    }

    private void DrawPathCompletionText(DrawingContext dc, string text, double x, double y, double width, Brush brush, string? query = null)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _normalTypeface,
            Math.Min(_fontSize, 14), brush, _pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };
        if (!string.IsNullOrEmpty(query))
        {
            var matched = 0;
            for (var i = 0; i < text.Length && matched < query.Length; i++)
                if (char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(query[matched]))
                {
                    formatted.SetForegroundBrush(_palette.Link, i, 1);
                    formatted.SetFontWeight(FontWeights.Bold, i, 1);
                    matched++;
                }
        }
        dc.DrawText(formatted, new Point(x, y));
    }

    private bool HandlePathCompletionClick(Point point, int clickCount)
    {
        if (!_pathCompletionOpen)
            return false;
        if (!_pathCompletionBounds.Contains(point))
        {
            ClosePathCompletion();
            return false;
        }
        _pathCompletionMouseDown = true;
        var row = (int)((point.Y - _pathCompletionBounds.Y - 34) / _pathCompletionRowHeight);
        var index = _pathCompletionFirstVisible + row;
        if (_pathMatchedQuery == _pathCompletionQuery && point.Y >= _pathCompletionBounds.Y + 34 &&
            point.Y < _pathCompletionBounds.Bottom - 28 && index < _pathCompletionMatches.Length)
        {
            _pathCompletionSelected = index;
            if (clickCount == 2)
            {
                if (_pathCompletionMatches[index].IsDirectory)
                    ChangePathCompletionDirectory(_pathCompletionMatches[index].FullPath);
                else
                    InsertPathCompletion(raw: false);
            }
            DrawPathCompletion();
        }
        return true;
    }
}
