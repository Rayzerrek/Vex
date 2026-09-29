using System.Diagnostics;
using System.Text;
using Vex.Libghostty;

foreach (var (name, line) in new[]
{
    ("ascii", "the quick brown fox jumps over the lazy dog "),
    ("unicode", "zażółć gęślą jaźń ─│┌┐└┘漢字 "),
    ("graphemes", "e\u0301 👩\u200D💻 🇵🇱 ")
})
{
    using var terminal = new GhosttyTerminal(160, 50);
    var screen = new StringBuilder("\x1b[H");
    for (var row = 0; row < 50; row++)
    {
        screen.Append($"\x1b[{row + 1};1H");
        for (var repeat = 0; repeat < 3; repeat++)
            screen.Append(line);
    }
    var bytes = Encoding.UTF8.GetBytes(screen.ToString());
    terminal.Feed(bytes, 0, bytes.Length);
    // Force the same cell walk used by dirty frames without input or WPF costs.
    for (var warmup = 0; warmup < 200; warmup++)
    {
        terminal.InvalidateCellCache();
        terminal.UpdateFrame();
    }
    for (var sample = 0; sample < 5; sample++)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var frame = 0; frame < 500; frame++)
        {
            terminal.InvalidateCellCache();
            terminal.UpdateFrame();
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        Console.WriteLine(FormattableString.Invariant($"{name} sample={sample} ms/frame={elapsed.TotalMilliseconds / 500:F3} bytes/frame={(GC.GetAllocatedBytesForCurrentThread() - allocated) / 500}"));
    }
}
