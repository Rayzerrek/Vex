#if DEBUG || VEX_SELFTEST
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Vex.App.Model;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

/// <summary>
/// Deterministic renderer self-test, enabled by setting VEX_SELFTEST to a
/// report path. Instead of driving the shell through ConPTY, it feeds the
/// emulator scripted VT output directly — the same pump/flush pipeline the
/// live shell uses — then renders the control to a bitmap and checks the
/// pixels: a full screen of text, a clean screen after ED2 (no ghost rows),
/// wide-character rendering, and pixel-identical snap-back after scrolling.
/// </summary>
internal static partial class RenderSelfTest
{
    public static string? ReportPath { get; } = Environment.GetEnvironmentVariable("VEX_SELFTEST");

    /// <summary>Set VEX_LIVE=1 to drive a real ConPTY session (nvim pum open,
    /// Tab accept, close) inside the app instead of the deterministic replay —
    /// the full async pump path, pixel-checked against the buffer.</summary>
    public static bool LiveMode { get; } = Environment.GetEnvironmentVariable("VEX_LIVE") == "1";

    /// <summary>With VEX_LIVE=1, run the output-flood stress scenario (nu +
    /// gh help) instead of the nvim pum scenario.</summary>
    public static bool StressMode { get; } = Environment.GetEnvironmentVariable("VEX_LIVE_STRESS") == "1";

    /// <summary>With VEX_LIVE=1, run the enter-TUI/exit/type scenario that
    /// reproduces the duplicate-prompt bug.</summary>
    public static bool PromptMode { get; } = Environment.GetEnvironmentVariable("VEX_LIVE_PROMPT") == "1";
    private static readonly string? LiveDir = Environment.GetEnvironmentVariable("VEX_LIVE_DIR");
    private static readonly string? LiveShell = Environment.GetEnvironmentVariable("VEX_LIVE_SHELL");

    /// <summary>With VEX_LIVE=1, open this file in nvim and type a function
    /// into it instead of running the words-file pum scenario. The session's
    /// working directory is the pane's own.</summary>
    public static string? LiveFile { get; } = Environment.GetEnvironmentVariable("VEX_LIVE_FILE");

    /// <summary>Run only the deterministic incremental-repaint scenario
    /// (<see cref="RunIncrementalParity"/>): streamed output, scrolls, and
    /// grid resizes, each checked for pixels the incremental paint left
    /// stale.</summary>
    public static bool IncrementalMode { get; } = Environment.GetEnvironmentVariable("VEX_SELFTEST_INCR") == "1";
    private static bool RendererBenchMode { get; } = Environment.GetEnvironmentVariable("VEX_SELFTEST_BENCH") == "1";
    private static bool _failed;
    private static bool _runScheduled;

    /// <summary>With VEX_LIVE=1, run the nvim mouse round-trip instead: mouse
    /// tracking must engage from nvim's own output, a press-drag-release must
    /// select in nvim's Visual mode, and d must delete the selection.</summary>
    public static bool MouseLiveMode { get; } = Environment.GetEnvironmentVariable("VEX_LIVE_MOUSE") == "1";

