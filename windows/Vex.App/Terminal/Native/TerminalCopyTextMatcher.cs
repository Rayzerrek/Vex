using System.Text;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

/// <summary>Visible cell range used by application copy feedback, including wide glyph tails.</summary>
internal readonly record struct TerminalCopyRange(int Row, int StartColumn, int EndColumn);

/// <summary>Locates copied text in the visible terminal without changing the application's selection.</summary>
internal static class TerminalCopyTextMatcher
{
    internal static IReadOnlyList<TerminalCopyRange> FindCopyRanges(FrameRow[] rows, int columns, string copiedText, int cursorRow)
    {
        var lines = copiedText.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
        if (lines.Length == 0 || lines.All(string.IsNullOrWhiteSpace))
            return [];
        var visibleRows = new List<(string Text, List<int> Columns)>();
        for (var row = 0; row < rows.Length; row++)
        {
            var text = new StringBuilder();
            var characterColumns = new List<int>();
            var cells = rows[row].Cells;
            for (var col = 0; col < Math.Min(columns, cells.Length); col++)
            {
                if (cells[col].Tail)
                    continue;
                var cellText = string.IsNullOrEmpty(cells[col].Text) ? " " : cells[col].Text;
                text.Append(cellText);
                for (var i = 0; i < cellText.Length; i++)
                    characterColumns.Add(col);
            }
            visibleRows.Add((text.ToString(), characterColumns));
        }
        var ranges = new List<TerminalCopyRange>();
        // Limit matching work to one viewport even when a whole file is copied.
        foreach (var line in lines.Take(rows.Length))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            TerminalCopyRange? best = null;
            for (var row = 0; row < rows.Length; row++)
            {
                if (ranges.Any(range => range.Row == row))
                    continue;
                var visible = visibleRows[row];
                var index = visible.Text.IndexOf(line, StringComparison.Ordinal);
                if (index < 0)
                    continue;
                var cells = rows[row].Cells;
                var end = visible.Columns[index + line.Length - 1];
                if (cells[end].Wide)
                    end = Math.Min(end + 1, columns - 1);
                var candidate = new TerminalCopyRange(row, visible.Columns[index], end);
                if (best is null || Math.Abs(row - cursorRow) < Math.Abs(best.Value.Row - cursorRow))
                    best = candidate;
            }
            if (best is { } range && !ranges.Any(existing => existing.Row == range.Row))
                ranges.Add(range);
        }
        return ranges;
    }
}
