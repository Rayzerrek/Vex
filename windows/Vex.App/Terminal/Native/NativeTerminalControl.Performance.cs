#if DEBUG || VEX_SELFTEST
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private PerformanceSample? _performanceSample;
    private static readonly JsonSerializerOptions PerformanceJsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record SampleDistribution(int Count, double P50, double P95, double P99, double Max);

    private sealed class PerformanceSample
    {
        internal readonly List<double> FlushMilliseconds = new();
        internal readonly List<double> SnapshotMilliseconds = new();
        internal readonly List<double> CompositionIntervals = new();
        internal readonly List<double> InputDelays = new();
        internal int RowsDrawn;
        internal int BusyParserSkips;
    }

    /// <summary>Measures UI latency and WPF callbacks through the live output and scroll paths; not GPU presentation.</summary>
    internal IEnumerable<string> SelfTestBenchSmoothness(string directory, string? neovim)
    {
        SelfTestStabilizeCaret();
        using (var stop = new CancellationTokenSource())
        {
            var outputBytes = 0L;
            var producer = new Thread(() =>
            {
                var sequence = 0;
                while (!stop.IsCancellationRequested)
                {
                    var text = $"\x1b[32mbuild step {sequence++:D8}\x1b[0m the quick brown fox 0123456789\r\n";
                    var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(text, 32)));
                    var buffer = ArrayPool<byte>.Shared.Rent(bytes.Length);
                    bytes.CopyTo(buffer, 0);
                    OnSessionOutput(new ArraySegment<byte>(buffer, 0, bytes.Length));
                    Interlocked.Add(ref outputBytes, bytes.Length);
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            producer.Start();
            try { yield return MeasureSmoothness("output-flood", null, () => Interlocked.Read(ref outputBytes)); }
            finally { stop.Cancel(); producer.Join(); }
        }

        var scrollback = string.Concat(Enumerable.Range(0, 10000)
            .Select(row => $"row {row:D5} the quick brown fox jumps over the lazy dog\r\n"));
        SelfTestFeed("\x1b[?1049l\x1b[2J\x1b[H" + scrollback);
        var scrollTicks = 0;
        yield return MeasureSmoothness("scrollback", () => SelfTestScroll(++scrollTicks % 120 < 60 ? -3 : 3), () => 0);

        if (string.IsNullOrWhiteSpace(neovim) || !File.Exists(neovim))
        {
            yield return JsonSerializer.Serialize(new { scenario = "neovim", skipped = "Neovim executable was not supplied or does not exist." });
            yield break;
        }

        var fixture = Path.Combine(directory, "neovim-fixture.txt");
        File.WriteAllLines(fixture, Enumerable.Range(1, 10000)
            .Select(row => $"VEX_NVIM_{row:D5} the quick brown fox jumps over the lazy dog"));
        SelfTestShell = Vex.Terminal.TerminalSession.DefaultShell();
        SelfTestStartSession();
        WaitForBenchmark(() => _session is not null, "ConPTY session did not start.");
        SelfTestType($"\"{neovim}\" --clean -i NONE -n \"{fixture}\"\r");
        WaitForBenchmark(() => _terminal.IsAlternateScreen
            && _terminal.FrameRows.Any(row => string.Concat(row.Cells.Select(cell => cell.Text)).Contains("VEX_NVIM_", StringComparison.Ordinal)),
            "Neovim did not render the fixture on the alternate screen.");
        var movements = 0;
        yield return MeasureSmoothness("neovim-scroll", () => SelfTestType(++movements % 120 < 60 ? "10j" : "10k"), () => 0);
        SelfTestType("\x1b:q!\r");
        WaitForBenchmark(() => !_terminal.IsAlternateScreen, "Neovim did not exit the alternate screen.");
    }

    private string MeasureSmoothness(string scenario, Action? workloadTick, Func<long> outputBytes)
    {
        var sample = new PerformanceSample();
        var firstBytes = outputBytes();
        var previousComposition = 0L;
        var probePending = 0;
        using var stop = new CancellationTokenSource();
        var probe = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref probePending, 1) == 0)
                {
                    var queued = Stopwatch.GetTimestamp();
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                    {
                        if (ReferenceEquals(_performanceSample, sample))
                            sample.InputDelays.Add(Stopwatch.GetElapsedTime(queued).TotalMilliseconds);
                        Interlocked.Exchange(ref probePending, 0);
                    });
                }
                Thread.Sleep(10);
            }
        }) { IsBackground = true };
        void OnComposition(object? sender, EventArgs args)
        {
            var now = Stopwatch.GetTimestamp();
            if (previousComposition != 0)
                sample.CompositionIntervals.Add(Stopwatch.GetElapsedTime(previousComposition, now).TotalMilliseconds);
            previousComposition = now;
        }
        var frame = new DispatcherFrame();
        var duration = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        duration.Tick += (_, _) => { duration.Stop(); frame.Continue = false; };
        var workload = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        workload.Tick += (_, _) => workloadTick?.Invoke();
        using var process = Process.GetCurrentProcess();
        var firstCpu = process.TotalProcessorTime;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        _performanceSample = sample;
        CompositionTarget.Rendering += OnComposition;
        probe.Start();
        duration.Start();
        if (workloadTick is not null) workload.Start();
        try { Dispatcher.PushFrame(frame); }
        finally
        {
            duration.Stop();
            workload.Stop();
            CompositionTarget.Rendering -= OnComposition;
            _performanceSample = null;
            stop.Cancel();
            probe.Join();
        }
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        return JsonSerializer.Serialize(new
        {
            scenario, seconds = elapsed, inputBytes = outputBytes() - firstBytes,
            flushes = sample.FlushMilliseconds.Count, flushesPerSecond = sample.FlushMilliseconds.Count / elapsed,
            sample.RowsDrawn, sample.BusyParserSkips,
            uiAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
            appCpuMilliseconds = (process.TotalProcessorTime - firstCpu).TotalMilliseconds,
            flushMs = SummarizeSamples(sample.FlushMilliseconds), snapshotMs = SummarizeSamples(sample.SnapshotMilliseconds),
            inputDispatchMs = SummarizeSamples(sample.InputDelays), wpfCallbackIntervalMs = SummarizeSamples(sample.CompositionIntervals),
        }, PerformanceJsonOptions);
    }

    private void WaitForBenchmark(Func<bool> ready, string failure)
    {
        var started = Stopwatch.StartNew();
        while (!ready() && started.Elapsed < TimeSpan.FromSeconds(10))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        if (!ready()) throw new InvalidOperationException(failure);
    }

    private static SampleDistribution SummarizeSamples(List<double> samples)
    {
        samples.Sort();
        double Percentile(double percentile) => samples.Count == 0 ? 0 : samples[(int)Math.Ceiling((samples.Count - 1) * percentile)];
        return new SampleDistribution(samples.Count, Percentile(0.50), Percentile(0.95), Percentile(0.99), Percentile(1));
    }
}
#endif
