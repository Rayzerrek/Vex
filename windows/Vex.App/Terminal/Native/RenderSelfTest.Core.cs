#if DEBUG || VEX_SELFTEST
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Vex.App.Model;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

internal static partial class RenderSelfTest
{
    private static void RunCore(NativeTerminalControl control)
    {
        Report(control, $"start cols={control.SelfTestCols} rows={control.SelfTestRows} cell={control.SelfTestCellWidth:0.0}x{control.SelfTestCellHeight:0.0}");

        CheckStartupOverlays(control);

        // Bitmap targets do not preserve screen subpixel antialiasing, so
        // verify the live surface's mode rather than its captured pixels.
        Assert(control, TextOptions.GetTextRenderingMode(control) == TextRenderingMode.ClearType,
            "font smoothing: opaque terminal uses ClearType");

        // A steady caret so bitmaps from different captures are comparable.
        control.SelfTestStabilizeCaret();

        CheckIndexedThemeContrast(control);
        CheckBlockElementContrast(control);
        foreach (var dpi in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            foreach (var blocks in new[] { false, true })
                Report(control, control.SelfTestCellFillSeams(dpi, blocks)
                    ? $"PASS cell-fill seams: dpi={dpi} blocks={blocks} solid pixels across row and run boundaries"
                    : $"FAIL cell-fill seams: dpi={dpi} blocks={blocks} gaps in solid color");
        }

        control.SelfTestPathCompletion(() => DumpPng(control, Capture(control), Path.ChangeExtension(ReportPath!, ".path-completion.png")));
        Report(control, "PASS path completion: fuzzy filtering, keyboard navigation, paste, alternate screen and cleanup");

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
        CheckGlyphGridAlignment(control);
        CheckFontGridAlignment(control);
        Report(control, control.SelfTestFallbackDpiChange()
            ? "PASS fallback DPI: retained glyphs match the new monitor scale"
            : "FAIL fallback DPI: retained glyphs use the previous monitor scale");
        Report(control, control.SelfTestGlyphAdvanceDpiChange()
            ? "PASS glyph advances: retained spacing is immutable and matches the new monitor scale"
            : "FAIL glyph advances: cached spacing changed or uses the previous monitor scale");

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

        CheckCopyFeedback(control);

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

        // Rapid repeats must extend by characters, never turn into mouse multi-clicks.
        control.SelfTestClearSelection();
        control.SelfTestFeed("\x1b[2J\x1b[Hkeyboard sel line\x1b[H");
        // The scrolled-selection scenario above leaves the viewport up in
        // scrollback; row 0 must be the freshly fed line again.
        control.SelfTestScrollToBottom();
        for (var i = 0; i < 5; i++)
        {
            control.SelfTestKeyboardSelectionKey(Key.Right);
            Assert(control, control.SelfTestSelectedText() == "keyboard"[..(i + 1)],
                $"selection: Shift+Right repeat {i + 1} selects exactly {i + 1} characters");
        }
        var kbSel = control.SelfTestSelectedText();
        var kbLum = CellCornerLum(control, Capture(control), col: 2, row: 0);
        Report(control, $"selection keyboard='{kbSel}' bandLum={kbLum}");
        Report(control, kbSel == "keybo"
            ? "PASS selection: keyboard caret range selects"
            : $"FAIL selection: keyboard caret range selected '{kbSel}'");
        Report(control, kbLum > 170
            ? "PASS selection: keyboard band painted"
            : $"FAIL selection: keyboard band not painted (lum={kbLum})");

        for (var i = 0; i < 5; i++)
            control.SelfTestKeyboardSelectionKey(Key.Left);
        Assert(control, control.SelfTestSelectedText() is null, "selection: returning to anchor clears selection");
        control.SelfTestKeyboardSelectionKey(Key.Right, byWord: true);
        Assert(control, control.SelfTestSelectedText() == "keyboard", "selection: word-right stops after word");
        control.SelfTestKeyboardSelectionKey(Key.Left, byWord: true);
        Assert(control, control.SelfTestSelectedText() is null, "selection: word-left returns to anchor");
        control.SelfTestClearSelection();
        control.SelfTestFeed("\x1b[1;9H");
        for (var i = 0; i < 5; i++)
        {
            control.SelfTestKeyboardSelectionKey(Key.Left);
            Assert(control, control.SelfTestSelectedText() == "keyboard"[(7 - i)..],
                $"selection: Shift+Left repeat {i + 1} selects exactly {i + 1} characters");
        }
        control.SelfTestClearSelection();

        control.SelfTestFeed("\x1b[2J\x1b[Halpha beta");
        for (var i = 0; i < 10; i++)
            control.SelfTestKeyboardSelectionKey(Key.Left);
        Assert(control, control.SelfTestSelectionBackspace().SequenceEqual(Enumerable.Repeat((byte)0x7f, 10)),
            "selection: Backspace deletes all selected input, including spaces");
        Assert(control, control.SelfTestSelectedText() is null, "selection: Backspace clears selection");

        foreach (var inputLength in new[] { control.SelfTestCols - 1, control.SelfTestCols,
            control.SelfTestCols + 1, control.SelfTestCols * 2, control.SelfTestCols * 2 + 7 })
        {
            var longInput = new string('x', inputLength - 1) + "Z";
            control.SelfTestFeed("\x1b[2J\x1b[H" + longInput);
            control.SelfTestKeyboardSelectionKey(Key.Left);
            Assert(control, control.SelfTestSelectedText() == "Z",
                $"selection: Shift+Left includes the last character of {inputLength}-character input");
            control.SelfTestKeyboardSelectionKey(Key.Right);
            Assert(control, control.SelfTestSelectedText() is null,
                $"selection: returning to anchor clears {inputLength}-character input selection");
            for (var i = 0; i < inputLength; i++)
                control.SelfTestKeyboardSelectionKey(Key.Left);
            Assert(control, control.SelfTestSelectedText() == longInput,
                $"selection: selects all {inputLength} characters across soft wraps");
            Assert(control, control.SelfTestSelectionBackspace().SequenceEqual(Enumerable.Repeat((byte)0x7f, inputLength)),
                $"selection: Backspace deletes all {inputLength} characters across soft wraps");
        }

        control.SelfTestFeed("\x1b[2J\x1b[H  e\u0301\U0001F9EA  ");
        for (var i = 0; i < 7; i++)
            control.SelfTestKeyboardSelectionKey(Key.Left);
        Assert(control, control.SelfTestSelectionBackspace().Length == 6,
            "selection: deletion preserves spaces and counts Unicode graphemes, not UTF-16 units");

        control.SelfTestFeed("\x1b[2J\x1b[Houtput\r\ninput");
        control.SelfTestKeyboardSelectionKey(Key.Up);
        Assert(control, control.SelfTestSelectionBackspace().Length == 1,
            "selection: crossing hard newlines keeps ordinary Backspace behavior");

        control.SelfTestFeed("\x1b[2J\x1b[Halpha beta\x1b[1;7H");
        control.SelfTestKeyboardSelectionKey(Key.Right);
        Assert(control, control.SelfTestSelectionBackspace().Length == 1,
            "selection: text beyond the shell cursor is not bulk-deleted");

        var copyOnSelect = AppSettings.Instance.CopyOnSelect;
        try
        {
            AppSettings.Instance.CopyOnSelect = false;
            control.SelfTestFeed("\x1b[2J\x1b[Hselection stays");
            control.SelfTestSelect(0, 0, 4, 0);
            control.SelfTestSelectionMouseUp();
            Assert(control, control.SelfTestSelectedText() == "selec",
                "selection: disabling copy on select retains mouse selection on release");
        }
        finally
        {
            AppSettings.Instance.CopyOnSelect = copyOnSelect;
            control.SelfTestClearSelection();
        }

        // Mouse input must follow the mode negotiated by the TUI rather than
        // always emitting SGR. These asymmetric cases catch both a hard-coded
        // encoder and forwarding drag events in mode 1000.
        var mouseSgr = control.SelfTestMouseReport("\x1b[?1000h\x1b[?1006h",
            MouseInputAction.Press, MouseInputButton.Left, anyButtonPressed: true);
        var mouseUrxvt = control.SelfTestMouseReport("\x1b[?1000h\x1b[?1015h",
            MouseInputAction.Press, MouseInputButton.Left, anyButtonPressed: true);
        var mouseFilteredMotion = control.SelfTestMouseReport("\x1b[?1000h\x1b[?1006h",
            MouseInputAction.Motion, MouseInputButton.Left, anyButtonPressed: true);
        var mouseAnyMotion = control.SelfTestMouseReport("\x1b[?1003h\x1b[?1006h",
            MouseInputAction.Motion, null, col: 7);
        Report(control, $"mouse sgr='{mouseSgr}' urxvt='{mouseUrxvt}' normal-motion='{mouseFilteredMotion}' any-motion='{mouseAnyMotion}'");
        Report(control, mouseSgr == "<ESC>[<0;6;3M" && mouseUrxvt == "<ESC>[32;6;3M"
            ? "PASS mouse: negotiated SGR and URXVT formats"
            : "FAIL mouse: negotiated format encoding wrong");
        Report(control, mouseFilteredMotion.Length == 0 && mouseAnyMotion == "<ESC>[<35;8;3M"
            ? "PASS mouse: tracking modes filter motion correctly"
            : "FAIL mouse: tracking mode filtering wrong");

        // A TUI that resets one tracking mode while another is still set
        // (e.g. 1000h 1002h 1000l) must keep reporting: routing reads the
        // live mode bits and the encoder must agree with them. The cached
        // last-transition flag this used to rely on collapses to "none"
        // here, silently emitting zero bytes for every event.
        var mouseDivergent = control.SelfTestMouseReport("\x1b[?1002h\x1b[?1000h\x1b[?1006h\x1b[?1000l",
            MouseInputAction.Press, MouseInputButton.Left, anyButtonPressed: true);
        Report(control, $"mouse divergent tracking={control.SelfTestMouseTracking} report='{mouseDivergent}'");
        Report(control, control.SelfTestMouseTracking && mouseDivergent == "<ESC>[<0;6;3M"
            ? "PASS mouse: mixed tracking-mode reset keeps reporting"
            : "FAIL mouse: mixed tracking-mode reset went silent");
        var wheelForward = control.SelfTestWheelSteps(40, 40, 40);
        var wheelReverse = control.SelfTestWheelSteps(-60, -60);
        Report(control, wheelForward.SequenceEqual(new[] { 0, 0, 1 }) &&
                        wheelReverse.SequenceEqual(new[] { 0, -1 })
            ? "PASS mouse: precision wheel deltas accumulate"
            : "FAIL mouse: precision wheel accumulation wrong");
        Report(control, control.SelfTestBenchMouseEncoding(10_000));

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
        // Exercise an already-hovered URL, as when the desktop pointer is
        // on it. Normalize the baseline before comparing hover feedback.
        control.SelfTestLinkHover(6, 0);
        control.SelfTestLinkHover(0, 0);
        var linkShot = Capture(control);
        var linkLineInk = BottomLineInk(control, linkShot, col: 5, row: 0);
        var plainLineInk = BottomLineInk(control, linkShot, col: 1, row: 0);
        Report(control, $"link-underline ink link-cell={linkLineInk} plain-cell={plainLineInk}");
        Report(control, linkLineInk > 0 && plainLineInk == 0
            ? "PASS link: underline painted under url only"
            : "FAIL link: underline wrong");

        control.SelfTestLinkHover(6, 0);
        var hoveredLinkShot = Capture(control);
        Assert(control, !PixelsEqual(linkShot, hoveredLinkShot), "link hover: URL visibly highlighted");
        Assert(control, CellCornerLum(control, linkShot, 1, 0) == CellCornerLum(control, hoveredLinkShot, 1, 0),
            "link hover: neighboring text unchanged");
        Assert(control, control.SelfTestLinkPreview(Path.ChangeExtension(ReportPath!, ".link-preview.png")),
            "link hover: delayed address preview opens");
        control.SelfTestLinkHover(0, 0);
        Assert(control, PixelsEqual(linkShot, Capture(control)), "link hover: leaving URL clears feedback");
        Report(control, "PASS link hover: highlight, delayed preview, clean dismissal");

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
        foreach (var visibility in new[] { Visibility.Hidden, Visibility.Collapsed })
        {
            Report(control, control.SelfTestHiddenOutputCatchUp(visibility)
                ? $"PASS visibility {visibility}: hidden output deferred, events live, reveal catches up"
                : $"FAIL visibility {visibility}: hidden work or missing event/output");
            ParityCheck(control, $"visibility-{visibility}");
        }
        Report(control, control.SelfTestDetachedOutputCatchUp()
            ? "PASS visibility detached: latest output appears after unchanged-size reload without invalidating font caches"
            : "FAIL visibility detached: latest output missing or font caches invalidated after unchanged-size reload");
        ParityCheck(control, "visibility-detached");
        Report(control, control.SelfTestInputPrecedesOutputRedraw()
            ? "PASS scheduling: input precedes output redraw and final output is applied"
            : "FAIL scheduling: output redraw delays input or final output is missing");

        Report(control, control.SelfTestCursorBlinkModes()
            ? "PASS cursor blink modes: steady/hidden stop timer, steady stays painted, reveal restarts"
            : "FAIL cursor blink modes: stale timer or incorrect steady/hidden caret");
        ParityCheck(control, "cursor-blink-modes");
        Report(control, control.SelfTestTerminalPreview()
            ? "PASS terminal preview: live hidden frames, immutable cache, atomic output and unchanged geometry"
            : "FAIL terminal preview: stale frame, resize or torn synchronized output");
        Report(control, Window.GetWindow(control) is MainWindow window && window.SelfTestTabPeek(control)
            ? "PASS tab peek: overview opens without resizing or activating a terminal and stops work on close"
            : "FAIL tab peek: terminal geometry changed or preview work survived dismissal");
        Report(control, "done");
    }

