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
    public async Task WpfRenderer_ScriptedFramesHaveNoRenderingFailures(bool incremental)
    {
        var hostPath = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vex.RendererSelfTestHost.txt")).Trim();
        Assert.True(File.Exists(hostPath), $"Renderer self-test app not found: {hostPath}");
        var artifactRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vex-renderer-tests"));
        var directory = Path.Combine(artifactRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "report.txt");
        var startInfo = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        // A test owns its workspace and shell profile; it must never restore
        // or overwrite the user's running terminal session.
        foreach (var variable in startInfo.Environment.Keys.Where(key => key.StartsWith("VEX_LIVE", StringComparison.Ordinal)).ToArray())
            startInfo.Environment.Remove(variable);
        startInfo.Environment.Remove("VEX_SELFTEST_BENCH");
        startInfo.Environment["VEX_SELFTEST"] = reportPath;
        startInfo.Environment["VEX_SELFTEST_INCR"] = incremental ? "1" : "0";
        startInfo.Environment["VEX_PROFILE_DIR"] = Path.Combine(directory, "profile");
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
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) == artifactRoot)
            Directory.Delete(directory, recursive: true);
    }
}
#endif
