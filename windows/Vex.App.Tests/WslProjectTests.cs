using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class WslProjectTests
{
    [Fact]
    public void DistributionList_ParsesWslUnicodeOutputAndRejectsMalformedNames()
    {
        Assert.Equal(new[] { "Ubuntu", "Debian" }, WslProjectLauncher.ParseDistributions("\uFEFFUbuntu\0\r\nDebian\r\nUbuntu\r\n"));
        Assert.Throws<FormatException>(() => WslProjectLauncher.ParseDistributions("bad/name\r\n"));
    }

    [Fact]
    public void ResolvedDirectory_PreservesDistributionSpacesAndUnicode()
    {
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\user\My project Żółć",
            WslProjectLauncher.ParseProjectDirectory("Ubuntu\n/home/user/My project Żółć\n", "Ubuntu"));
        Assert.Equal(@"\\wsl.localhost\Debian\", WslProjectLauncher.ParseProjectDirectory("Debian\n/\n", "Debian"));
    }

    [Theory]
    [InlineData("Ubuntu\nrelative\n")]
    [InlineData("Other\n/home/user\n")]
    [InlineData("Ubuntu\n/home/user\nextra\n")]
    [InlineData("Ubuntu\n/home/user\\bad\n")]
    public void ResolvedDirectory_RejectsInvalidOrAmbiguousOutput(string text) =>
        Assert.Throws<FormatException>(() => WslProjectLauncher.ParseProjectDirectory(text, "Ubuntu"));

    [Fact]
    public void WslProject_AlwaysSelectsWslEvenWhenDefaultShellIsWindows()
    {
        Assert.Equal("wsl", WslShellPaths.ShellIdForDirectory("pwsh", @"\\wsl.localhost\Ubuntu\home\user"));
        Assert.Equal("wsl", WslShellPaths.ShellIdForDirectory("cmd", @"\\wsl$\Debian\"));
        Assert.Equal("pwsh", WslShellPaths.ShellIdForDirectory("pwsh", @"C:\src"));
    }

    [Fact]
    public void LinuxShellDiscovery_DeduplicatesAliasesAndRejectsNonShellExecutables()
    {
        var profiles = WslProjectLauncher.ParseShells("/bin/bash\n/usr/bin/bash\n/usr/bin/zsh\n/home/user/.cargo/bin/nu\n/usr/sbin/nologin\n/bin/bad\nrelative/nu\n/bin/nu\x1b\n");
        Assert.Equal(new[] { "wsl", "wsl:/bin/bash", "wsl:/usr/bin/zsh", "wsl:/home/user/.cargo/bin/nu" }, profiles.Select(profile => profile.Id));
    }

    [Fact]
    public async Task CancelledDiscovery_DoesNotStartWsl()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WslProjectLauncher.ListDistributionsAsync(cancellation.Token));
    }

    [Fact]
    public async Task InstalledDistribution_ResolvesHomeAndReportsMissingDirectory()
    {
        if (Environment.GetEnvironmentVariable("VEX_TEST_UBUNTU") != "1") return;
        var distributions = await WslProjectLauncher.ListDistributionsAsync(CancellationToken.None);
        Assert.Contains("Ubuntu", distributions);
        var shells = await WslProjectLauncher.ListShellsAsync("Ubuntu", CancellationToken.None);
        Assert.Contains(shells, profile => profile.Id == "wsl");
        Assert.Contains(shells, profile => profile.Id.EndsWith("/bash", StringComparison.Ordinal));
        var defaultHome = await WslProjectLauncher.ResolveDefaultHomeAsync(CancellationToken.None);
        Assert.NotNull(WslShellPaths.ParseNetworkPath(defaultHome));
        var home = await WslProjectLauncher.ResolveDirectoryAsync("Ubuntu", "~", CancellationToken.None);
        Assert.StartsWith(@"\\wsl.localhost\Ubuntu\", home);
        Assert.True(System.IO.Directory.Exists(home));
        Assert.Equal(home, await WslProjectLauncher.ResolveDirectoryAsync("Ubuntu", "~/.", CancellationToken.None));
        Assert.Equal(home, await WslProjectLauncher.ResolveDirectoryAsync("Ubuntu", WslShellPaths.ToLinuxPath(home), CancellationToken.None));
        Assert.Equal(@"\\wsl.localhost\Ubuntu\", await WslProjectLauncher.ResolveDirectoryAsync("Ubuntu", "/", CancellationToken.None));
        await Assert.ThrowsAsync<System.IO.IOException>(() => WslProjectLauncher.ResolveDirectoryAsync("Ubuntu", "/__vex_missing_directory__", CancellationToken.None));
    }
}
