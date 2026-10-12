#if DEBUG || VEX_SELFTEST
using System.IO;
using System.Windows.Threading;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

internal static partial class RenderSelfTest
{
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
        steps.Enqueue((4000, () => control.SelfTestType("let h = (gh help); 1..200 | each { print $h }\r")));
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
            ShutdownSelfTest();
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
            ShutdownSelfTest();
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
            ShutdownSelfTest();
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
            ShutdownSelfTest();
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
    /// Live mouse round-trip — the reported regression: open nvim, drag a
    /// selection with the mouse (reported through the app's real SendMouse
    /// path), then press d. nvim must enter Visual mode from the reported
    /// events and delete the selection. Also asserts tracking engaged from
    /// nvim's own output, so a TUI that never enables the mouse (or a build
    /// whose tracking detection is broken) is reported as such.
    /// </summary>
    private static void RunLiveMouse(NativeTerminalControl control)
    {
        var dir = string.IsNullOrEmpty(LiveDir) ? Path.GetTempPath() : LiveDir;
        Report(control, $"live-mouse start cols={control.SelfTestCols} rows={control.SelfTestRows}");
        try
        {
            control.SelfTestStabilizeCaret();
            control.SelfTestStartSession();
        }
        catch (Exception e)
        {
            Report(control, $"live-mouse setup EXCEPTION {e.GetType().Name}: {e.Message}");
            ShutdownSelfTest();
            return;
        }

        var testFile = Path.Combine(dir, "vex-live-mouse.txt");
        File.WriteAllText(testFile, "apple banana cherry\n");

        var steps = new Queue<(int DelayMs, Action Act)>();
        steps.Enqueue((2000, () => control.SelfTestType($"nvim --clean -i NONE -n '{testFile}'\r")));
        // nvim must have negotiated mouse tracking from its own startup
        // output (mode 1002 + 1006 on nvim 0.10+).
        steps.Enqueue((2500, () =>
        {
            Shot(control, Path.Combine(dir, "mouse-01-open.png"));
            Report(control, $"live-mouse tracking={control.SelfTestMouseTracking} {control.SelfTestCursorInfo()}");
            Report(control, control.SelfTestMouseTracking
                ? "PASS mouse: nvim tracking engaged"
                : "FAIL mouse: nvim tracking never engaged (nvim <0.10 or detection broken)");
        }));
        // Press at 'b' of banana (col 6), drag to its last 'a' (col 11),
        // release: nvim should highlight "banana" in Visual mode.
        steps.Enqueue((300, () => control.SelfTestMouse(MouseInputAction.Press, MouseInputButton.Left, col: 6, row: 0)));
        steps.Enqueue((100, () => control.SelfTestMouse(MouseInputAction.Motion, MouseInputButton.Left, col: 9, row: 0)));
        steps.Enqueue((100, () => control.SelfTestMouse(MouseInputAction.Motion, MouseInputButton.Left, col: 11, row: 0)));
        steps.Enqueue((200, () => control.SelfTestMouse(MouseInputAction.Release, MouseInputButton.Left, col: 11, row: 0)));
        steps.Enqueue((600, () =>
        {
            Shot(control, Path.Combine(dir, "mouse-02-selected.png"));
            Report(control, $"live-mouse row0='{control.SelfTestRowText(0)}'");
        }));
        // Lowercase d deletes the selected characters; Visual D deletes whole lines.
        steps.Enqueue((200, () => control.SelfTestType("d")));
        steps.Enqueue((800, () =>
        {
            Shot(control, Path.Combine(dir, "mouse-03-deleted.png"));
            var row0 = control.SelfTestRowText(0) ?? "";
            Report(control, $"live-mouse after-d row0='{row0}'");
            Report(control, row0 == "apple  cherry"
                ? "PASS mouse: nvim deleted exactly the mouse selection on d"
                : "FAIL mouse: selection not deleted exactly by d");
            Report(control, "live-mouse done");
            ShutdownSelfTest();
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
            Report(control, $"live-mouse step remaining={steps.Count} delay={delay}");
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
            // The regression: after a TUI exits the shell redraws its prompt,
            // and the stale OSC 133;A marker used to force a line feed — so the
            // prompt landed twice, on consecutive rows. Assert the invariant
            // rather than one shell's row layout: no two adjacent rows may
            // carry identical text, and the exited command line appears once.
            var duplicateRow = -1;
            var commandRows = 0;
            var previous = "";
            for (var r = 0; r < control.SelfTestRows; r++)
            {
                var current = control.SelfTestRowText(r);
                if (current.Contains("nvim --clean"))
                    commandRows++;
                if (current.Length > 0 && current == previous)
                    duplicateRow = r;
                previous = current;
            }
            Report(control, duplicateRow < 0 && commandRows == 1
                ? "PASS prompt: single prompt after TUI exit"
                : $"FAIL prompt: duplicated row{duplicateRow} or {commandRows} command-line rows");
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
            ShutdownSelfTest();
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
}
#endif
