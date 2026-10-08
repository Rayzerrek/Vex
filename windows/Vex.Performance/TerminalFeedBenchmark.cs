using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vex.Libghostty;

internal static class TerminalFeedBenchmark
{
    internal static void Run(string variant)
    {
        var workloads = new[]
        {
            ("feed-ascii", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog\r\n", 1400)))),
            ("feed-ansi", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("\x1b[32mthe quick brown fox\x1b[0m\r\n", 1900)))),
            ("feed-status-flood", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 1000).Select(i => $"\x1b]7501;state=working:app=build:progress={i % 101}:msg=UnVubmluZyB0ZXN0cw==\x1b\\")))),
        };
        foreach (var (name, bytes) in workloads)
        {
            using var terminal = new GhosttyTerminal(160, 50);
            for (var warmup = 0; warmup < 30; warmup++) terminal.Feed(bytes, 0, bytes.Length);
            for (var sample = 0; sample < 5; sample++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                for (var iteration = 0; iteration < 100; iteration++) terminal.Feed(bytes, 0, bytes.Length);
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds / 100;
                Console.WriteLine(JsonSerializer.Serialize(new { variant, name, sample, ms,
                    bytesPerIteration = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 100,
                    inputBytes = bytes.Length, megabytesPerSecond = bytes.Length / ms / 1000 }));
            }
        }
    }
}
