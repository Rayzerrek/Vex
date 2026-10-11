using System.Text;
using Vex.Libghostty;
using Xunit;

namespace Vex.Libghostty.Tests;

public sealed class WorkingDirectoryTests
{
    [Theory]
    [InlineData("\x07")]
    [InlineData("\x1b\\")]
    public void Osc7_AcceptsLocalUriAcrossEveryPossibleChunkBoundary(string terminator)
    {
        var uri = new Uri(@"C:\My files\Żółć").AbsoluteUri;
        var bytes = Encoding.UTF8.GetBytes("\x1b]7;" + uri + terminator);
        for (var split = 1; split < bytes.Length; split++)
        {
            using var term = new GhosttyTerminal(80, 24);
            term.Feed(bytes, 0, split);
            term.Feed(bytes, split, bytes.Length - split);
            Assert.Equal(@"C:\My files\Żółć", term.WorkingDirectory);
        }
    }

    [Theory]
    [InlineData("file://linux-host/home/user/My%20project", @"\\wsl.localhost\Ubuntu\home\user\My project")]
    [InlineData("file:///home/user/%C5%BB%C3%B3%C5%82%C4%87", @"\\wsl.localhost\Ubuntu\home\user\Żółć")]
    [InlineData("file:///", @"\\wsl.localhost\Ubuntu\")]
    public void Osc7_WslDirectoryMapsToItsDistributionAcrossChunkBoundaries(string uri, string expected)
    {
        var bytes = Encoding.UTF8.GetBytes("\x1b]7;" + uri + "\x1b\\");
        for (var split = 1; split < bytes.Length; split++)
        {
            using var terminal = new GhosttyTerminal(80, 24, "Ubuntu");
            terminal.Feed(bytes, 0, split);
            terminal.Feed(bytes, split, bytes.Length - split);
            Assert.Equal(expected, terminal.WorkingDirectory);
        }
    }

    [Fact]
    public void Osc7_WslDirectoryRejectsWindowsAndControlPaths()
    {
        using var terminal = new GhosttyTerminal(80, 24, "Ubuntu");
        terminal.Feed("\x1b]7;file:///home/user\x07");
        foreach (var uri in new[] { "file:///C:/project", "file:///home/bad%0Apath", "file:///home/bad%5Cpath", "https://example.org" })
        {
            terminal.Feed("\x1b]7;" + uri + "\x07");
            Assert.Equal(@"\\wsl.localhost\Ubuntu\home\user", terminal.WorkingDirectory);
        }
    }

    [Fact]
    public void Osc7_LocalHostnamesResolveToLocalDrivesInsteadOfUncShares()
    {
        using var term = new GhosttyTerminal(80, 24);
        foreach (var host in new[] { "localhost", Environment.MachineName })
        {
            term.Feed("\x1b]7;file://" + host + "/C:/My%20files\x07");
            Assert.Equal(@"C:\My files", term.WorkingDirectory);
        }
    }

    [Fact]
    public void Osc7_RejectsRemoteAndMalformedDirectoriesWithoutLosingTheLastLocalPath()
    {
        using var term = new GhosttyTerminal(80, 24);
        term.Feed("\x1b]7;file:///C:/project\x07");
        foreach (var value in new[] { "file://remote-host/C:/other", "https://example.org", "invalid", "file:///C:/bad%0Apath" })
        {
            term.Feed("\x1b]7;" + value + "\x07");
            Assert.Equal(@"C:\project", term.WorkingDirectory);
        }
        term.UpdateFrame();
        Assert.All(term.FrameRows, row => Assert.All(row.Cells, cell => Assert.True(string.IsNullOrWhiteSpace(cell.Text))));
    }
}
