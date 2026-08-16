using System.IO;

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
    private static readonly char[] PathSeparators = { '/', '\\' };
    private readonly string _root;
    private List<(string FullPath, string RelativePath)> _indexedFiles = new();

    public FileSearchEngine(string root)
    {
        _root = root;
    }

    /// <summary>Enumerates all files under the root, honoring typical ignore
    /// conventions (.git, node_modules, bin/obj). Runs synchronously; call on
    /// a background thread.</summary>
    public void RebuildIndex()
    {
        var files = new List<(string FullPath, string RelativePath)>();
        try
        {
            var rootDir = new DirectoryInfo(_root);
            if (rootDir.Exists)
            {
                var prefixLen = _root.Length + (_root.EndsWith(Path.DirectorySeparatorChar) || _root.EndsWith(Path.AltDirectorySeparatorChar) ? 0 : 1);
                Walk(rootDir, prefixLen, files);
            }
        }
        catch
        {
            // A missing root or an access error leaves the previous index.
        }
        _indexedFiles = files;
    }

    private void Walk(DirectoryInfo dir, int rootPrefixLen, List<(string FullPath, string RelativePath)> files)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = dir.EnumerateFileSystemInfos();
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            var name = entry.Name;
            if (name.StartsWith('.') ||
                name == "node_modules" ||
                name == "bin" ||
                name == "obj")
                continue;

            if (entry is DirectoryInfo sub)
            {
                Walk(sub, rootPrefixLen, files);
            }
            else
            {
                var full = entry.FullName;
                var rel = full.Length >= rootPrefixLen ? full[rootPrefixLen..] : name;
                files.Add((full, rel));
            }
        }
    }

    /// <summary>Runs a fuzzy query over the current index. Cheap enough to
    /// call on the UI thread for each keystroke; the index is the expensive
    /// part. Returns up to <paramref name="limit"/> best matches, empty when
    /// the query is blank.</summary>
    public List<FileSearchResult> Search(string query, int limit = 50)
    {
        var results = new List<FileSearchResult>(Math.Min(limit, 50));
        if (string.IsNullOrWhiteSpace(query))
            return results;

        var files = _indexedFiles;
        for (var i = 0; i < files.Count; i++)
        {
            var (full, rel) = files[i];
            if (!TryScore(rel, query, out var score, out var positions))
                continue;

            var fileName = Path.GetFileName(rel);
            var nameStart = rel.Length - fileName.Length;

            // Extract match positions in the file name without LINQ allocations
            var nameMatchCount = 0;
            for (var p = 0; p < positions.Length; p++)
            {
                if (positions[p] >= nameStart)
                    nameMatchCount++;
            }

            var namePositions = new int[nameMatchCount];
            var dest = 0;
            for (var p = 0; p < positions.Length; p++)
            {
                if (positions[p] >= nameStart)
                    namePositions[dest++] = positions[p] - nameStart;
            }

            results.Add(new FileSearchResult
            {
                FullPath = full,
                RelativePath = rel,
                FileName = fileName,
                DirectoryPart = Path.GetDirectoryName(rel) ?? "",
                MatchPositions = namePositions,
                Score = score,
            });
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (results.Count > limit)
            results.RemoveRange(limit, results.Count - limit);
        return results;
    }

    /// <summary>
    /// Scores a candidate path against the query using subsequence matching.
    /// Fast-path returns false before allocating positions array for non-matches.
    /// </summary>
    private static bool TryScore(string text, string query, out double score, out int[] positions)
    {
        score = 0;
        positions = Array.Empty<int>();

        var qLen = query.Length;
        var tLen = text.Length;
        if (tLen < qLen)
            return false;

        // Subsequence pre-check
        var qIdx = 0;
        for (var t = 0; t < tLen && qIdx < qLen; t++)
        {
            if (char.ToLowerInvariant(text[t]) == char.ToLowerInvariant(query[qIdx]))
                qIdx++;
        }

        if (qIdx < qLen)
            return false;

        var pos = new int[qLen];
        var idx = 0;
        for (var i = 0; i < qLen; i++)
        {
            var ch = char.ToLowerInvariant(query[i]);
            while (idx < tLen && char.ToLowerInvariant(text[idx]) != ch)
                idx++;
            pos[i] = idx++;
        }

        double s = 0;
        var last = -1;
        var lastSlash = text.LastIndexOfAny(PathSeparators);

        for (var i = 0; i < pos.Length; i++)
        {
            var p = pos[i];
            // Consecutive matches chain bonuses (fzf's consecutive bonus).
            if (last >= 0 && p == last + 1)
                s += 8;

            // Boundary bonus: after a slash or a separator, or a camelCase transition
            if (p == 0 ||
                text[p - 1] == '/' ||
                text[p - 1] == '\\' ||
                text[p - 1] == '_' ||
                text[p - 1] == '-' ||
                text[p - 1] == '.' ||
                (p > 0 && char.IsUpper(text[p]) && char.IsLower(text[p - 1])))
            {
                s += 16;
            }

            // Matches in the file name weigh more than in directory part
            if (p > lastSlash)
                s += 6;

            last = p;
        }

        s += Math.Max(0, 40 - tLen) * 0.25;
        s -= pos[0] * 0.05;

        score = s;
        positions = pos;
        return true;
    }
}
