using System.IO;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class AppCrashLogTests
{
    [Fact]
    public void WriteCrashLog_RecordsFailureAndReplacesPreviousReport()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vex-crash-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppCrashLog.WriteCrashLog(directory, "dispatcher", new InvalidOperationException("first failure"));
            var path = Path.Combine(directory, "crash.log");
            var first = File.ReadAllText(path);
            Assert.Contains("dispatcher", first);
            Assert.Contains("System.InvalidOperationException: first failure", first);
            AppCrashLog.WriteCrashLog(directory, "app-domain", new Exception(new string('x', 100_000)));
            var latest = File.ReadAllText(path);
            Assert.DoesNotContain("first failure", latest);
            Assert.Contains("[truncated]", latest);
            Assert.True(latest.Length < 34_000);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

#if DEBUG
    [Fact]
    public async Task FatalDispatcherException_ProducesLogAndStillTerminatesProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vex-fatal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var host = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vex.RendererSelfTestHost.txt")).Trim();
        var start = new System.Diagnostics.ProcessStartInfo(host) { UseShellExecute = false };
        foreach (var name in start.Environment.Keys.Where(static name => name.StartsWith("VEX_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        start.Environment["VEX_PROFILE_DIR"] = directory;
        start.Environment["VEX_SELFTEST"] = Path.Combine(directory, "report.txt");
        start.Environment["VEX_SELFTEST_FATAL"] = "1";
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"ShellId\":\"cmd\"}");
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, process.ExitCode);
            var log = File.ReadAllText(Path.Combine(directory, "crash.log"));
            Assert.Contains("dispatcher", log);
            Assert.Contains("Vex fatal exception self-test", log);
            Assert.False(File.Exists(Path.Combine(directory, "report.txt")));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            Directory.Delete(directory, recursive: true);
        }
    }
#endif

    [Fact]
    public void WriteCrashLog_UnwritableDestinationDoesNotThrow()
    {
        var file = Path.GetTempFileName();
        try
        {
            var failure = Record.Exception(() => AppCrashLog.WriteCrashLog(file, "dispatcher", new Exception("original")));
            Assert.Null(failure);
        }
        finally { File.Delete(file); }
    }
}