    public static void Run(NativeTerminalControl control)
    {
        if (string.IsNullOrEmpty(ReportPath) || _runScheduled)
            return;
        // UI scenarios realize temporary tabs and splits. They must not
        // schedule another scenario while the first is pumping the dispatcher.
        _runScheduled = true;

        var t = new DispatcherTimer(DispatcherPriority.ApplicationIdle, control.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        t.Tick += (_, _) =>
        {
            if (control.SelfTestCols == 0 || control.SelfTestRows == 0)
                return;
            t.Stop();
            if (Environment.GetEnvironmentVariable("VEX_SELFTEST_FATAL") == "1")
            {
                // Run outside the harness catch so the real fatal handlers
                // log the exception without turning it into a passing report.
                _ = control.Dispatcher.BeginInvoke(() => throw new InvalidOperationException("Vex fatal exception self-test"));
                return;
            }
            try
            {
                if (LiveMode)
                {
                    // Live scenarios drive their own step timers and shut
                    // the app down from their last step (or below on error);
                    // they return immediately after scheduling.
                    try
                    {
                        if (PromptMode)
                            RunLivePrompt(control);
                        else if (MouseLiveMode)
                            RunLiveMouse(control);
                        else if (StressMode)
                            RunStress(control);
                        else if (!string.IsNullOrEmpty(LiveFile))
                            RunLiveFile(control, LiveFile);
                        else
                            RunLive(control);
                    }
                    catch (Exception e)
                    {
                        Report(control, $"EXCEPTION {e}");
                        ShutdownSelfTest();
                    }
                    return;
                }
                if (Environment.GetEnvironmentVariable("VEX_SELFTEST_PROMPT_LATENCY") == "1")
                {
                    foreach (var sample in control.SelfTestBenchPromptLatency())
                        Report(control, sample);
                    Report(control, "done");
                }
                else if (Environment.GetEnvironmentVariable("VEX_SELFTEST_CLIPBOARD") == "1")
                    RunClipboardSelection(control);
                else if (Environment.GetEnvironmentVariable("VEX_SELFTEST_SMOOTHNESS") == "1")
                {
                    var directory = Path.GetDirectoryName(Path.GetFullPath(ReportPath)) ?? Path.GetTempPath();
                    foreach (var sample in control.SelfTestBenchSmoothness(directory, Environment.GetEnvironmentVariable("VEX_PERF_NVIM")))
                        Report(control, sample);
                    Report(control, "done");
                }
                else if (RendererBenchMode)
                    RunRendererBench(control);
                else if (IncrementalMode)
                    RunIncrementalParity(control);
                else if (Environment.GetEnvironmentVariable("VEX_SELFTEST_STARTUP") == "1")
                    RunStartupOnly(control);
                else
                    RunCore(control);
            }
            catch (Exception e)
            {
                Report(control, $"EXCEPTION {e}");
            }
            finally
            {
                // Self-test runs are disposable: close the app once the
                // report is written so the caller knows it finished.
                if (!LiveMode)
                    ShutdownSelfTest();
            }
        };
        t.Start();
    }

    private static void Shot(NativeTerminalControl control, string path)
    {
        try
        {
            DumpPng(control, Capture(control), path);
        }
        catch (Exception e)
        {
            Report(control, $"shot-EXCEPTION {e.Message}");
        }
    }

    private static void Report(NativeTerminalControl c, string line)
    {
        if (line.StartsWith("FAIL", StringComparison.Ordinal) || line.Contains("EXCEPTION", StringComparison.Ordinal))
            _failed = true;
        try { File.AppendAllText(ReportPath!, $"{line}\n"); }
        catch { _failed = true; }
    }

    private static void ShutdownSelfTest() => Application.Current.Shutdown(_failed ? 1 : 0);

    private static void RunRendererBench(NativeTerminalControl control)
    {
        control.SelfTestStabilizeCaret();
        Report(control, $"bench grid={control.SelfTestCols}x{control.SelfTestRows} cell={control.SelfTestCellWidth:F3}x{control.SelfTestCellHeight:F3} dpi={VisualTreeHelper.GetDpi(control).PixelsPerDip} font={AppSettings.Instance.FontFamily} size={AppSettings.Instance.FontSize}");
        foreach (var (name, text) in new[]
        {
            ("ascii", "the quick brown fox jumps over the lazy dog 0123456789 "),
            ("ansi", "\u001b[31mred\u001b[0m \u001b[32mgreen\u001b[0m \u001b[1;34mbold\u001b[0m "),
            ("graphemes", "e\u0301 \U0001F9EA tail \U0001F469\u200D\U0001F4BB "),
        })
        {
            var screen = new StringBuilder("\u001b[2J");
            for (var row = 1; row <= control.SelfTestRows; row++)
            {
                screen.Append($"\u001b[{row};1H");
                for (var repeat = 0; repeat < 3; repeat++)
                    screen.Append(text);
            }
            control.SelfTestFeed(screen.ToString());
            for (var sample = 0; sample < 3; sample++)
                Report(control, $"{name} sample={sample} {control.SelfTestBenchRendering(100)}");
        }
        Report(control, "done");
    }

    private static void RunClipboardSelection(NativeTerminalControl control)
    {
        CheckClipboardSelection(control);
        control.SelfTestFeed("\x1b[?1049h\x1b[?1000h\x1b[?1006h\x1b[48;2;35;55;75m");
        try
        {
            CheckClipboardSelection(control);
        }
        finally
        {
            control.SelfTestFeed("\x1b[0m\x1b[?1000l\x1b[?1006l\x1b[?1049l");
        }
        Report(control, "done");
    }

    private static void CheckClipboardSelection(NativeTerminalControl control)
    {
        const string text = "vex clipboard regression";
        control.SelfTestFeed($"\x1b[2J\x1b[H{text}");
        var plain = Capture(control);
        var copyOnSelect = AppSettings.Instance.CopyOnSelect;
        try
        {
            AppSettings.Instance.CopyOnSelect = true;
            foreach (var copy in new Action[]
            {
                control.SelfTestSelectionMouseUp,
                control.SelfTestCopySelection,
                control.SelfTestCutSelection,
            })
            {
                control.SelfTestSelect(0, 0, text.Length - 1, 0);
                Assert(control, control.SelfTestSelectedText() == text, "clipboard: mouse drag selects the fixture");
                copy();
                Assert(control, NativeTerminalControl.TryGetClipboardText(out var copied) && copied == text,
                    "clipboard: copy writes selected text through WPF");
                Assert(control, control.SelfTestSelectedText() is null, "clipboard: successful copy clears selection");
                if (SystemParameters.ClientAreaAnimation)
                    Assert(control, CellCornerLum(control, plain, 9, 0) != CellCornerLum(control, Capture(control), 9, 0),
                        "clipboard: automatic copy, manual copy and cut show feedback over application backgrounds");
                control.SelfTestCopyFeedback(1);
                Assert(control, PixelsEqual(plain, Capture(control)), "clipboard: feedback completion restores clean pixels");
            }
        }
        finally
        {
            AppSettings.Instance.CopyOnSelect = copyOnSelect;
        }
        Report(control, "PASS clipboard: automatic copy, manual copy and cut with feedback");
    }

    private static void CheckCopyFeedback(NativeTerminalControl control)
    {
        CheckCopyFeedback(control, "");
        control.SelfTestFeed("\x1b[?1049h\x1b[?1000h\x1b[?1006h");
        try
        {
            CheckCopyFeedback(control, "\x1b[48;2;35;55;75m");
        }
        finally
        {
            control.SelfTestFeed("\x1b[0m\x1b[?1000l\x1b[?1006l\x1b[?1049l");
        }
    }

    private static void CheckCopyFeedback(NativeTerminalControl control, string background)
    {
        control.SelfTestClearSelection();
        control.SelfTestFeed($"{background}\x1b[2J\x1b[Hprefix copied text suffix\x1b[2;1H");
        var applicationPlain = Capture(control);
        control.SelfTestApplicationCopy("copied text");
        if (SystemParameters.ClientAreaAnimation)
        {
            var applicationCopy = Capture(control);
            Assert(control, CellCornerLum(control, applicationPlain, 9, 0) != CellCornerLum(control, applicationCopy, 9, 0),
                "application copy: feedback works without a terminal selection");
            Assert(control, CellCornerLum(control, applicationPlain, 2, 0) == CellCornerLum(control, applicationCopy, 2, 0),
                "application copy: feedback leaves the surrounding text untouched");
        }
        control.SelfTestCopyFeedback(1);
        Assert(control, PixelsEqual(applicationPlain, Capture(control)),
            "application copy: completion restores the application's pixels");
        foreach (var text in new[] { "copied content outside the visible viewport", "", " \r\n" })
        {
            control.SelfTestApplicationCopy(text);
            Assert(control, PixelsEqual(applicationPlain, Capture(control)),
                "application copy: absent or blank source does not flash an empty caret rectangle");
            control.SelfTestCopyFeedback(1);
        }
        // Delete/cut sends the removed text to the clipboard while terminal
        // painting can still lag behind the editor's updated screen.
        control.SelfTestApplicationCopyAfterOutput($"{background}\x1b[Hprefix suffix\x1b[K", "copied text");
        control.SelfTestFeed("");
        var deleted = Capture(control);
        control.SelfTestCopyFeedback(1);
        Assert(control, PixelsEqual(deleted, Capture(control)),
            "application copy: deleted text does not animate from a stale frame");
        control.SelfTestFeed($"{background}\x1b[2J\x1b[Hprefix first line\r\nsecond line end\x1b[3;1H");
        var plain = Capture(control);
        control.SelfTestSelect(7, 0, 5, 1);
        var selected = Capture(control);
        control.SelfTestCopyFeedback(0);
        var initial = Capture(control);
        DumpPng(control, selected, Path.ChangeExtension(ReportPath!, ".selection.png"));
        DumpPng(control, initial, Path.ChangeExtension(ReportPath!, ".copy.png"));
        if (SystemParameters.ClientAreaAnimation)
        {
            Assert(control, !PixelsEqual(plain, initial), "copy feedback: selected text remains visible after deselection");
            Assert(control, CellCornerLum(control, plain, 9, 0) != CellCornerLum(control, initial, 9, 0),
                "copy feedback: selection confirmation stays visible over application backgrounds");
            Assert(control, CellCornerLum(control, plain, 2, 0) == CellCornerLum(control, initial, 2, 0),
                "copy feedback: unselected gap outside multiline selection stays untouched");
            control.SelfTestCopyFeedback(0.25);
            var sweep = Capture(control);
            DumpPng(control, sweep, Path.ChangeExtension(ReportPath!, ".copy-sweep.png"));
            Assert(control, !PixelsEqual(initial, sweep), "copy feedback: light sweeps across the held snapshot");
            Assert(control, CellCornerLum(control, plain, 2, 0) == CellCornerLum(control, sweep, 2, 0),
                "copy feedback: sweep stays inside the multiline selection");
            control.SelfTestCopyFeedback(0.5);
            var fading = Capture(control);
            DumpPng(control, fading, Path.ChangeExtension(ReportPath!, ".copy-fade.png"));
            Assert(control, !PixelsEqual(sweep, fading), "copy feedback: snapshot dissolves and gently lifts");
        }
        control.SelfTestCopyFeedback(1);
        Assert(control, PixelsEqual(plain, Capture(control)), "copy feedback: completion restores clean terminal pixels");
        Report(control, "PASS copy feedback: multiline footprint, light sweep, dissolve, clean completion");
    }

    private static void CheckGlyphGridAlignment(NativeTerminalControl control)
    {
        for (var columns = 2; columns <= 12; columns++)
        {
            var prefix = new string('.', columns);
            control.SelfTestFeed($"\u001b[2J\u001b[H{prefix}tail\r\n\u001b[31m{prefix}\u001b[0mtail\r\n\U0001F9EA{new string('.', columns - 2)}tail\u001b[4;1H");
            var shot = Capture(control);
            foreach (var (name, row) in new[] { ("color", 1), ("emoji", 2) })
            {
                var (shift, best, zero) = BandAlignment(control, shot, 0, row, columns, columns + 4, 3);
                Report(control, shift == 0
                    ? $"PASS glyph-grid {name} col={columns}: segment boundary preserves alignment"
                    : $"FAIL glyph-grid {name} col={columns}: shift={shift} best={best} zero={zero}");
            }
        }
        control.SelfTestFeed("\u001b[2J\u001b[He\u0301\r\n\u00E9\u001b[3;1H");
        var accent = Capture(control);
        var (_, accentDifference, _) = BandAlignment(control, accent, 0, 1, 0, 1, 0);
        Report(control, $"combining accent pixel difference={accentDifference}");
        Report(control, CellInk(control, accent, 1, 0) > CellInk(control, accent, 1, 1) + 3
            ? "FAIL combining accent: mark escapes its terminal cell"
            : "PASS combining accent: mark stays with its base letter");
        DumpPng(control, accent, Path.ChangeExtension(ReportPath!, ".accent.png"));
    }

    private static void CheckFontGridAlignment(NativeTerminalControl control)
    {
        var originalFamily = AppSettings.Instance.FontFamily;
        var originalSize = AppSettings.Instance.FontSize;
        try
        {
            foreach (var (family, size) in new[] { ("Consolas", 13.0), ("Global Monospace", 14.0) })
            {
                control.SelfTestFontMetrics(family, size);
                Report(control, $"font-grid family={family} size={size}");
                CheckGlyphGridAlignment(control);
            }
        }
        finally
        {
            control.SelfTestFontMetrics(originalFamily, originalSize);
        }
    }

    private static void CheckBlockElementContrast(NativeTerminalControl control)
    {
        const string content = "A▀A▄A█A▌A▐A░A▒A▓A▖A▟";
        var originalContrast = AppSettings.Instance.AutomaticContrast;
        try
        {
            // Identical SGR on adjacent text and blocks exercises mixed-run rendering.
            var fixture = new StringBuilder("\x1b[0m\x1b[2J\x1b[H");
            var styles = new[] { "", "1;", "2;", "7;" };
            for (var row = 0; row < styles.Length; row++)
                fixture.Append($"\x1b[{row + 1};1H\x1b[0;{styles[row]}38;2;80;90;100;48;2;90;100;110m{content}");
            fixture.Append("\x1b[0m\x1b[5;1H");
            control.SelfTestFeed(fixture.ToString());
            AppSettings.Instance.AutomaticContrast = false;
            control.SelfTestFullRedraw();
            var original = Capture(control);
            AppSettings.Instance.AutomaticContrast = true;
            control.SelfTestFullRedraw();
            var corrected = Capture(control);

            var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
            var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
            for (var row = 0; row < styles.Length; row++)
            {
                for (var col = 0; col < content.Length; col++)
                {
                    var x0 = (int)Math.Round(col * control.SelfTestCellWidth * dpi);
                    var x1 = (int)Math.Round((col + 1) * control.SelfTestCellWidth * dpi);
                    var y0 = (int)Math.Round(row * control.SelfTestCellHeight * dpi);
                    var y1 = (int)Math.Round((row + 1) * control.SelfTestCellHeight * dpi);
                    var equal = true;
                    for (var y = y0; y < y1; y++)
                    {
                        var offset = (y * width + x0) * 4;
                        var length = (x1 - x0) * 4;
                        equal &= original.AsSpan(offset, length).SequenceEqual(corrected.AsSpan(offset, length));
                    }
                    Assert(control, equal == (col % 2 == 1),
                        $"block contrast: row={row} col={col} preserves graphics but repairs adjacent text");
                }
            }
            Report(control, "PASS block contrast: original graphic colors, readable adjacent text, bold, faint and inverse");
        }
        finally
        {
            AppSettings.Instance.AutomaticContrast = originalContrast;
            control.SelfTestFeed("\x1b[0m\x1b[2J\x1b[H");
        }
    }

    private static void RunIncrementalParity(NativeTerminalControl control)
    {
        Report(control, $"incr start cols={control.SelfTestCols} rows={control.SelfTestRows} cell={control.SelfTestCellWidth:0.0}x{control.SelfTestCellHeight:0.0}");
        control.SelfTestStabilizeCaret();
        control.SelfTestFeed("\u001b[2J\u001b[H");

        // Agent-style streaming: ConPTY-sized chunks, one flush per chunk,
        // wrapping and scrolling the buffer as it goes. The every-eighth-chunk
        // captures keep the run honest without a full-grid compare per chunk.
        var stream = new StringBuilder();
        for (var i = 1; i <= 60; i++)
            stream.Append("stream-line-").Append(i).Append(" the quick brown fox jumps 0123456789\r\n");
        var text = stream.ToString();
        for (var i = 0; i < text.Length; i += 37)
        {
            control.SelfTestFeed(text.Substring(i, Math.Min(37, text.Length - i)));
            if (i % (37 * 8) == 0)
                ParityCheck(control, $"incr-stream@{i}");
        }
        ParityCheck(control, "incr-stream-end");
        InkGhostCheck(control, "incr-stream-end");

        // Viewport scroll up and back through scrollback.
        control.SelfTestScroll(-7);
        ParityCheck(control, "incr-scrolled-up");
        control.SelfTestScroll(7);
        ParityCheck(control, "incr-scrolled-back");

        // A pane resize mid-stream (fewer rows here), then more output: the
        // rows below the content must not keep pre-resize glyphs.
        var rows = control.SelfTestRows;
        control.SelfTestResizeGrid(control.SelfTestCols, Math.Max(6, rows - 5));
        ParityCheck(control, "incr-resized-smaller");
        control.SelfTestFeed("after-resize one\r\nafter-resize two\r\n");
        ParityCheck(control, "incr-after-resize-output");
        InkGhostCheck(control, "incr-after-resize-output");
        control.SelfTestResizeGrid(control.SelfTestCols, rows);
        ParityCheck(control, "incr-resized-back");

        // In-place status redraws (spinner) and a colored full-width bar, the
        // shapes an agent TUI repaints many times a second.
        control.SelfTestFeed("\u001b[1;1H\u001b[48;2;30;40;60mstatus: working      \u001b[0m");
        ParityCheck(control, "incr-status");
        control.SelfTestFeed("\u001b[1;1H\u001b[48;2;30;40;60mstatus: done         \u001b[0m");
        ParityCheck(control, "incr-status2");
        control.SelfTestFeed("\u001b[1;1H\u001b[0K");
        ParityCheck(control, "incr-status-cleared");
        InkGhostCheck(control, "incr-status-cleared");

        // Synchronized output (DEC 2026): a TUI/agent frame must reach the
        // screen in one step. Nothing may change between the opening marker
        // and the closing one, or the pane shows a half-updated frame — new
        // text in the rows written so far while the previous frame still fills
        // the rest, which reads as duplicated/ghost lines.
        control.SelfTestFeed("\u001b[2J\u001b[H");
        control.SelfTestFeed("baseline one\r\nbaseline two\r\nbaseline three\r\n");
        var syncBefore = Capture(control);
        control.SelfTestFeed("\u001b[?2026h");
        control.SelfTestFeed("\u001b[1;1Hframe one   ");
        Report(control, PixelsEqual(syncBefore, Capture(control))
            ? "PASS incr-sync: mid-frame pixels unchanged"
            : "FAIL incr-sync: half-applied frame painted");
        control.SelfTestFeed("\u001b[2;1Hframe two   ");
        Report(control, PixelsEqual(syncBefore, Capture(control))
            ? "PASS incr-sync: second mid-frame unchanged"
            : "FAIL incr-sync: second half-applied frame painted");
        control.SelfTestFeed("\u001b[?2026l");
        var syncAfter = Capture(control);
        Report(control, !PixelsEqual(syncBefore, syncAfter)
            ? "PASS incr-sync: frame committed on close"
            : "FAIL incr-sync: frame never appeared");
        ParityCheck(control, "incr-sync-after-close");
        InkGhostCheck(control, "incr-sync-after-close");

        // Multi-codepoint clusters: a narrow cell holding three UTF-16 units
        // (keycap "1️⃣" = digit + VS16 + combining keycap) contributes three
        // run-width entries, so a row full of them exceeds the pooled
        // per-row buffers every cell is copied into. A throw mid-redraw
        // aborts the pass, and every retry aborts at the same row: the rows
        // below it stop updating — stale/duplicated text until a full repaint.
        control.SelfTestFeed("\u001b[2J\u001b[H");
        var failuresBefore = control.SelfTestRenderFailures;
        var clusters = new StringBuilder();
        for (var i = 0; i < control.SelfTestCols; i++)
            clusters.Append('1').Append('\uFE0F').Append('\u20E3');
        control.SelfTestFeed(clusters.ToString());
        // Repaint once before comparing: the first paint of a cluster whose
        // fallback font is not loaded yet rasterizes one cell at a time while
        // the face resolves, which is a first-render artifact of the font
        // cache rather than a stale-pixel difference.
        control.SelfTestFullRedraw();
        Report(control, $"incr-cluster cells: {CellAttrDumpRange(control, 0, 0, 4)}");
        Report(control, control.SelfTestRenderFailures == failuresBefore
            ? "PASS incr-clusters: cluster-dense row redraws cleanly"
            : $"FAIL incr-clusters: {control.SelfTestRenderFailures - failuresBefore} redraw passes threw");
        control.SelfTestFeed("\r\nafter clusters one\r\nafter clusters two\r\n");
        for (var r = 0; r < Math.Min(4, control.SelfTestRows); r++)
        {
            var rowText = control.SelfTestRowText(r);
            Report(control, $"incr-clusters row{r} len={rowText.Length} head='{rowText[..Math.Min(24, rowText.Length)]}'");
        }
        ParityCheck(control, "incr-clusters-followup");
        InkGhostCheck(control, "incr-clusters-followup");
        Report(control, control.SelfTestRenderFailures == failuresBefore
            ? "PASS incr-clusters-followup: rows below still painting"
            : $"FAIL incr-clusters-followup: {control.SelfTestRenderFailures - failuresBefore} redraw passes threw");

        Report(control, "incr done");
    }

    /// <summary>Captures the incrementally painted frame, repaints the whole
    /// surface from the same buffer, and compares. A mismatch means the
    /// incremental path left pixels behind — the duplicated/ghost text.</summary>
    private static void ParityCheck(NativeTerminalControl control, string label)
    {
        var incremental = Capture(control);
        control.SelfTestFullRedraw();
        var forced = Capture(control);
        if (PixelsEqual(incremental, forced))
        {
            Report(control, $"PASS {label}: incremental pixels match a full repaint");
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = Math.Max(1, incremental.Length / (width * 4));
        var rowPixels = Math.Max(1, (int)Math.Round(control.SelfTestCellHeight * dpi));
        var diff = 0;
        var reported = 0;
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (var i = 0; i < incremental.Length; i += 4)
        {
            if (incremental[i] == forced[i] && incremental[i + 1] == forced[i + 1] && incremental[i + 2] == forced[i + 2])
                continue;
            diff++;
            var pixel = i / 4;
            var x = pixel % width;
            var y = pixel / width;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (reported < 6)
            {
                var row = y / rowPixels;
                var col = x / Math.Max(1, (int)Math.Round(control.SelfTestCellWidth * dpi));
                Report(control, $"{label} differ row{row} col{col} px=({x},{y}) incremental=({incremental[i]},{incremental[i + 1]},{incremental[i + 2]}) forced=({forced[i]},{forced[i + 1]},{forced[i + 2]})");
                reported++;
            }
        }
        Report(control, $"FAIL {label}: {diff} pixels differ from a full repaint (x {minX}-{maxX}, y {minY}-{maxY} of {width}x{height})");
        control.SelfTestFullRedraw();
        var forcedAgain = Capture(control);
        Report(control, PixelsEqual(forced, forcedAgain)
            ? $"{label} repeat: two full repaints are identical"
            : $"{label} repeat: two full repaints DIFFER (non-deterministic paint)");

        var diffRow = minY / rowPixels;
        Report(control, $"{label} row{diffRow} text len={control.SelfTestRowText(diffRow).Length}");
        foreach (var probeCol in new[] { 0, 1, 2, 3, 10, 40 })
        {
            Report(control, $"{label} ink col{probeCol} incremental={CellInk(control, incremental, probeCol, diffRow)} forced={CellInk(control, forced, probeCol, diffRow)}");
        }
    }

    /// <summary>Blank cells must carry no ink: a glyph that survived an
    /// overwrite or a shift shows up as a lit cell centre the buffer says is
    /// empty. Overlap-prone neighbours (wide glyphs, AA spill) are skipped,
    /// like <see cref="ReplayCheck"/> does for backgrounds.</summary>
    private static void InkGhostCheck(NativeTerminalControl control, string label)
    {
        var shot = Capture(control);
        var backgroundLum = MinLum(shot);
        var caret = control.SelfTestCaretCell();
        var ghosts = 0;
        for (var row = 0; row < Math.Min(control.SelfTestRows, 24); row++)
        {
            var line = control.SelfTestLine(row);
            if (line is null)
                continue;
            var cells = line.Cells;
            for (var col = 0; col < cells.Length; col++)
            {
                if (caret is { Row: var caretRow, Col: var caretCol } && row == caretRow && col == caretCol)
                    continue;
                if (cells[col].Text.Length > 0)
                    continue;
                if (col > 0 && cells[col - 1].Text.Length > 0)
                    continue;
                if (col + 1 < cells.Length && cells[col + 1].Text.Length > 0)
                    continue;
                if (!HasInkAt(control, shot, col, row, backgroundLum))
                    continue;
                ghosts++;
                if (ghosts <= 8)
                    Report(control, $"{label} ghost row{row} col{col} text='{control.SelfTestRowText(row)}'");
            }
        }
        Report(control, ghosts == 0
            ? $"PASS {label}: no ghost ink in blank cells"
            : $"FAIL {label}: {ghosts} blank cells carry ink");
    }

    private static void CheckIndexedThemeContrast(NativeTerminalControl control)
    {
        foreach (var theme in new[] { BuiltInThemes.VexDark, BuiltInThemes.VexLight })
        {
            var palette = new TerminalPalette(theme);
            palette.Resolve(ColorTag.Palette, 0, ColorTag.None, 0, CellFlags.None,
                out var black, out _);
            palette.Resolve(ColorTag.Palette, 1, ColorTag.None, 0, CellFlags.None,
                out var red, out _);

            var background = FastColor.ParseHex(theme.Background);
            var readable = black is SolidColorBrush blackBrush && red is SolidColorBrush redBrush
                && TerminalContrastCorrection.ContrastRatio(blackBrush.Color, background) >= 4.5
                && TerminalContrastCorrection.ContrastRatio(redBrush.Color, background) >= 4.5
                && redBrush.Color.R > redBrush.Color.G && redBrush.Color.R > redBrush.Color.B;
            Report(control, readable
                ? $"PASS theme-contrast({theme.Name}): indexed colors remain readable and distinct"
                : $"FAIL theme-contrast({theme.Name}): black={black} red={red}");
        }
    }


}
#else
namespace Vex.App.Terminal.Native;

internal static class RenderSelfTest
{
    public static string? ReportPath => null;

    [System.Diagnostics.Conditional("DEBUG")]
    public static void Run(NativeTerminalControl control) { }
}
#endif
