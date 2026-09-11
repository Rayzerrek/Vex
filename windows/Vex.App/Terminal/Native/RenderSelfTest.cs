using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
internal static class RenderSelfTest
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

    public static void Run(NativeTerminalControl control)
    {
        if (string.IsNullOrEmpty(ReportPath))
            return;

        var t = new DispatcherTimer(DispatcherPriority.ApplicationIdle, control.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        t.Tick += (_, _) =>
        {
            if (control.SelfTestCols == 0 || control.SelfTestRows == 0)
                return;
            t.Stop();
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
                        Application.Current.Shutdown();
                    }
                    return;
                }
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
                    Application.Current.Shutdown();
            }
        };
        t.Start();
    }

    /// <summary>
    /// Live output-flood stress: real nu + gh session fed through the full
    /// async pump. Every phase is captured to a PNG (VEX_LIVE_DIR) and
    /// pixel-checked against the emulator buffer — background corners must
    /// match their cells (no overlapping/ghost pixels, colors correct) and
    /// clears must leave a clean screen.
    /// </summary>
    private static void RunStress(NativeTerminalControl control)
    {
        var dir = string.IsNullOrEmpty(LiveDir) ? Path.GetTempPath() : LiveDir;
        Report(control, $"stress start shell={LiveShell ?? "default"} cols={control.SelfTestCols} rows={control.SelfTestRows}");
        control.SelfTestStabilizeCaret();
        control.SelfTestShell = LiveShell;
        control.SelfTestStartSession();

        byte[]? clearedPixels = null;
        var steps = new Queue<(int DelayMs, Action Act)>();
        steps.Enqueue((4000, () => control.SelfTestType("1..200 | each {gh help}\r")));
        steps.Enqueue((30000, () =>
        {
            Shot(control, Path.Combine(dir, "stress-01-flood.png"));
            Report(control, $"stress flood {control.SelfTestScrollInfo()}");
            ReplayCheck(control, "stress-flood");
        }));
        steps.Enqueue((300, () => control.SelfTestType("cls\r")));
        steps.Enqueue((2500, () =>
        {
            clearedPixels = Capture(control);
            Shot(control, Path.Combine(dir, "stress-02-clear.png"));
            // The shell redraws its (possibly multi-line) prompt right after
            // cls, so "empty screen" is not the contract; the contract is
            // that every painted pixel matches the buffer (no ghosts).
            ReplayCheck(control, "stress-clear");
        }));
        steps.Enqueue((200, () => control.SelfTestScroll(-10)));
        steps.Enqueue((600, () =>
        {
            Shot(control, Path.Combine(dir, "stress-03-scrolled-up.png"));
            ReplayCheck(control, "stress-scrolled-up");
            control.SelfTestScroll(10);
        }));
        steps.Enqueue((600, () =>
        {
            var snapped = Capture(control);
            Shot(control, Path.Combine(dir, "stress-04-snapback.png"));
            Report(control, clearedPixels is not null && PixelsEqual(clearedPixels, snapped)
                ? "PASS stress-scroll: snap-back identical"
                : "FAIL stress-scroll: snap-back differs");
        }));
        steps.Enqueue((200, () => control.SelfTestType("1..2000 | each {print \"the quick brown fox jumps over the lazy dog 0123456789\"}\r")));
        steps.Enqueue((20000, () =>
        {
            Shot(control, Path.Combine(dir, "stress-05-big.png"));
            Report(control, $"stress big {control.SelfTestScrollInfo()}");
            ReplayCheck(control, "stress-big");
        }));
        steps.Enqueue((200, () =>
        {
            Report(control, "stress done");
            Application.Current.Shutdown();
        }));

        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, control.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        timer.Tick += (_, _) =>
        {
            if (steps.Count == 0)
            {
                timer.Stop();
                return;
            }
            var (delay, act) = steps.Peek();
            Report(control, $"live step remaining={steps.Count} delay={delay}");
            // The pump timer has driven the previous step long enough.
            timer.Stop();
            var fire = new DispatcherTimer(DispatcherPriority.ApplicationIdle, control.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(50, delay)),
            };
            fire.Tick += (_, _) =>
            {
                fire.Stop();
                steps.Dequeue();
                act();
                timer.Start();
            };
            fire.Start();
        };
        timer.Start();
    }

    /// <summary>
    /// Live file-edit scenario: open the given file in nvim, type a function
    /// into it (insert mode), leave without saving, and pixel-check every
    /// phase against the emulator buffer.
    /// </summary>
    private static void RunLiveFile(NativeTerminalControl control, string file)
    {
        var dir = string.IsNullOrEmpty(LiveDir) ? Path.GetTempPath() : LiveDir;
        Report(control, $"live-file start file={file} cols={control.SelfTestCols} rows={control.SelfTestRows}");
        control.SelfTestStabilizeCaret();
        control.SelfTestStartSession();

        var steps = new Queue<(int DelayMs, Action Act)>();
        // Single quotes: nu treats backslashes in double quotes as escapes.
        steps.Enqueue((2000, () => control.SelfTestType($"nvim --clean -i NONE '{file}'\r")));
        steps.Enqueue((3000, () =>
        {
            Shot(control, Path.Combine(dir, "file-01-open.png"));
            Report(control, $"live-file open {control.SelfTestCursorInfo()}");
            ReplayCheck(control, "file-open");
        }));
        // Append at the end of the last line, then type a small function.
        steps.Enqueue((300, () => control.SelfTestType("Go")));
        steps.Enqueue((500, () => control.SelfTestType("function greet(name: string): string {\r\treturn `Hello, ${name}!`;\r}\r")));
        steps.Enqueue((1500, () =>
        {
            Shot(control, Path.Combine(dir, "file-02-typed.png"));
            Report(control, $"live-file typed {control.SelfTestCursorInfo()}");
            Report(control, $"live-file row0='{control.SelfTestRowText(0)}'");
            ReplayCheck(control, "file-typed");
        }));
        // Leave insert mode and quit without saving: the user's file must
        // stay untouched.
        steps.Enqueue((300, () => control.SelfTestType("\x1b:q!\r")));
        steps.Enqueue((2500, () =>
        {
            Shot(control, Path.Combine(dir, "file-03-after-exit.png"));
            ReplayCheck(control, "file-after-exit");
        }));
        steps.Enqueue((200, () =>
        {
            Report(control, "live-file done");
            Application.Current.Shutdown();
        }));

        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, control.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        timer.Tick += (_, _) =>
        {
            if (steps.Count == 0)
            {
                timer.Stop();
                return;
            }
            var (delay, act) = steps.Peek();
            Report(control, $"live step remaining={steps.Count} delay={delay}");
            // The pump timer has driven the previous step long enough.
            timer.Stop();
            var fire = new DispatcherTimer(DispatcherPriority.ApplicationIdle, control.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(50, delay)),
            };
            fire.Tick += (_, _) =>
            {
                fire.Stop();
                steps.Dequeue();
                act();
                timer.Start();
            };
            fire.Start();
        };
        timer.Start();
    }

    /// <summary>
    /// Live end-to-end drive: real ConPTY session, real pump, real async
    /// timing — the same nvim pum scenario the user runs by hand. Each
    /// phase is captured to a PNG (VEX_LIVE_DIR) and the "after Tab" frame
    /// is pixel-checked against the emulator buffer.
    /// </summary>
    private static void RunLive(NativeTerminalControl control)
    {
        Report(control, $"live start cols={control.SelfTestCols} rows={control.SelfTestRows}");
        try
        {
            control.SelfTestStabilizeCaret();
            Report(control, "live caret stabilized");
            control.SelfTestStartSession();
            Report(control, "live session started");
        }
        catch (Exception e)
        {
            Report(control, $"live setup EXCEPTION {e.GetType().Name}: {e.Message}");
            return;
        }

        var dir = string.IsNullOrEmpty(LiveDir) ? Path.GetTempPath() : LiveDir;
        var testFile = Path.Combine(dir, "vex-live-words.txt");
        var initFile = Path.Combine(dir, "vex-live-init.vim");
        File.WriteAllText(testFile, "apple\napricot\navocado\nbanana\nbattery\nbook\nbottle\nbrave\nbreeze\nbridge\n");
        File.WriteAllText(initFile, "set completeopt=menuone,noinsert,noselect\nhighlight Pmenu ctermbg=236 ctermfg=250\nhighlight PmenuSel ctermbg=240 ctermfg=255 cterm=bold\nhighlight PmenuSbar ctermbg=238\nset pumheight=12\n");

        var steps = new Queue<(int DelayMs, Action Act)>();
        // nu treats backslashes as escapes in double quotes ('\U' breaks
        // paths), so the file paths use single quotes.
        steps.Enqueue((2000, () => control.SelfTestType($"nvim --clean -u '{initFile}' -i NONE '{testFile}'\r")));
        steps.Enqueue((3000, () => control.SelfTestType("i")));
        steps.Enqueue((600, () => control.SelfTestType("b")));
        steps.Enqueue((700, () => control.SelfTestType("\x0e")));   // Ctrl-N: open pum
        steps.Enqueue((1200, () =>
        {
            Shot(control, Path.Combine(dir, "live-02-pum-open.png"));
            Report(control, $"live pum-open buffer attrs: {CellAttrDump(control, 1)} / {CellAttrDump(control, 2)}");
        }));
        steps.Enqueue((300, () => control.SelfTestType("\t")));     // Tab: accept, pum closes
        steps.Enqueue((1500, () =>
        {
            Shot(control, Path.Combine(dir, "live-03-after-tab.png"));
            Report(control, $"live after-tab {control.SelfTestCursorInfo()}");
            Report(control, $"live after-tab row0='{control.SelfTestRowText(0)}'");
            Report(control, $"live after-tab row1='{control.SelfTestRowText(1)}'");
            Report(control, $"live after-tab cells18-26: {CellAttrDumpRange(control, 1, 18, 9)}");
            Report(control, $"live after-tab buffer attrs: {CellAttrDump(control, 1)} / {CellAttrDump(control, 2)}");
            ReplayCheck(control, "live-after-tab");
        }));
        steps.Enqueue((300, () => control.SelfTestType("\x1b")));
        steps.Enqueue((400, () => control.SelfTestType(":q!\r")));
        steps.Enqueue((2500, () =>
        {
            Shot(control, Path.Combine(dir, "live-04-after-exit.png"));
            ReplayCheck(control, "live-after-exit");
        }));
        steps.Enqueue((200, () =>
        {
            Report(control, "live done");
            Application.Current.Shutdown();
        }));

        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, control.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        timer.Tick += (_, _) =>
        {
            if (steps.Count == 0)
            {
                timer.Stop();
                return;
            }
            var (delay, act) = steps.Peek();
            Report(control, $"live step remaining={steps.Count} delay={delay}");
            // The pump timer has driven the previous step long enough.
            timer.Stop();
            var fire = new DispatcherTimer(DispatcherPriority.ApplicationIdle, control.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(50, delay)),
            };
            fire.Tick += (_, _) =>
            {
                fire.Stop();
                steps.Dequeue();
                act();
                timer.Start();
            };
            fire.Start();
        };
        timer.Start();
    }

    /// <summary>
    /// Live enter-TUI / exit / type scenario: reproduces the duplicate-prompt
    /// bug by opening nvim, quitting, then typing into the shell and dumping
    /// every frame row so the duplication is visible in the report.
    /// </summary>
    private static void RunLivePrompt(NativeTerminalControl control)
    {
        Report(control, $"prompt start cols={control.SelfTestCols} rows={control.SelfTestRows}");
        control.SelfTestStabilizeCaret();
        control.SelfTestStartSession();

        var dir = string.IsNullOrEmpty(LiveDir) ? Path.GetTempPath() : LiveDir;

        var steps = new Queue<(int DelayMs, Action Act)>();
        steps.Enqueue((2500, () => control.SelfTestType("nvim --clean -i NONE\r")));
        steps.Enqueue((3500, () =>
        {
            Report(control, $"in-nvim {control.SelfTestCursorInfo()}");
            control.SelfTestType(":q!\r");
        }));
        steps.Enqueue((2500, () =>
        {
            Shot(control, Path.Combine(dir, "prompt-01-after-exit.png"));
            DumpRows(control, "after-exit");
            // The regression: after a TUI exits there must be exactly one
            // prompt. The stale OSC 133;A marker used to force a line feed
            // here, leaving the prompt duplicated on the row below.
            var prompt0 = control.SelfTestRowText(0);
            var prompt1 = control.SelfTestRowText(1);
            var prompt2 = control.SelfTestRowText(2);
            // Row 0 is the restored command line ("... nvim --clean -i NONE"),
            // row 1 the single prompt, row 2 must be empty (no duplicate).
            var ok = prompt0.Contains("nvim") && !prompt1.Contains("nvim")
                     && prompt1.Length > 0 && prompt2.Length == 0;
            Report(control, ok
                ? "PASS prompt: single prompt after TUI exit"
                : $"FAIL prompt: duplicate or misplaced prompt row0='{prompt0}' row1='{prompt1}' row2='{prompt2}'");
            control.SelfTestType("hello");
        }));
        steps.Enqueue((1500, () =>
        {
            Shot(control, Path.Combine(dir, "prompt-02-typed.png"));
            DumpRows(control, "after-type");
        }));
        steps.Enqueue((500, () =>
        {
            Report(control, "prompt done");
            Application.Current.Shutdown();
        }));

        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, control.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        timer.Tick += (_, _) =>
        {
            if (steps.Count == 0)
            {
                timer.Stop();
                return;
            }
            var (delay, act) = steps.Peek();
            timer.Stop();
            var fire = new DispatcherTimer(DispatcherPriority.ApplicationIdle, control.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(50, delay)),
            };
            fire.Tick += (_, _) =>
            {
                fire.Stop();
                steps.Dequeue();
                act();
                timer.Start();
            };
            fire.Start();
        };
        timer.Start();
    }

    private static void DumpRows(NativeTerminalControl control, string label)
    {
        for (var r = 0; r < control.SelfTestRows; r++)
        {
            var text = control.SelfTestRowText(r);
            if (!string.IsNullOrEmpty(text))
                Report(control, $"{label} row{r}='{text}'");
        }
        Report(control, $"{label} {control.SelfTestCursorInfo()}");
        Report(control, $"{label} {control.SelfTestScrollInfo()}");
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
        try { File.AppendAllText(ReportPath!, $"{line}\n"); }
        catch { }
    }

    private static void RunCore(NativeTerminalControl control)
    {
        Report(control, $"start cols={control.SelfTestCols} rows={control.SelfTestRows} cell={control.SelfTestCellWidth:0.0}x{control.SelfTestCellHeight:0.0}");

        // A steady caret so bitmaps from different captures are comparable.
        control.SelfTestStabilizeCaret();

        CheckIndexedThemeContrast(control);

        // 300 lines: the long-session stress case (scrollback + full redraws).
        var sb = new StringBuilder(300 * 60);
        for (var i = 1; i <= 300; i++)
            sb.Append("line-").Append(i).Append(" the quick brown fox jumps over the lazy dog 0123456789\r\n");
        control.SelfTestFeed(sb.ToString());
        var full = Capture(control);
        var fullInk = InkBands(control, full);
        Report(control, $"after-feed inkBands={fullInk}");

        // ED2 + cursor home: what `clear` sends. The screen must be empty
        // afterwards — any leftover text rows are ghost pixels.
        control.SelfTestFeed("\x1b[2J\x1b[H");
        var cleared = Capture(control);
        var clearedInk = InkBands(control, cleared);
        Report(control, $"after-clear inkBands={clearedInk}");
        Report(control, clearedInk <= 2 ? "PASS clear: screen clean" : "FAIL clear: ghost rows remain");

        // Wide characters: CJK, emoji and box drawing on one line.
        control.SelfTestFeed("wide: 日本語テスト 🎉🚀 emoji ┌─┐│├┤┼ ✓ あいうえお END\r\n");
        var wide = Capture(control);
        var wideInk = InkBands(control, wide);
        Report(control, $"after-wide inkBands={wideInk}");
        Report(control, wideInk >= 1 ? "PASS wide: line rendered" : "FAIL wide: nothing rendered");

        // Wide-glyph advance: a 2-column CJK glyph at cell 0 must push the X
        // to cell 2 — a 1-cell advance (the old bug) puts it at cell 1. The
        // caret moves to row 1 so its overlay cannot ink the cells under test.
        control.SelfTestFeed("\x1b[2J\x1b[H中X\x1b[2;1H");
        var wideAdv = Capture(control);
        var xAt2 = HasInkAt(control, wideAdv, col: 2, row: 0);
        var xAt3 = HasInkAt(control, wideAdv, col: 3, row: 0);
        Report(control, $"wide-adv X at cell2={xAt2} cell3={xAt3}");
        Report(control, xAt2 && !xAt3 ? "PASS wide-adv: X lands in cell 2" : "FAIL wide-adv: X misplaced");

        // Mixed-run alignment: an emoji mid-line must not shift the text that
        // follows it. The whole-run FormattedText fallback applied natural
        // font advances, so every later character drifted off the grid and
        // slid under the next run's background — the doubled-letter look in
        // nvim/AI-agent output. The drift is only a few px (Cascadia's ASCII
        // advance matches the cell), so compare the tail's pixel band against
        // a reference row of pure ASCII instead of probing cell centers.
        control.SelfTestFeed("\x1b[2J\x1b[H🎉tail\r\n");
        var mixedRow = control.SelfTestLine(0);
        var tCol = -1;
        if (mixedRow is not null)
        {
            for (var c = 0; c < mixedRow.Cells.Length; c++)
            {
                if (mixedRow.Cells[c].Text == "t")
                {
                    tCol = c;
                    break;
                }
            }
        }
        Assert(control, tCol > 0, "mixed-run: emoji cell present before 'tail'");
        var reference = new string('.', tCol) + "tail";
        control.SelfTestFeed(reference + "\x1b[3;1H");
        var mixedShot = Capture(control);
        var (bestShift, diffAtBest, _) = BandAlignment(control, mixedShot, rowA: 0, rowB: 1, colFrom: tCol + 1, colTo: tCol + 4, maxShift: 3);
        Report(control, $"mixed-run tail alignment bestShift={bestShift} diff={diffAtBest}");
        Report(control, bestShift == 0 ? "PASS mixed-run: text after emoji is grid-aligned" : "FAIL mixed-run: fallback drift after emoji");

        // Scroll up two pages and back: the snap-back bitmap must be
        // pixel-identical to the pre-scroll one, otherwise stale rows remain.
        control.SelfTestFeed("scroll-anchor\r\n");
        var anchor = Capture(control);
        control.SelfTestScroll(-control.SelfTestRows * 2);
        var scrolledUp = Capture(control);
        control.SelfTestScroll(control.SelfTestRows * 2);
        var snappedBack = Capture(control);
        Report(control, PixelsEqual(anchor, snappedBack) ? "PASS scroll: snap-back identical" : "FAIL scroll: snap-back differs");
        Report(control, PixelsEqual(anchor, scrolledUp) ? "FAIL scroll: scrolled-up unchanged" : "PASS scroll: scrolled-up shows other content");

        // Alt-screen round trip (nvim): the normal screen must come back
        // pixel-identical after the TUI exits.
        var preAlt = Capture(control);
        control.SelfTestFeed("\x1b[?1049h");
        control.SelfTestFeed("tui line one\r\ntui line two\r\n");
        var altShot = Capture(control);
        control.SelfTestFeed("\x1b[?1049l");
        var postAlt = Capture(control);
        Report(control, PixelsEqual(preAlt, postAlt) ? "PASS alt-screen: normal screen restored" : "FAIL alt-screen: normal screen differs");
        Report(control, PixelsEqual(altShot, preAlt) ? "FAIL alt-screen: no change on enter" : "PASS alt-screen: TUI content drawn");

        // Live-like chunked feeding: ConPTY delivers output in many small
        // chunks and typing arrives byte-by-byte, so this exercises the
        // partial-redraw path across many sequential flushes — the pattern
        // that never happens in a single big Feed.
        var chunked = new StringBuilder(20000);
        for (var i = 1; i <= 200; i++)
            chunked.Append("chunked-line-").Append(i).Append(" padding text to make the line reasonably long\r\n");
        var chunkedText = chunked.ToString();
        for (var i = 0; i < chunkedText.Length; i += 64)
            control.SelfTestFeed(chunkedText.Substring(i, Math.Min(64, chunkedText.Length - i)));
        var chunkedShot = Capture(control);
        var chunkedInk = InkBands(control, chunkedShot);
        Report(control, $"after-chunked inkBands={chunkedInk}");
        Report(control, chunkedInk >= 10 ? "PASS chunked: content rendered" : "FAIL chunked: missing content");

        // Typing stress: single characters that wrap and scroll the buffer.
        const string typewriter = "typewritertest";
        for (var i = 0; i < 1200; i++)
            control.SelfTestFeed(typewriter[i % typewriter.Length].ToString());
        control.SelfTestFeed("\r\n");

        control.SelfTestFeed("\x1b[2J\x1b[H");
        var cleared2 = Capture(control);
        var cleared2Ink = InkBands(control, cleared2);
        Report(control, $"after-chunked-clear inkBands={cleared2Ink}");
        Report(control, cleared2Ink <= 2 ? "PASS chunked-clear: screen clean" : "FAIL chunked-clear: ghost rows remain");

        control.SelfTestScroll(-10);
        var chunkScrolledUp = Capture(control);
        control.SelfTestScroll(10);
        var chunkSnappedBack = Capture(control);
        Report(control, PixelsEqual(cleared2, chunkSnappedBack) ? "PASS chunked-scroll: snap-back identical" : "FAIL chunked-scroll: snap-back differs");
        Report(control, PixelsEqual(cleared2, chunkScrolledUp) ? "FAIL chunked-scroll: scrolled-up unchanged" : "PASS chunked-scroll: scrolled-up differs");

        // Caret-over-glyph pass: the block caret draws the cell glyph on the
        // overlay. Two caret moves queue two versions of the overlay; fresh
        // per-draw glyph arrays keep the in-flight first version from
        // rasterizing the second version's glyph (ghost characters behind the
        // cursor while typing fast).
        control.SelfTestFeed("\x1b[2J\x1b[Hab\x1b[2D"); // caret over 'b'
        control.SelfTestFeed("\x1b[1D");                // caret over 'a'
        var caretAtA = Capture(control);
        var bInk = CellInk(control, caretAtA, col: 1, row: 0);
        var aInk = CellInk(control, caretAtA, col: 0, row: 0);
        Report(control, $"caret-race ink a={aInk} b={bInk}");
        Report(control, bInk >= 20 ? "PASS caret-race: b clean" : "FAIL caret-race: b obscured by ghost glyph");
        Report(control, aInk >= 20 ? "PASS caret-race: caret over a" : "FAIL caret-race: caret missing");

        // Popup-menu round trip (nvim pum): a popup drawn with reverse video
        // over two text rows, then closed by restoring those rows the way
        // nvim does (cursor moves + SGR reset + print + EL). Stale pixels
        // would leave a bright inverse band where the popup used to be.
        control.SelfTestFeed("\x1b[2J\x1b[H");
        control.SelfTestFeedBatch(
            "line one\r\nline two\r\nline three\r\nline four\r\nline five\r\nline six\r\n",
            "\x1b[2;1H\x1b[7mITEM ONE     \x1b[27m",
            "\x1b[3;1H\x1b[7mITEM TWO     \x1b[27m",
            "\x1b[1;1H");
        var pumOpen = Capture(control);
        var openCellInverse = CellCornerLum(control, pumOpen, col: 3, row: 1);
        var openCellPlain = CellCornerLum(control, pumOpen, col: 20, row: 1);
        Report(control, $"pum-open corner inverse-cell={openCellInverse} plain-cell={openCellPlain} attrs={CellAttrDump(control, 1)}");
        Report(control, openCellInverse > 160 && openCellPlain < 150 ? "PASS pum: popup rendered" : "FAIL pum: popup not visible");
        control.SelfTestFeedBatch(
            "\x1b[2;1H\x1b[0mline two\x1b[0K",
            "\x1b[3;1H\x1b[0mline three\x1b[0K",
            "\x1b[1;1H");
        var pumClosed = Capture(control);
        var closedCell = CellCornerLum(control, pumClosed, col: 3, row: 1);
        Report(control, $"pum-closed corner cell3-row1={closedCell} attrs={CellAttrDump(control, 1)}");
        Report(control, closedCell < 150 ? "PASS pum: popup pixels cleared" : "FAIL pum: ghost popup remains");

        // Erase with a background color: what TUI apps (nvim, less, btop,
        // tmux, fzf) do to paint their full-screen background. Erased cells
        // have no text and carry the color as cell CONTENT, not style — the
        // screen must be that color everywhere, not just under text.
        control.SelfTestFeed("\x1b[48;2;30;40;60m\x1b[2J\x1b[H\x1b[0m");
        var painted = Capture(control);
        var (p1r, p1g, p1b) = CellCornerRgb(control, painted, col: 5, row: 10);
        var (p2r, p2g, p2b) = CellCornerRgb(control, painted, col: 40, row: 20);
        Report(control, $"bg-paint corners ({p1r},{p1g},{p1b}) ({p2r},{p2g},{p2b})");
        // ±2 tolerance: the windowed capture wobbles by 1 LSB per channel
        // depending on where the window lands (DPI/rounding), so exact
        // equality flakes across runs.
        var p1Ok = Math.Abs(p1r - 30) <= 2 && Math.Abs(p1g - 40) <= 2 && Math.Abs(p1b - 60) <= 2;
        var p2Ok = Math.Abs(p2r - 30) <= 2 && Math.Abs(p2g - 40) <= 2 && Math.Abs(p2b - 60) <= 2;
        Report(control, p1Ok && p2Ok
            ? "PASS bg-paint: erased cells painted everywhere"
            : "FAIL bg-paint: background missing outside text");

        // Replay a captured real nvim session (pum open, Tab accept, pum
        // close) through the exact pump-style feeding and check every cell's
        // rendered fill against its emulator attribute: ghost pixels are
        // cells whose painted background disagrees with the buffer.
        var replayPath = Environment.GetEnvironmentVariable("VEX_SELFTEST_REPLAY");
        if (!string.IsNullOrEmpty(replayPath) && File.Exists(replayPath))
        {
            var replayBytes = File.ReadAllBytes(replayPath);
            control.SelfTestFeed("\x1b[2J\x1b[H");
            control.SelfTestFeedBytes(replayBytes);
            ReplayCheck(control, "replay-end");

            // Mid-stream checks: capture4-style streams have the Tab-close
            // tail ESC[12X (the pum-close frame); opencode-style streams have
            // the title set ESC]0;OpenCode right after the background fill
            // (the "TUI running" frame). Check whichever marks exist.
            var replayText = System.Text.Encoding.Latin1.GetString(replayBytes);
            var marks = new[] { "\u001b[12X", "\u001b]0;OpenCode\u0007", "\u001b]0;OpenCode\u001b\\" };
            foreach (var mark in marks)
            {
                var idx = replayText.IndexOf(mark);
                if (idx <= 0)
                    continue;
                control.SelfTestFeed("\x1b[2J\x1b[H");
                control.SelfTestFeedBytes(replayBytes[..idx]);
                ReplayCheck(control, $"replay-mid({EscapeMark(mark)})");
            }
        }

        // Selection gesture end-to-end: the press-drag-release sequence the
        // mouse handlers run (grid_ref → gesture events → selection snapshot).
        // A GhosttyPoint size mismatch used to make grid_ref fail here and
        // crash the whole app on a plain click.
        control.SelfTestFeed("\x1b[2J\x1b[Hselection test line\r\n");
        control.SelfTestSelect(pressCol: 0, pressRow: 0, dragCol: 4, dragRow: 0);
        var selText = control.SelfTestSelectedText();
        var selShot = Capture(control);
        var selCellLum = CellCornerLum(control, selShot, col: 2, row: 0);
        Report(control, $"selection text='{selText}' bandLum={selCellLum}");
        Report(control, selText == "selec"
            ? "PASS selection: drag selects 'selec'"
            : $"FAIL selection: text='{selText}'");
        Report(control, selCellLum > 170
            ? "PASS selection: band painted"
            : $"FAIL selection: band not painted (lum={selCellLum})");

        // Selecting while scrolled up resolves viewport points into
        // scrollback; it must select content, not throw.
        control.SelfTestScroll(-2);
        string? scrolledSel = null;
        Exception? scrolledErr = null;
        try
        {
            control.SelfTestSelect(pressCol: 2, pressRow: 0, dragCol: 6, dragRow: 0);
            scrolledSel = control.SelfTestSelectedText();
        }
        catch (Exception ex)
        {
            scrolledErr = ex;
        }
        Report(control, $"selection scrolled='{scrolledSel}' err={scrolledErr?.Message ?? "none"}");
        Report(control, scrolledErr is null && !string.IsNullOrEmpty(scrolledSel)
            ? "PASS selection: scrolled-up drag selects"
            : "FAIL selection: scrolled-up drag failed");

        // Keyboard-style selection: the Shift+Arrow handler replays the full
        // press-drag-release gesture (ghostty commits only on release) even
        // though no pointer button exists. The result must be copyable.
        control.SelfTestFeed("\x1b[2J\x1b[Hkeyboard sel line\r\n");
        // The scrolled-selection scenario above leaves the viewport up in
        // scrollback; row 0 must be the freshly fed line again.
        control.SelfTestScrollToBottom();
        control.SelfTestKeyboardSelect(pressCol: 0, pressRow: 0, dragCol: 4, dragRow: 0);
        var kbSel = control.SelfTestSelectedText();
        var kbLum = CellCornerLum(control, Capture(control), col: 2, row: 0);
        Report(control, $"selection keyboard='{kbSel}' bandLum={kbLum}");
        Report(control, kbSel == "keybo"
            ? "PASS selection: keyboard press-drag-release selects"
            : $"FAIL selection: keyboard press-drag-release selected '{kbSel}'");
        Report(control, kbLum > 170
            ? "PASS selection: keyboard band painted"
            : $"FAIL selection: keyboard band not painted (lum={kbLum})");

        // URL detection: a printed link resolves at its cells (and nowhere
        // else), and its underline paints the cell bottom rows — the
        // ctrl+click target for the mouse handler.
        control.SelfTestFeed("\x1b[2J\x1b[Hsee https://github.com/anomalyco/opencode done\r\n");
        control.SelfTestScrollToBottom();
        Report(control, $"link row0='{control.SelfTestRowText(0)}' scroll={control.SelfTestScrollInfo()}");
        var linkUri = control.SelfTestLinkAt(0, 6);
        var linkOffUri = control.SelfTestLinkAt(0, 0);
        Report(control, $"link uri='{linkUri}' off-uri='{linkOffUri ?? "none"}'");
        Report(control, linkUri == "https://github.com/anomalyco/opencode" && linkOffUri is null
            ? "PASS link: url detected at its cells only"
            : "FAIL link: url detection wrong");
        var linkShot = Capture(control);
        var linkLineInk = BottomLineInk(control, linkShot, col: 5, row: 0);
        var plainLineInk = BottomLineInk(control, linkShot, col: 1, row: 0);
        Report(control, $"link-underline ink link-cell={linkLineInk} plain-cell={plainLineInk}");
        Report(control, linkLineInk > 0 && plainLineInk == 0
            ? "PASS link: underline painted under url only"
            : "FAIL link: underline wrong");

        // Link flood: a screen full of URLs (the worst case for the scan)
        // must stay cheap. The feed+flush wall time over repeated floods is
        // reported, not asserted — selftest hosts vary too much for a hard
        // bound.
        var flood = new StringBuilder(40 * 80);
        for (var i = 0; i < 40; i++)
            flood.Append("check https://example.com/path/").Append(i).Append(" and https://github.com/ghostty/ghostty/issues/").Append(i).Append(" ok\r\n");
        control.SelfTestFeed("\x1b[2J\x1b[H");
        control.SelfTestScrollToBottom();
        var floodWatch = System.Diagnostics.Stopwatch.StartNew();
        var floodAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
            control.SelfTestFeed(flood.ToString());
        floodWatch.Stop();
        var floodAlloc = GC.GetAllocatedBytesForCurrentThread() - floodAllocBefore;
        var floodUri = control.SelfTestLinkAt(0, 8);
        Report(control, $"link-flood 20x40lines ms={floodWatch.Elapsed.TotalMilliseconds:F2} allocKB={floodAlloc / 1024.0:F0} sample-uri='{floodUri ?? "none"}'");
        Report(control, floodUri is not null && floodUri.StartsWith("https://example.com/path/")
            ? "PASS link-flood: url resolves"
            : "FAIL link-flood: url missing");
        Report(control, control.SelfTestBenchLinkScan(100));

        Report(control, "done");
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

            var blackColor = ((SolidColorBrush)black!).Color;
            var redColor = ((SolidColorBrush)red!).Color;
            var defaultColor = ((SolidColorBrush)palette.Foreground).Color;
            var expectedRed = FastColor.ParseHex(theme.Red);
            Report(control, blackColor == defaultColor && redColor == expectedRed
                ? $"PASS theme-contrast({theme.Name}): unreadable indexed color repaired"
                : $"FAIL theme-contrast({theme.Name}): black={blackColor} red={redColor}");
        }
    }

    private static byte[] Capture(NativeTerminalControl control)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = Math.Max(1, (int)Math.Round(control.ActualHeight * dpi));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>Hard precondition inside the deterministic suite: reports and
    /// aborts so downstream checks do not read garbage.</summary>
    private static void Assert(NativeTerminalControl control, bool condition, string what)
    {
        if (condition)
            return;
        Report(control, $"FAIL assert: {what}");
        throw new InvalidOperationException($"selftest precondition failed: {what}");
    }

    /// <summary>Finds the horizontal shift in [-maxShift,maxShift] pixels
    /// that best aligns the band spanning columns [colFrom,colTo) of two
    /// viewport rows of one capture. Asserts grid alignment rather than
    /// pixel identity: the same glyphs at the same grid column rasterize
    /// with a slightly different AA phase depending on which run segment
    /// carries them, so raw diffs never reach zero even when aligned —
    /// but a real drift (whole-run font fallback moves the text by the
    /// emoji's natural-vs-grid advance difference) shifts the argmin away
    /// from 0 decisively.</summary>
    /// <returns>(bestShift, diffAtBest, diffAtZero).</returns>
    private static (int BestShift, int DiffAtBest, int DiffAtZero) BandAlignment(
        NativeTerminalControl control, byte[] pixels, int rowA, int rowB, int colFrom, int colTo, int maxShift)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var x0 = (int)Math.Round(colFrom * control.SelfTestCellWidth * dpi);
        var x1 = Math.Min(width - maxShift, (int)Math.Round(colTo * control.SelfTestCellWidth * dpi));
        var yA0 = (int)Math.Round(rowA * control.SelfTestCellHeight * dpi);
        var yA1 = Math.Min(height, (int)Math.Round((rowA + 1) * control.SelfTestCellHeight * dpi));
        var yB0 = (int)Math.Round(rowB * control.SelfTestCellHeight * dpi);
        var yB1 = Math.Min(height, (int)Math.Round((rowB + 1) * control.SelfTestCellHeight * dpi));
        var rows = Math.Min(yA1 - yA0, yB1 - yB0);

        int DiffAt(int shift)
        {
            var diff = 0;
            for (var dy = 0; dy < rows; dy++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var ia = ((yA0 + dy) * width + x) * 4;
                    var ib = ((yB0 + dy) * width + x + shift) * 4;
                    if (Math.Abs(pixels[ia] - pixels[ib]) > 8 ||
                        Math.Abs(pixels[ia + 1] - pixels[ib + 1]) > 8 ||
                        Math.Abs(pixels[ia + 2] - pixels[ib + 2]) > 8)
                        diff++;
                }
            }
            return diff;
        }

        var bestShift = 0;
        var diffAtBest = int.MaxValue;
        var diffAtZero = DiffAt(0);
        for (var s = -maxShift; s <= maxShift; s++)
        {
            var d = DiffAt(s);
            if (d < diffAtBest)
            {
                diffAtBest = d;
                bestShift = s;
            }
        }
        return (bestShift, diffAtBest, diffAtZero);
    }

    /// <summary>Writes a raw Pbgra32 capture to disk for visual inspection.</summary>
    internal static void DumpPng(NativeTerminalControl control, byte[] pixels, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var bitmap = BitmapSource.Create(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static bool PixelsEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    /// <summary>Number of row bands that carry ink (text or stale pixels). The
    /// scrollbar overlay occupies the right edge, so columns there are ignored.</summary>
    private static int InkBands(NativeTerminalControl control, byte[] pixels)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var rowPixels = Math.Max(1, (int)Math.Round(control.SelfTestCellHeight * dpi));
        var textWidth = width - (int)Math.Round(24 * dpi);

        // Background reference: the darkest pixels anywhere (text is brighter).
        var minLum = 765;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Pbgra32: premultiplied, but channel sums stay ordered for our
            // opaque-over-black base fill.
            var lum = pixels[i] + pixels[i + 1] + pixels[i + 2];
            if (lum < minLum)
                minLum = lum;
        }

        var bands = new StringBuilder();
        var y = 0;
        while (y < height)
        {
            var bandEnd = Math.Min(y + rowPixels, height);
            var hasInk = false;
            for (var py = y; py < bandEnd && !hasInk; py += 2)
            {
                var rowStart = py * width * 4;
                for (var px = 0; px < textWidth * 4; px += 8)
                {
                    var lum = pixels[rowStart + px] + pixels[rowStart + px + 1] + pixels[rowStart + px + 2];
                    if (lum > minLum + 90)
                    {
                        hasInk = true;
                        break;
                    }
                }
            }
            if (hasInk)
                bands.Append(y / rowPixels).Append(',');
            y += rowPixels;
        }
        Report(control, $"ink bands at: {bands}");
        return bands.Length == 0 ? 0 : bands.ToString().TrimEnd(',').Split(',').Length;
    }

    /// <summary>Tagged colors and flags of the first few cells of a row — the
    /// raw emulator state behind whatever pixels got rendered.</summary>
    private static string CellAttrDump(NativeTerminalControl control, int row)
    {
        return CellAttrDumpRange(control, row, 0, 8);
    }

    private static string CellAttrDumpRange(NativeTerminalControl control, int row, int start, int count)
    {
        var line = control.SelfTestLine(row);
        if (line is null)
            return "no-line";
        var sb = new StringBuilder();
        var cells = line.Cells;
        for (var c = start; c < start + count && c < cells.Length; c++)
        {
            var cell = cells[c];
            sb.Append($"{c}:'{(cell.Text.Length == 0 ? '·' : cell.Text)}'={(byte)cell.Flags:X2}/{cell.FgTag}{(cell.FgTag == ColorTag.Rgb ? cell.FgValue.ToString("X6") : cell.FgValue.ToString())} ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Replays a captured stream and asserts that every checked cell's
    /// painted background fill agrees with its emulator attribute: ghost
    /// pixels are cells whose rendered fill disagrees with the buffer.</summary>
    private static void ReplayCheck(NativeTerminalControl control, string label)
    {
        var shot = Capture(control);
        var caret = control.SelfTestCaretCell();
        var disagreements = 0;
        for (var row = 0; row < Math.Min(control.SelfTestRows, 12); row++)
        {
            var line = control.SelfTestLine(row);
            if (line is null)
                continue;
            var cells = line.Cells;
            for (var col = 0; col < Math.Min(24, cells.Length); col++)
            {
                // The block caret repaints its cell (cursor color); pixels
                // there are not cell content, so skip it.
                if (caret is { Row: var cr, Col: var cc } && row == cr && col == cc)
                    continue;
                ref readonly var cell = ref cells[col];
                // Cells with text legitimately carry glyph ink at any corner,
                // and the cell right after a text cell can carry the
                // overflow of a font-wide glyph (emoji are single-column in
                // ghostty's grid but their fallback glyphs are not);
                // background-fill verification (ghost pixel detection) only
                // makes sense on cells away from text.
                if (cell.Text.Length > 0)
                    continue;
                if (col > 0 && cells[col - 1].Text.Length > 0)
                    continue;
                var (bg, baseColor) = control.SelfTestResolveCell(cell);
                var expected = bg ?? baseColor;
                // Premultiplied over the (opaque-ish) backdrop: compare the
                // corner pixel channels to the resolved color's.
                var (r, g, b) = CellCornerRgb(control, shot, col, row);
                var tol = 60;
                if (Math.Abs(r - expected.R) > tol || Math.Abs(g - expected.G) > tol || Math.Abs(b - expected.B) > tol)
                {
                    disagreements++;
                    if (disagreements <= 10)
                        Report(control, $"{label} disagree row{row} col{col} flags={(byte)cell.Flags:X2} fg={cell.FgTag}:{cell.FgValue} bg={cell.BgTag}:{cell.BgValue} pixel=({r},{g},{b}) expected=({expected.R},{expected.G},{expected.B})");
                }
            }
        }
        Report(control, disagreements == 0 ? $"PASS {label}: pixels match buffer" : $"FAIL {label}: {disagreements} cells disagree");
    }

    private static string EscapeMark(string mark) =>
        string.Concat(mark.Select(c => c < 0x20 ? $"<{(int)c:x2}>" : c.ToString()));

    /// <summary>RGB at a cell's top-right corner — background fill only,
    /// away from glyph strokes.</summary>
    private static (byte R, byte G, byte B) CellCornerRgb(NativeTerminalControl control, byte[] pixels, int col, int row)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var x = Math.Min(width - 1, (int)Math.Round((col + 0.85) * control.SelfTestCellWidth * dpi));
        var y = Math.Min(height - 1, (int)Math.Round((row + 0.12) * control.SelfTestCellHeight * dpi));
        var i = (y * width + x) * 4;
        return (pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    /// <summary>Luminance at a cell's top-right corner — background fill only,
    /// away from glyph strokes.</summary>
    private static int CellCornerLum(NativeTerminalControl control, byte[] pixels, int col, int row)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var x = Math.Min(width - 1, (int)Math.Round((col + 0.85) * control.SelfTestCellWidth * dpi));
        var y = Math.Min(height - 1, (int)Math.Round((row + 0.12) * control.SelfTestCellHeight * dpi));
        var i = (y * width + x) * 4;
        return pixels[i] + pixels[i + 1] + pixels[i + 2];
    }

    /// <summary>Darkest channel sum in the frame: the base background fill.</summary>
    private static int MinLum(byte[] pixels)
    {
        var minLum = 765;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var lum = pixels[i] + pixels[i + 1] + pixels[i + 2];
            if (lum < minLum)
                minLum = lum;
        }
        return minLum;
    }

    /// <summary>Number of bright pixels inside the cell at (col,row): how much
    /// of the glyph there survives (a background-colored ghost glyph painted
    /// over it would eat into the ink).</summary>
    private static int CellInk(NativeTerminalControl control, byte[] pixels, int col, int row)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var minLum = 765;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var lum = pixels[i] + pixels[i + 1] + pixels[i + 2];
            if (lum < minLum)
                minLum = lum;
        }

        var x0 = (int)Math.Round(col * control.SelfTestCellWidth * dpi);
        var x1 = (int)Math.Round((col + 1) * control.SelfTestCellWidth * dpi);
        var y0 = (int)Math.Round(row * control.SelfTestCellHeight * dpi);
        var y1 = (int)Math.Round((row + 1) * control.SelfTestCellHeight * dpi);
        var ink = 0;
        for (var y = y0; y < y1 && y < height; y++)
        {
            for (var x = x0; x < x1 && x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > minLum + 90)
                    ink++;
            }
        }
        return ink;
    }

    /// <summary>Inked pixels along the bottom two pixel rows of a cell, where
    /// SGR and detected-link underlines land.</summary>
    private static int BottomLineInk(NativeTerminalControl control, byte[] pixels, int col, int row)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var minLum = 765;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var lum = pixels[i] + pixels[i + 1] + pixels[i + 2];
            if (lum < minLum)
                minLum = lum;
        }

        var x0 = (int)Math.Round(col * control.SelfTestCellWidth * dpi);
        var x1 = (int)Math.Round((col + 1) * control.SelfTestCellWidth * dpi);
        var y1 = (int)Math.Round((row + 1) * control.SelfTestCellHeight * dpi);
        var y0 = Math.Max(0, y1 - 2);
        var ink = 0;
        for (var y = y0; y < y1 && y < height; y++)
        {
            for (var x = x0; x < x1 && x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > minLum + 90)
                    ink++;
            }
        }
        return ink;
    }

    /// <summary>True when the cell at (col,row) contains any ink.</summary>
    private static bool HasInkAt(NativeTerminalControl control, byte[] pixels, int col, int row)
    {
        var dpi = VisualTreeHelper.GetDpi(control).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpi));
        var height = pixels.Length / (width * 4);
        var minLum = 765;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var lum = pixels[i] + pixels[i + 1] + pixels[i + 2];
            if (lum < minLum)
                minLum = lum;
        }

        var cx = (int)Math.Round((col + 0.5) * control.SelfTestCellWidth * dpi);
        var cy = (int)Math.Round((row + 0.5) * control.SelfTestCellHeight * dpi);
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                if (x < 0 || y < 0 || x >= width || y >= height)
                    continue;
                var i = (y * width + x) * 4;
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > minLum + 90)
                    return true;
            }
        }
        return false;
    }
}