    private static void RunStartupOnly(NativeTerminalControl control)
    {
        Report(control, $"startup start cols={control.SelfTestCols} rows={control.SelfTestRows}");
        CheckStartupOverlays(control);
        control.SelfTestStabilizeCaret();
        control.SelfTestFeed("\u001b[2J\u001b[HVex startup 漢🦀e\u0301\r\n\u001b[31mcolor\u001b[0m");
        ParityCheck(control, "startup-text");
        control.SelfTestFeed("\u001b[2J\u001b[H");
        ParityCheck(control, "startup-clear");
        Report(control, "startup done");
    }

    private static void CheckStartupOverlays(NativeTerminalControl control)
    {
        var window = Window.GetWindow(control) as MainWindow
            ?? throw new InvalidOperationException("Startup self-test terminal has no MainWindow host.");
        Assert(control, window.SelfTestDeferredPopups(), "deferred popups: bindings, placement and confirmation");
        Assert(control, window.SelfTestStartupOverlays(), "startup overlays: settings, command palette and theme switcher");
        Assert(control, window.SelfTestSavedLayouts(), "saved layouts: deferred editor and pane bindings");
        Assert(control, window.SelfTestShellSelection(), "shell selection: dropdown, split submenu and independent project defaults");
        Assert(control, control.SelfTestProgramStatusBatch(), "program status: bounded UI updates and latched attention");
        Report(control, "PASS startup overlays: popups, settings, command palette and theme switcher");
    }

    /// <summary>
    /// Deterministic incremental-repaint parity. Streams agent-style output in
    /// small chunks, scrolls, resizes the grid, and overwrites lines in place —
    /// capturing after every phase and comparing the incrementally painted
    /// pixels against a full repaint of the very same emulator buffer. Any
    /// difference is a pixel the incremental path left stale: the duplicated /
    /// ghost lines seen while an agent streams. Also probes blank cells for
    /// leftover ink, which catches a glyph that survived an overwrite.
    /// </summary>
}
#endif
