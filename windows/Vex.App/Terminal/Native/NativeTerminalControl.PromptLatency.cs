#if DEBUG || VEX_SELFTEST
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private Action? _promptPainted;

    /// <summary>Measures synthetic PTY input to a painted prompt row, before and after a real Neovim session; not GPU presentation.</summary>
    internal IEnumerable<string> SelfTestBenchPromptLatency()
    {
        SelfTestStabilizeCaret();
        SelfTestStartSession();
        WaitForBenchmark(() => _session is not null && !_sessionStarting, "Prompt benchmark session did not start.");
        WriteUserInput("echo VEX_PROMPT_READY\r"u8);
        WaitForBenchmark(() => _terminal.FrameRows.Any(row => string.Concat(row.Cells.Select(cell => cell.Text)).Contains("VEX_PROMPT_READY", StringComparison.Ordinal)),
            "Shell did not paint the readiness marker.");
        PumpPromptBenchmark(TimeSpan.FromMilliseconds(500));
        yield return MeasurePromptTyping("prompt-before-neovim");

        WriteUserInput("nvim --clean -i NONE -n\r"u8);
        WaitForBenchmark(() => _terminal.IsAlternateScreen, "Neovim did not enter the alternate screen.");
        PumpPromptBenchmark(TimeSpan.FromMilliseconds(500));
        WriteUserInput("\x1b:q!\r"u8);
        WaitForBenchmark(() => !_terminal.IsAlternateScreen, "Neovim did not leave the alternate screen.");
        yield return MeasurePromptTyping("prompt-after-neovim");
    }

    private string MeasurePromptTyping(string scenario)
    {
        WriteUserInput("\x03"u8);
        PumpPromptBenchmark(TimeSpan.FromMilliseconds(300));
        var samples = new List<double>();
        var text = new StringBuilder();
        foreach (var character in "#VEX_PROMPT_LATENCY_0123456789")
        {
            text.Append(character);
            var expected = text.ToString();
            var started = Stopwatch.GetTimestamp();
            WriteUserInput(Encoding.UTF8.GetBytes(character.ToString()));
            WaitForPaintedPrompt(expected);
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        WriteUserInput("\x03"u8);
        return JsonSerializer.Serialize(new
        {
            scenario,
            shell = _sessionShellId,
            columns = _cols,
            rows = _rows,
            synchronizedOutput = _terminal.SynchronizedOutput,
            inputToPaintMs = SummarizeSamples(samples),
        }, PerformanceJsonOptions);
    }

    private bool HasPaintedPromptText(string text)
    {
        var row = _terminal.Cursor.Y;
        if (row >= _terminal.FrameRows.Length || row >= _rowHashes.Length)
            return false;
        var frame = _terminal.FrameRows[row];
        return string.Concat(frame.Cells.Select(cell => cell.Text)).Contains(text, StringComparison.Ordinal)
            && _rowVersions[row] == _renderVersion && _rowHashes[row] == RowHash(frame, _cols);
    }

    private void WaitForPaintedPrompt(string text)
    {
        var frame = new DispatcherFrame();
        var painted = HasPaintedPromptText(text);
        if (painted)
            return;
        var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        timer.Tick += (_, _) => frame.Continue = false;
        _promptPainted = () =>
        {
            if (HasPaintedPromptText(text))
            {
                painted = true;
                frame.Continue = false;
            }
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally
        {
            timer.Stop();
            _promptPainted = null;
        }
        if (!painted)
            throw new TimeoutException($"Prompt benchmark did not paint '{text}' within five seconds.");
    }

    private void PumpPromptBenchmark(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = duration };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }
}
#endif
