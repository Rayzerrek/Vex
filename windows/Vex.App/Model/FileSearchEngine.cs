using System.IO;
using System.Windows.Media;

namespace Vex.App.Model;

/// <summary>
/// One file matched by the fuzzy search. Immutable snapshot of the path plus
/// the match positions so the result list can highlight hits without touching
/// the disk.
/// </summary>
public sealed class FileSearchResult
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required string FileName { get; init; }
    public required string DirectoryPart { get; init; }

    /// <summary>Indices into <see cref="FileName"/> of the matched characters.</summary>
    public required int[] MatchPositions { get; init; }

    /// <summary>fzf-style score used for ordering; higher is better.</summary>
    public double Score { get; init; }
}

/// <summary>
/// Fuzzy file search over a project directory, in the spirit of fzf:
/// subsequence matching with bonuses for consecutive runs, camel-case /
/// separator boundaries and matches in the file name. The file list is
/// enumerated once per project (off the UI thread) and reused across queries.
/// </summary>
public sealed class FileSearchEngine
{
    private readonly string _root;
    private List<string> _files = new();

    public FileSearchEngine(string root)
    {
        _root = root;
    }

    /// <summary>Enumerates all files under the root, honoring typical ignore
    /// conventions (.git, node_modules, bin/obj). Runs synchronously; call on
    /// a background thread.</summary>
    public void RebuildIndex()
    {
        var files = new List<string>();
        try
        {
            Walk(new DirectoryInfo(_root), files);
        }
        catch
        {
            // A missing root or an access error leaves the previous index.
        }
        _files = files;
    }

    private void Walk(DirectoryInfo dir, List<string> files)
    {
        // Shallow-first scan: try to get entries once, bail out on access
        // errors instead of throwing per subdirectory.
        FileSystemInfo[] entries;
        try
        {
            entries = dir.GetFileSystemInfos();
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Name.StartsWith(".") ||
                entry.Name == "node_modules" ||
                entry.Name == "bin" ||
                entry.Name == "obj")
                continue;

            if (entry is DirectoryInfo sub)
            {
                Walk(sub, files);
            }
            else
            {
                files.Add(entry.FullName);
            }
        }
    }

    /// <summary>Runs a fuzzy query over the current index. Cheap enough to
    /// call on the UI thread for each keystroke; the index is the expensive
    /// part. Returns up to <paramref name="limit"/> best matches, empty when
    /// the query is blank.</summary>
    public List<FileSearchResult> Search(string query, int limit = 50)
    {
        var results = new List<FileSearchResult>(limit);
        if (string.IsNullOrWhiteSpace(query))
            return results;

        foreach (var file in _files)
        {
            var rel = Path.GetRelativePath(_root, file);
            var match = Score(rel, query);
            if (match == null)
                continue;

            var fileName = Path.GetFileName(rel);
            var nameStart = rel.Length - fileName.Length;
            // Positions relative to the file name so the row can highlight
            // the matched characters in the name line.
            var namePositions = match.Value.Positions
                .Where(p => p >= nameStart)
                .Select(p => p - nameStart)
                .ToArray();

            results.Add(new FileSearchResult
            {
                FullPath = file,
                RelativePath = rel,
                FileName = fileName,
                DirectoryPart = Path.GetDirectoryName(rel) ?? "",
                MatchPositions = namePositions,
                Score = match.Value.Score,
            });
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (results.Count > limit)
            results.RemoveRange(limit, results.Count - limit);
        return results;
    }

    /// <summary>
    /// Scores a candidate path against the query using subsequence matching.
    /// Returns null when the query is not a subsequence of the path.
    /// </summary>
    private static (double Score, int[] Positions)? Score(string text, string query)
    {
        var positions = new int[query.Length];
        var idx = 0;
        for (var i = 0; i < query.Length; i++)
        {
            var ch = char.ToLowerInvariant(query[i]);
            while (idx < text.Length && char.ToLowerInvariant(text[idx]) != ch)
                idx++;
            if (idx == text.Length)
                return null;
            positions[i] = idx;
            idx++;
        }

        double score = 0;
        var last = -1;
        for (var i = 0; i < positions.Length; i++)
        {
            var pos = positions[i];
            // Consecutive matches chain bonuses (fzf's consecutive bonus).
            if (last >= 0 && pos == last + 1)
                score += 8;
            // Boundary bonus: after a slash or a separator, or a camelCase
            // transition — matches at "word starts" are worth more.
            if (pos == 0 ||
                text[pos - 1] == '/' ||
                text[pos - 1] == '\\' ||
                text[pos - 1] == '_' ||
                text[pos - 1] == '-' ||
                text[pos - 1] == '.' ||
                (pos > 0 && char.IsUpper(text[pos]) && char.IsLower(text[pos - 1])))
                score += 16;
            // Matches in the file name (after the last slash) weigh more than
            // in the directory part.
            var lastSlash = text.LastIndexOf('/');
            if (pos > lastSlash)
                score += 6;
            last = pos;
        }

        // Prefer shorter paths and matches that start earlier.
        score += Math.Max(0, 40 - text.Length) * 0.25;
        score -= positions[0] * 0.05;

        // Normalize so scores are comparable across candidates.
        return (score, positions);
    }
}
