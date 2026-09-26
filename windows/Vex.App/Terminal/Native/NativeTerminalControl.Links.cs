using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
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

    private readonly record struct LinkSpan(int StartCol, int EndCol, int TextStart, int TextLength);

    private void RebuildLinkPen()
    {
        var pen = new Pen(_palette.Link, 1);
        pen.Freeze();
        _linkPen = pen;
    }

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
}
