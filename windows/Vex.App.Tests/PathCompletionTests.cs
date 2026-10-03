using System.Diagnostics;
using System.IO;
using Vex.App.Terminal.Native;
using Xunit;
using Xunit.Abstractions;

namespace Vex.App.Tests;

public sealed class PathCompletionTests(ITestOutputHelper output)
{
    private static PathCompletionIndex.PathEntry Entry(string path, bool directory = false) => new(@"C:\project\" + path, path, directory);

    [Fact]
    public void FuzzySearch_RanksFilenameAndContiguousMatchesAheadOfScatteredLetters()
    {
        var paths = new[] { Entry(@"license\source\types.ts"), Entry(@"src\Licensing.ts"), Entry(@"src\License.ts"), Entry("LICENSE"), Entry("other.txt") };
        var result = PathCompletionIndex.SearchPaths(paths, "lic", 3);
        Assert.Equal(new[] { "LICENSE", @"src\License.ts", @"src\Licensing.ts" }, result.Select(entry => entry.RelativePath));
        Assert.DoesNotContain(paths[^1], result);
        Assert.Single(PathCompletionIndex.SearchPaths(paths, "srLicense.ts", 10));
    }

    [Fact]
    public void EmptySearch_ListsImmediateChildrenWithDirectoriesFirst()
    {
        var paths = new[] { Entry("README.md"), Entry(@"src\file.cs"), Entry("src", true), Entry("docs", true) };
        Assert.Equal(new[] { "src", "docs", "README.md" }, PathCompletionIndex.SearchPaths(paths, "", 10).Select(entry => entry.RelativePath));
        Assert.Empty(PathCompletionIndex.SearchPaths(paths, "", 0));
    }

    [Fact]
    public void Search_AcceptsMixedCaseAndForwardSlashQueriesAndHonorsCancellation()
    {
        var paths = new[] { Entry(@"src\NativeTerminalControl.cs"), Entry(@"docs\README.md") };
        Assert.Equal(paths[0], Assert.Single(PathCompletionIndex.SearchPaths(paths, "SRC/ntc", 10)));
        Assert.Equal(paths[1], Assert.Single(PathCompletionIndex.SearchPaths(paths, "readme", 10)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PathCompletionIndex.SearchPaths(paths, "", 10, cancellation.Token));
    }

    [Fact]
    public async Task Index_ListsSkippedDirectoriesButOnlyScansThemWhenNavigatedInto()
    {
        var root = Path.Combine(Path.GetTempPath(), "vex-path-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        File.WriteAllText(Path.Combine(root, "src", "Żółć.cs"), "");
        File.WriteAllText(Path.Combine(root, "node_modules", "package.json"), "");
        try
        {
            using var index = new PathCompletionIndex();
            index.ScanDirectory(root);
            await WaitForIndex(index);
            Assert.Null(index.Error);
            Assert.Contains(index.Entries, entry => entry.RelativePath == @"src\Żółć.cs");
            Assert.Contains(index.Entries, entry => entry.RelativePath == "node_modules" && entry.IsDirectory);
            Assert.DoesNotContain(index.Entries, entry => entry.RelativePath == @"node_modules\package.json");
            var snapshot = index.Entries;
            index.CacheDirectory(root);
            index.ScanDirectory(Path.Combine(root, "node_modules"));
            await WaitForIndex(index);
            Assert.Equal("package.json", Assert.Single(index.Entries).RelativePath);
            index.ScanDirectory(root);
            Assert.False(index.IsScanning);
            Assert.Same(snapshot, index.Entries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Index_RapidDirectoryChangesCannotPublishPreviousDirectoryResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "vex-path-tests", Guid.NewGuid().ToString("N"));
        var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        for (var i = 0; i < 250; i++)
            File.WriteAllText(Path.Combine(first, $"first-{i}.txt"), "");
        File.WriteAllText(Path.Combine(second, "second.txt"), "");
        try
        {
            using var index = new PathCompletionIndex();
            for (var i = 0; i < 20; i++)
            {
                index.ScanDirectory(first);
                index.ScanDirectory(second);
            }
            await WaitForIndex(index);
            Assert.Equal("second.txt", Assert.Single(index.Entries).RelativePath);
            index.ScanDirectory(Path.Combine(root, "missing"));
            await WaitForIndex(index);
            Assert.NotNull(index.Error);
            Assert.Empty(index.Entries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task WaitForIndex(PathCompletionIndex index)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (index.IsScanning)
            await Task.Delay(5, timeout.Token);
    }

    [Theory]
    [InlineData("cmd", @"C:\My files\a.txt", "\"C:\\My files\\a.txt\"")]
    [InlineData("pwsh", @"C:\My files\it's.txt", "'C:\\My files\\it''s.txt'")]
    [InlineData("powershell", @"C:\$HOME\file.txt", "'C:\\$HOME\\file.txt'")]
    [InlineData("nu", @"C:\My files\a.txt", "\"C:\\\\My files\\\\a.txt\"")]
    [InlineData("gitbash", @"C:\My files\it's.txt", "'/c/My files/it'\"'\"'s.txt'")]
    [InlineData("wsl", @"D:\My files\a.txt", "'/mnt/d/My files/a.txt'")]
    public void PathInsertion_QuotesForTheSelectedShell(string shell, string path, string expected) =>
        Assert.Equal(expected, PathCompletionText.FormatPath(path, shell, raw: false));

    [Fact]
    public void RawInsertion_PreservesSpacesAndRejectsControlCharacters()
    {
        Assert.Equal(@"C:\My files\a.txt", PathCompletionText.FormatPath(@"C:\My files\a.txt", "pwsh", raw: true));
        Assert.Throws<ArgumentException>(() => PathCompletionText.FormatPath("C:\\bad\rname.txt", "cmd", raw: true));
        Assert.Equal(@"C:\project\src", PathCompletionText.ResolveDirectory(@"C:\project\docs", "../src/"));
        Assert.Equal(@"D:\src", PathCompletionText.ResolveDirectory(@"C:\project", "D:/src/"));
    }

    [Theory]
    [InlineData(@"PS C:\My files> cat ", "pwsh", @"C:\My files")]
    [InlineData(@"D:\src>cd ..", "cmd", @"D:\src")]
    [InlineData(@"C:\src>echo", "system", @"C:\src")]
    [InlineData(@"some output > text", "cmd", null)]
    [InlineData(@"PS C:\src>", "wsl", null)]
    public void DirectoryFallback_RecognizesOnlyWindowsShellPrompts(string line, string shell, string? expected) =>
        Assert.Equal(expected, PathCompletionText.ReadPromptDirectory(line, shell));

    [Fact]
    public void SearchBenchmark_ReportsLatencyForOneHundredThousandPaths()
    {
        var paths = Enumerable.Range(0, 100_000).Select(i => Entry($@"projects\workspace-{i % 200}\src\NativeTerminalControl-{i}.cs")).ToArray();
        for (var i = 0; i < 3; i++)
            PathCompletionIndex.SearchPaths(paths, "ntc", 50);
        var elapsed = new double[20];
        for (var i = 0; i < elapsed.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            Assert.Equal(50, PathCompletionIndex.SearchPaths(paths, "ntc", 50).Length);
            elapsed[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(elapsed);
        output.WriteLine($"100,000 paths, query 'ntc': median {elapsed[10]:F2} ms, p95 {elapsed[18]:F2} ms (search only).");
    }
}
