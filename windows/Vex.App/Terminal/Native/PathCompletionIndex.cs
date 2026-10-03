using System.IO;

namespace Vex.App.Terminal.Native;

/// <summary>Indexes immediate directory entries off the UI thread for local path completion.</summary>
internal sealed class PathCompletionIndex : IDisposable
{
    internal sealed record PathEntry(string FullPath, string RelativePath, bool IsDirectory)
    {
        internal string SearchPath { get; } = RelativePath.ToLowerInvariant();
        internal int FilenameOffset { get; } = RelativePath.LastIndexOf('\\') + 1;
    }
    private const int MaximumEntries = 100_000;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _publicationLock = new();
    private readonly Dictionary<string, (PathEntry[] Entries, DateTime Scanned)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanCancellation;
    private PathEntry[] _entries = [];
    private string _scannedDirectory = "";
    private DateTime _scanCompletedAt;
    internal PathEntry[] Entries => Volatile.Read(ref _entries);
    internal bool IsScanning { get; private set; }
    internal bool IsTruncated { get; private set; }
    internal string? Error { get; private set; }
    internal event Action? SnapshotChanged;

    /// <summary>Publishes cached paths immediately and refreshes stale snapshots in the background.</summary>
    internal void ScanDirectory(string directory)
    {
        lock (_publicationLock)
        {
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }
        var cancellation = _scanCancellation.Token;
        _scannedDirectory = directory;
        _scanCompletedAt = default;
        Error = null;
        IsTruncated = false;
        if (_cache.TryGetValue(directory, out var cached))
        {
            _scanCompletedAt = cached.Scanned;
            Volatile.Write(ref _entries, cached.Entries);
            if (DateTime.UtcNow - cached.Scanned < TimeSpan.FromSeconds(30))
            {
                IsScanning = false;
                SnapshotChanged?.Invoke();
                return;
            }
        }
        else
            Volatile.Write(ref _entries, []);
        IsScanning = true;
        SnapshotChanged?.Invoke();
        _ = ScanPathsAsync(directory, cancellation);
    }

