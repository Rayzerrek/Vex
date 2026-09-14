using System.IO;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

/// <summary>
/// Fuzzy file search drives the Ctrl+P style picker, so ordering is the
/// contract: a query must surface the file the user meant before its weaker
/// matches. These tests pin the scoring behaviour rather than exact scores.
/// </summary>
public sealed class FileSearchEngineTests : IDisposable
{
    private readonly string _root;

    public FileSearchEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vex-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    private void Write(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
    }

    private FileSearchEngine Indexed(params string[] paths)
    {
        foreach (var p in paths)
            Write(p);
        var engine = new FileSearchEngine(_root);
        engine.RebuildIndex();
        return engine;
    }

    [Fact]
    public void BlankQuery_ReturnsNothing()
    {
        var engine = Indexed("Program.cs");
        Assert.Empty(engine.Search(""));
        Assert.Empty(engine.Search("   "));
    }

    [Fact]
    public void SubsequenceMatch_FindsFile()
    {
        var engine = Indexed("TerminalKeyMap.cs");
        var results = engine.Search("tkm");
        Assert.Contains(results, r => r.FileName == "TerminalKeyMap.cs");
    }

    [Fact]
    public void NonSubsequenceQuery_ReturnsNothing()
    {
        var engine = Indexed("TerminalKeyMap.cs");
        Assert.Empty(engine.Search("zzzz"));
    }

    [Fact]
    public void FilenameMatch_OutranksDirectoryOnlyMatch()
    {
        var engine = Indexed(
            "target/src/thing.cs",
            "src/thing.cs");

        var results = engine.Search("thing");
        Assert.NotEmpty(results);
        Assert.Equal("thing.cs", results[0].FileName);
    }

    [Fact]
    public void ConsecutiveRun_BeatsScatteredMatch()
    {
        // Scattered letters with no separator between them isolate the
        // consecutive bonus; separators carry their own boundary bonus that
        // would otherwise dominate the comparison.
        var engine = Indexed("abc.cs", "axbxc.cs");
        var results = engine.Search("abc");

        Assert.Equal(2, results.Count);
        Assert.Equal("abc.cs", results[0].FileName);
    }

    [Fact]
    public void SeparatorBoundary_OutweighsConsecutiveRun()
    {
        // Matching the first letter of each dash-separated word is a stronger
        // signal than matching a plain run, so the acronym wins.
        var engine = Indexed("abc.cs", "a-b-c.cs");
        var results = engine.Search("abc");

        Assert.Equal("a-b-c.cs", results[0].FileName);
    }

    [Fact]
    public void BoundaryMatch_IsBonused()
    {
        var engine = Indexed("src/FileSearchEngine.cs", "src/other/fse.cs");
        var results = engine.Search("fse");

        Assert.Equal("FileSearchEngine.cs", results[0].FileName);
    }

    [Fact]
    public void Index_SkipsIgnoredDirectories()
    {
        var engine = Indexed(
            "src/keep.cs",
            "node_modules/skip.cs",
            "bin/skip.cs",
            "obj/skip.cs",
            ".git/skip.cs");

        var results = engine.Search("skip");
        Assert.Empty(results);
        Assert.Contains(engine.Search("keep"), r => r.FileName == "keep.cs");
    }

    [Fact]
    public void MatchPositions_AreRelativeToFileName()
    {
        var engine = Indexed("src/alpha.cs");
        var result = engine.Search("alpha").Single();

        Assert.Equal("alpha.cs", result.FileName);
        // "alpha" occupies indices 0..4 of the file name, not of the path.
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, result.MatchPositions);
    }

    [Fact]
    public void Limit_TruncatesResults()
    {
        var engine = Indexed(Enumerable.Range(0, 20)
            .Select(i => $"match{i:D2}.cs")
            .ToArray());

        Assert.Equal(5, engine.Search("match", limit: 5).Count);
    }

    [Fact]
    public void Limit_KeepsBestMatchesRatherThanFirstIndexedMatches()
    {
        var engine = Indexed(
            "axbxc.cs",
            "aybyc.cs",
            "abc.cs");

        var result = engine.Search("abc", limit: 1).Single();

        Assert.Equal("abc.cs", result.FileName);
    }

    [Fact]
    public void CancelledSearch_ReturnsWithoutResults()
    {
        var engine = Indexed("match.cs");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Empty(engine.Search("match", cancellationToken: cancellation.Token));
    }

    [Fact]
    public void RelativePath_UsesForwardSlashFreeSeparatorFromRoot()
    {
        var engine = Indexed("src/nested/deep.cs");
        var result = engine.Search("deep").Single();

        Assert.Equal(Path.Combine("src", "nested", "deep.cs"), result.RelativePath);
        Assert.Equal(Path.Combine("src", "nested"), result.DirectoryPart);
    }

    [Fact]
    public void MissingRoot_LeavesIndexEmpty()
    {
        var engine = new FileSearchEngine(Path.Combine(_root, "does-not-exist"));
        engine.RebuildIndex();
        Assert.Empty(engine.Search("anything"));
    }

    [Fact]
    public void RebuildIndex_ReplacesPreviousContents()
    {
        var engine = Indexed("first.cs");
        Assert.NotEmpty(engine.Search("first"));

        File.Delete(Path.Combine(_root, "first.cs"));
        Write("second.cs");
        engine.RebuildIndex();

        Assert.Empty(engine.Search("first"));
        Assert.NotEmpty(engine.Search("second"));
    }
}
