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
    private static readonly State? Current = Environment.GetEnvironmentVariable("VEX_STARTUP_DIAG") is { Length: > 0 } path
        ? new State(path)
        : null;

    internal static void Note(string stage) => Current?.Note(stage);

    private sealed class State
    {
        private readonly string _logPath;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _sync = new();
        private readonly List<string> _lines = new();
        private readonly HalfDebouncer _writer;

        internal State(string logPath)
        {
            _logPath = logPath;
            _writer = new HalfDebouncer(TimeSpan.FromMilliseconds(100), WriteSnapshot, leadingEdge: false);
        }

        internal void Note(string stage)
        {
            var line = $"{_clock.Elapsed.TotalMilliseconds:F1}ms {stage}";
            lock (_sync)
                _lines.Add(line);
            // Buffer the burst of startup marks and write them after a quiet
            // period on the timer thread. Diagnostics must not add synchronous
            // disk I/O to the UI path they are measuring.
            _writer.Trigger();
        }

        private void WriteSnapshot()
        {
            string[] snapshot;
            lock (_sync)
                snapshot = _lines.ToArray();
            try { File.WriteAllLines(_logPath, snapshot); }
            catch { }
        }
    }
}