    private async Task ScanPathsAsync(string directory, CancellationToken cancellation)
    {
        try
        {
            await Task.Run(() =>
            {
                var entries = new List<PathEntry>();
                var publishedAt = Environment.TickCount64;
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System,
                };
                // Parent and child directories are scanned only after explicit navigation.
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(new PathEntry(entry.FullName, entry.Name, isDirectory));
                    if (entries.Count >= MaximumEntries)
                        break;
                    if (entries.Count % 2048 == 0 && Environment.TickCount64 - publishedAt >= 100)
                    {
                        PublishSnapshot(entries, cancellation);
                        publishedAt = Environment.TickCount64;
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                PublishSnapshot(entries, cancellation, truncated: entries.Count >= MaximumEntries);
            }, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            lock (_publicationLock)
            {
                if (!cancellation.IsCancellationRequested)
                    Error = e.Message;
            }
        }
        lock (_publicationLock)
        {
            if (cancellation.IsCancellationRequested)
                return;
            IsScanning = false;
            _scanCompletedAt = DateTime.UtcNow;
        }
        SnapshotChanged?.Invoke();
    }

    private void PublishSnapshot(List<PathEntry> entries, CancellationToken cancellation, bool truncated = false)
    {
        var snapshot = entries.ToArray();
        lock (_publicationLock)
        {
            cancellation.ThrowIfCancellationRequested();
            IsTruncated = truncated;
            Volatile.Write(ref _entries, snapshot);
        }
        SnapshotChanged?.Invoke();
    }

    /// <summary>Retains at most four completed directory snapshots; call on the owning UI thread.</summary>
    internal void CacheDirectory(string directory)
    {
        lock (_publicationLock)
        {
            if (IsScanning || Error is not null || IsTruncated || _scanCompletedAt == default || directory != _scannedDirectory)
                return;
            if (_cache.Count >= 4 && !_cache.ContainsKey(directory))
                _cache.Remove(_cache.MinBy(pair => pair.Value.Scanned).Key);
            _cache[directory] = (Entries, _scanCompletedAt);
        }
    }

    /// <summary>Returns only the best fuzzy matches without sorting or allocating for every indexed path.</summary>
    internal static PathEntry[] SearchPaths(PathEntry[] entries, string query, int limit, CancellationToken cancellation = default)
    {
        if (limit <= 0)
            return [];
        query = query.Replace('/', '\\').ToLowerInvariant();
        var matches = new PriorityQueue<PathEntry, (int Score, int Order)>();
        var minimumScore = int.MinValue;
        for (var i = 0; i < entries.Length; i++)
        {
            if ((i & 255) == 0)
                cancellation.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (query.Length == 0 && entry.FilenameOffset != 0)
                continue;
            var score = ScoreIndexedPath(entry.RelativePath, entry.SearchPath, entry.FilenameOffset, query);
            if (score < 0)
                continue;
            if (query.Length == 0 && entry.IsDirectory)
                score += 100;
            var priority = (score, -i);
            if (matches.Count < limit)
            {
                matches.Enqueue(entry, priority);
                if (matches.Count == limit && matches.TryPeek(out _, out var lowest))
                    minimumScore = lowest.Score;
            }
            else if (score > minimumScore)
            {
                matches.EnqueueDequeue(entry, priority);
                matches.TryPeek(out _, out var lowest);
                minimumScore = lowest.Score;
            }
        }
        var result = new PathEntry[matches.Count];
        for (var i = result.Length - 1; i >= 0; i--)
            result[i] = matches.Dequeue();
        return result;
    }

    /// <summary>Rewards contiguous fuzzy matches, filename matches, and word/path boundaries.</summary>
    internal static int ScorePath(string path, string query)
        => ScoreIndexedPath(path, path.ToLowerInvariant(), path.LastIndexOf('\\') + 1, query.Replace('/', '\\').ToLowerInvariant());

    private static int ScoreIndexedPath(string path, string searchPath, int filename, string query)
    {
        if (query.Length == 0)
            return 0;
        var nameScore = ScoreSubsequence(path, searchPath, query, filename);
        var best = nameScore >= 0 ? nameScore + 80 : filename > 0 ? ScoreSubsequence(path, searchPath, query, 0) : -1;
        if (best < 0)
            return -1;
        if (searchPath.AsSpan(filename).StartsWith(query, StringComparison.Ordinal))
            best += 160;
        if (searchPath.AsSpan(filename).Equals(query, StringComparison.Ordinal))
            best += 320;
        return best;
    }

    private static int ScoreSubsequence(string path, string searchPath, string query, int start)
    {
        var previous = -2;
        var score = 0;
        var offset = start;
        foreach (var character in query)
        {
            var found = searchPath.AsSpan(offset).IndexOf(character);
            if (found < 0)
                return -1;
            var i = offset + found;
            score += 20;
            if (i == previous + 1) score += 24;
            if (i == start || path[i - 1] is '\\' or '/' or '-' or '_' or '.' or ' ')
                score += 32;
            if (i > 0 && IsLowercasePathCharacter(path[i - 1]) && IsUppercasePathCharacter(path[i])) score += 16;
            if (previous >= 0) score -= Math.Min(12, i - previous - 1);
            previous = i;
            offset = i + 1;
        }
        return Math.Max(0, score - (path.Length - start) / 4);
    }

    private static bool IsLowercasePathCharacter(char character)
        => character <= 127 ? (uint)(character - 'a') < 26 : char.IsLower(character);

    private static bool IsUppercasePathCharacter(char character)
        => character <= 127 ? (uint)(character - 'A') < 26 : char.IsUpper(character);

    public void Dispose()
    {
        lock (_publicationLock)
        {
            _lifetime.Cancel();
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            _lifetime.Dispose();
        }
        SnapshotChanged = null;
    }
}
