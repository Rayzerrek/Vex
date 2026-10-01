#if DEBUG
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Vex.App.Tests;

public sealed class TerminalRendererRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task WpfRenderer_ScriptedFramesHaveNoRenderingFailures(bool incremental) => RunRendererSelfTest(incremental);

    [Theory]
    [InlineData("{\"ShellId\":\"cmd\"}", "{\"Windows\":[{\"Projects\":[null]}]}")]
    [InlineData("{\"ShellId\":\"cmd\"}", "{\"Windows\":[{\"Projects\":[{\"Tabs\":null}]}]}")]
    [InlineData("{\"ShellId\":\"cmd\",\"CustomShells\":null}", "{\"Windows\":null}")]
    public Task WpfStartup_CorruptProfileStillRendersTerminal(string settings, string session) =>
        RunRendererSelfTest(incremental: false, settings, session);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RendererEnvironment_IgnoresInheritedSelfTestModes(bool incremental)
    {
        var start = new ProcessStartInfo("unused-selftest-host");
        start.Environment["VEX_SELFTEST_FATAL"] = "1";
        start.Environment["VEX_SELFTEST_CLIPBOARD"] = "1";
        start.Environment["VEX_SELFTEST_REPLAY"] = "unrelated-replay.bin";
        start.Environment["VEX_LIVE"] = "1";
        ConfigureRendererEnvironment(start, "report.txt", "test-profile", incremental);
        var keys = start.Environment.Keys.Where(static key => key.StartsWith("VEX_", StringComparison.Ordinal)).Order().ToArray();
        Assert.Equal(new[] { "VEX_PROFILE_DIR", "VEX_SELFTEST", "VEX_SELFTEST_INCR" }, keys);
        Assert.Equal(incremental ? "1" : "0", start.Environment["VEX_SELFTEST_INCR"]);
    }

    private static void ConfigureRendererEnvironment(ProcessStartInfo startInfo, string reportPath, string profile, bool incremental)
    {
        // Inherited diagnostics must not switch scenarios or touch the user's
        // clipboard/profile. Opt into only the mode this test actually asserts.
        foreach (var variable in startInfo.Environment.Keys.Where(static key => key.StartsWith("VEX_", StringComparison.Ordinal)).ToArray())
            startInfo.Environment.Remove(variable);
        startInfo.Environment["VEX_SELFTEST"] = reportPath;
        startInfo.Environment["VEX_SELFTEST_INCR"] = incremental ? "1" : "0";
        startInfo.Environment["VEX_PROFILE_DIR"] = profile;
    }

    private static async Task RunRendererSelfTest(bool incremental, string? settings = null, string? session = null)
    {
        var hostPath = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vex.RendererSelfTestHost.txt")).Trim();
        Assert.True(File.Exists(hostPath), $"Renderer self-test app not found: {hostPath}");
        var artifactRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vex-renderer-tests"));
        var directory = Path.Combine(artifactRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var profile = Path.Combine(directory, "profile");
        if (settings is not null)
        {
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "settings.json"), settings);
            File.WriteAllText(Path.Combine(profile, "session.json"), session!);
        }
        var reportPath = Path.Combine(directory, "report.txt");
        var startInfo = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        ConfigureRendererEnvironment(startInfo, reportPath, profile, incremental);
        using var process = Process.Start(startInfo)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Renderer self-test timed out; artifacts: {directory}");
        }
        var report = File.Exists(reportPath) ? File.ReadAllText(reportPath) : "Renderer report was not created";
        Assert.True(process.ExitCode == 0, $"Renderer self-test exited with {process.ExitCode}; artifacts: {directory}\n{report}");
        Assert.EndsWith("done", report.TrimEnd());
        Assert.DoesNotContain("FAIL", report);
        Assert.DoesNotContain("EXCEPTION", report);
        Assert.StartsWith(incremental ? "incr start cols=" : "start cols=", report);
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) == artifactRoot)
            Directory.Delete(directory, recursive: true);
    }
}
#endif
