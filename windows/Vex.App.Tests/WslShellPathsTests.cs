using Vex.App.Model;
using Vex.App.Terminal.Native;
using Xunit;

namespace Vex.App.Tests;

public sealed class WslShellPathsTests
{
    [Fact]
    public void WslTransportProcessesDoNotRequireCloseConfirmation()
    {
        var index = Vex.Terminal.ProcessTree.Index.Build(new[]
        {
            (Pid: 10u, ParentPid: 1u, Name: "wsl"),
            (Pid: 20u, ParentPid: 10u, Name: "wslrelay"),
            (Pid: 30u, ParentPid: 20u, Name: "wslhost"),
        });
        Assert.NotNull(index);
        Assert.Null(Vex.Terminal.ProcessTree.DeepestChildProcess(index, 10, WslShellPaths.ShellHelpers));
    }

    [Fact]
    public void UserApplicationsUnderWslStillRequireCloseConfirmation()
    {
        var index = Vex.Terminal.ProcessTree.Index.Build(new[]
        {
            (Pid: 10u, ParentPid: 1u, Name: "wsl"),
            (Pid: 20u, ParentPid: 10u, Name: "wslrelay"),
            (Pid: 30u, ParentPid: 20u, Name: "editor"),
        });
        Assert.NotNull(index);
        Assert.Equal(("editor", 30u), Vex.Terminal.ProcessTree.DeepestChildProcess(index, 10, WslShellPaths.ShellHelpers));
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\user\My project", "Ubuntu", "/home/user/My project")]
    [InlineData(@"\\wsl$\Debian\home\user", "Debian", "/home/user")]
    [InlineData(@"\\WSL.LOCALHOST\Ubuntu", "Ubuntu", "/")]
    public void NetworkFoldersPreserveDistributionAndLinuxDirectory(string path, string distribution, string directory)
    {
        var parsed = WslShellPaths.ParseNetworkPath(path);
        Assert.NotNull(parsed);
        Assert.Equal(distribution, parsed.Value.Distribution);
        Assert.Equal(directory, parsed.Value.LinuxPath);
    }

    [Theory]
    [InlineData("\\\\wsl.localhost\\Ubuntu:\\home\\user")]
    [InlineData("\\\\wsl.localhost\\Ubuntu\n\\home\\user")]
    public void CorruptNetworkDistribution_CannotReachTheTerminalConstructor(string path) =>
        Assert.Null(WslShellPaths.ParseNetworkPath(path));

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\user\My project", "'/home/user/My project'")]
    [InlineData(@"D:\My project", "'/mnt/d/My project'")]
    public void CompletionInsertsLinuxPaths(string path, string expected)
    {
        Assert.Equal(expected, PathCompletionText.FormatPath(path, "wsl", false));
    }

    [Theory]
    [InlineData("wsl:/bin/bash")]
    [InlineData("wsl:/usr/bin/nu")]
    public void ExplicitLinuxShell_IsAnArgumentNotInterpolatedIntoTheBootstrap(string shellId)
    {
        if (!ShellRegistry.HasDetected("wsl")) return;
        var launch = ShellLaunchBuilder.BuildShellLaunch(shellId, workingDirectory: @"\\wsl.localhost\Ubuntu\home\user");
        Assert.Contains("--distribution Ubuntu", launch.Arguments);
        Assert.Contains("sh -c \"exec \\\"$1\\\" -l\" vex-shell", launch.Arguments);
        Assert.EndsWith("\"" + shellId[4..] + "\"", launch.Arguments);
        Assert.NotNull(WslProcessTracker.SessionToken(launch.Arguments));
    }

    [Theory]
    [InlineData("wsl:/bin/bash", "'/home/user/O'\"'\"'Brien'")]
    [InlineData("wsl:/usr/bin/nu", "\"/home/user/O'Brien\"")]
    [InlineData("wsl:/usr/bin/pwsh", "'/home/user/O''Brien'")]
    public void LinuxCompletion_UsesTheChosenShellQuoting(string shellId, string expected) =>
        Assert.Equal(expected, PathCompletionText.FormatPath(@"\\wsl.localhost\Ubuntu\home\user\O'Brien", shellId, false));

    [Fact]
    public void LaunchSelectsDistributionAndQuotesProjectDirectory()
    {
        if (!ShellRegistry.HasDetected("wsl"))
            return;
        var launch = ShellLaunchBuilder.BuildShellLaunch("wsl", workingDirectory: @"\\wsl.localhost\Ubuntu\home\user\My project");
        Assert.StartsWith("--distribution Ubuntu --cd \"/home/user/My project\" --exec env VEX_WSL_SESSION=", launch.Arguments);
        Assert.NotNull(WslProcessTracker.SessionToken(launch.Arguments));
        Assert.EndsWith("sh -c \"exec \\\"${SHELL:-/bin/sh}\\\" -l\"", launch.Arguments);
    }
}
