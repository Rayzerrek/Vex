using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class WslProcessTrackerTests
{
    [Fact]
    public void ForegroundApplication_IgnoresBackgroundJobsAndOtherTerminals()
    {
        var app = WslProcessTracker.ParseForegroundProcess("""
            100
            100 1 100 200 bash -bash
            200 100 200 200 python3 python3 -c 'import time; time.sleep(30)'
            300 100 300 200 node node background.js
            400 1 400 401 bash -bash
            401 400 401 401 nvim nvim other-pane.txt
            """);
        Assert.Equal(("python3", "python3 -c 'import time; time.sleep(30)'"), app);
    }

    [Fact]
    public void ForegroundEditor_RetainsIdentityOverLanguageServer()
    {
        var app = WslProcessTracker.ParseForegroundProcess("""
            100
            100 1 100 200 bash -bash
            200 100 200 200 nvim nvim main.ts
            201 200 200 200 node node typescript-language-server.js
            """);
        Assert.Equal(("nvim", "nvim main.ts"), app);
    }

    [Fact]
    public void ShellPrompt_WithBackgroundApplication_HasNoForegroundApplication()
    {
        Assert.Null(WslProcessTracker.ParseForegroundProcess("""
            100
            100 1 100 100 bash -bash
            200 100 200 100 python3 python3 background.py
            """));
    }

    [Theory]
    [InlineData("")]
    [InlineData("error: WSL unavailable")]
    [InlineData("100\ninvalid metadata")]
    [InlineData("100\n100 1 100 -1 bash -bash")]
    public void InvalidProcessMetadata_DoesNotIdentifyAnApplication(string text)
    {
        Assert.Null(WslProcessTracker.ParseForegroundProcess(text));
    }

    [Fact]
    public void WslLaunch_UsesUniqueSessionTokensForDirectAndPrewarmedShells()
    {
        if (!ShellRegistry.HasDetected("wsl"))
            return;
        var direct = ShellLaunchBuilder.BuildShellLaunch("wsl", workingDirectory: @"\\wsl.localhost\Ubuntu\home\user");
        var prewarmed = ShellLaunchBuilder.BuildShellLaunch("wsl", workingDirectory: @"\\wsl.localhost\Ubuntu\home\user");
        var token = WslProcessTracker.SessionToken(direct.Arguments);
        Assert.NotNull(token);
        Assert.NotEqual(token, WslProcessTracker.SessionToken(prewarmed.Arguments));
        Assert.Contains("--distribution Ubuntu", direct.Arguments);
        Assert.Contains("--exec env VEX_WSL_SESSION=", direct.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wsl.exe --cd /home/user")]
    [InlineData("wsl.exe --exec env VEX_WSL_SESSION=invalid")]
    public void MissingOrInvalidSessionToken_IsNotUsedForProcessQueries(string? commandLine)
    {
        Assert.Null(WslProcessTracker.SessionToken(commandLine));
    }
}
