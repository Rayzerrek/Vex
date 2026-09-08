using System.Diagnostics;
using System.IO;

namespace Vex.App.Model;

/// <summary>
/// Startup timing marks, enabled by setting VEX_STARTUP_DIAG to a log path.
/// Zero-cost when unset (a single null check per mark). Used to verify that
/// startup optimizations actually move the first frame earlier.
/// </summary>
internal static class StartupMark
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("VEX_STARTUP_DIAG");
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    internal static void Note(string stage)
    {
        if (LogPath is null)
            return;
        try { File.AppendAllText(LogPath, $"{Clock.Elapsed.TotalMilliseconds:F1}ms {stage}\n"); }
        catch { }
    }
}
