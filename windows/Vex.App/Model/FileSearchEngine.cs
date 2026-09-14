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
    private readonly string _root;
    private List<IndexedFile> _indexedFiles = new();

    private sealed class IndexedFile
    {
        public required string FullPath { get; init; }
        public required string RelativePath { get; init; }
        public required string NormalizedPath { get; init; }
        public required string FileName { get; init; }
        public required string DirectoryPart { get; init; }
        public required int NameStart { get; init; }
    }

    public FileSearchEngine(string root)
    {
        _root = root;
    }

    /// <summary>Enumerates all files under the root, honoring typical ignore
    /// conventions (.git, node_modules, bin/obj). Runs synchronously; call on
    /// a background thread.</summary>
    public void RebuildIndex()
    {
        var files = new List<IndexedFile>();
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

    private void Walk(DirectoryInfo dir, int rootPrefixLen, List<IndexedFile> files)
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
                var fileName = Path.GetFileName(rel);
                files.Add(new IndexedFile
                {
                    FullPath = full,
                    RelativePath = rel,
                    NormalizedPath = Normalize(rel),
                    FileName = fileName,
                    DirectoryPart = Path.GetDirectoryName(rel) ?? "",
                    NameStart = rel.Length - fileName.Length,
                });
            }
        }
    }

    /// <summary>Runs a fuzzy query over the current index. Returns up to
    /// <paramref name="limit"/> best matches, empty when the query is blank.
    /// Work stops early when <paramref name="cancellationToken"/> is cancelled.</summary>
    public List<FileSearchResult> Search(string query, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return new List<FileSearchResult>();

        var normalizedQuery = Normalize(query);
        var best = new PriorityQueue<(IndexedFile File, double Score), double>(limit);
        var files = _indexedFiles;
        for (var i = 0; i < files.Count; i++)
        {
            if ((i & 255) == 0 && cancellationToken.IsCancellationRequested)
                return new List<FileSearchResult>();

            var file = files[i];
            if (!TryScore(file, normalizedQuery, out var score))
                continue;

            if (best.Count < limit)
            {
                best.Enqueue((file, score), score);
            }
            else if (best.TryPeek(out _, out var lowestScore) && score > lowestScore)
            {
                best.Dequeue();
                best.Enqueue((file, score), score);
            }
        }

        var results = new List<FileSearchResult>(best.Count);
        while (best.TryDequeue(out var match, out _))
        {
            var file = match.File;
            results.Add(new FileSearchResult
            {
                FullPath = file.FullPath,
                RelativePath = file.RelativePath,
                FileName = file.FileName,
                DirectoryPart = file.DirectoryPart,
                MatchPositions = GetNameMatchPositions(file, normalizedQuery),
                Score = match.Score,
            });
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

    /// <summary>
    /// Scores a candidate path against the query using subsequence matching.
    /// The indexed lowercase path keeps this loop allocation-free and avoids
    /// Unicode case conversion for every character of every query.
    /// </summary>
    private static bool TryScore(IndexedFile file, string query, out double score)
    {
        score = 0;

        var text = file.RelativePath;
        var normalizedText = file.NormalizedPath;
        var qLen = query.Length;
        var tLen = text.Length;
        if (tLen < qLen)
            return false;

        var qIdx = 0;
        double s = 0;
        var last = -1;
        var first = -1;
        for (var p = 0; p < tLen && qIdx < qLen; p++)
        {
            if (normalizedText[p] != query[qIdx])
                continue;

            if (first < 0)
                first = p;

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
            if (p >= file.NameStart)
                s += 6;

            last = p;
            qIdx++;
        }

        if (qIdx < qLen)
            return false;

        s += Math.Max(0, 40 - tLen) * 0.25;
        s -= first * 0.05;

        score = s;
        return true;
    }

    private static int[] GetNameMatchPositions(IndexedFile file, string query)
    {
        var count = 0;
        var qIdx = 0;
        for (var p = 0; p < file.NormalizedPath.Length && qIdx < query.Length; p++)
        {
            if (file.NormalizedPath[p] != query[qIdx])
                continue;

            if (p >= file.NameStart)
                count++;
            qIdx++;
        }

        var positions = new int[count];
        var dest = 0;
        qIdx = 0;
        for (var p = 0; p < file.NormalizedPath.Length && qIdx < query.Length; p++)
        {
            if (file.NormalizedPath[p] != query[qIdx])
                continue;

            if (p >= file.NameStart)
                positions[dest++] = p - file.NameStart;
            qIdx++;
        }
        return positions;
    }

    private static string Normalize(string value)
    {
        return string.Create(value.Length, value, static (chars, source) =>
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = char.ToLowerInvariant(source[i]);
        });
    }
}
