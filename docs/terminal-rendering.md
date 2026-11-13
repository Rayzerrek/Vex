# Terminal rendering and compatibility repair plan

Vex's Windows path is application → ConPTY/OpenConsole → Ghostty VT →
cell snapshots → DirectWrite/D2D masks → D3D12. Both ConPTY and Ghostty
interpret terminal state. A correct renderer alone cannot repair a disagreement
between their grids or an incorrectly negotiated application protocol.

This is a source-level repair plan, not a desktop validation report.

## Font selection

`app/src/windows/renderer/terminal_font.cpp` now resolves the requested family
against the system collection before creating its four styles. Missing families
fall back to Consolas, Cascadia Mono, Lucida Console, then Courier New; if none
exists, creation fails rather than accepting an arbitrary proportional face.
DirectWrite's successful `CreateTextFormat` is not evidence that the requested
family exists. This matters for the default Cascadia Mono on machines where it
is not installed system-wide.

The font fixture checks a deliberately missing family, the resolved family name,
and actual narrow/wide Latin advances in all four styles. Existing font tests
cover ink bounds and rasterization, not visual parity with Windows Terminal.

## Text rasterization: remaining differences

`app/src/windows/renderer/terminal_renderer.cpp` rasterizes white glyphs through
a software WIC/D2D target with grayscale antialiasing. It uploads only alpha to
an `R8_UNORM` atlas. Terminal text now uses linear DirectWrite masks and applies
foreground-dependent gamma/contrast at compositing time, using the system's
DirectWrite rendering parameters and the Microsoft Terminal blending polynomial.
Previously the atlas baked in white text's correction and reused it for all colors.
Procedural graphics and UI masks keep their separate, uncorrected shader path;
glyph cache keys distinguish linear text from ordinary masks.

There is still no RGB subpixel coverage or color-emoji atlas path.

Windows Terminal's Atlas renderer has separate grayscale, ClearType and
passthrough paths, with rendering-parameter-based gamma/contrast correction.
Using DirectWrite in both applications therefore does not imply identical text.
Vex also quantizes text origins down to quarter-pixel phases and scales some
ASCII runs horizontally to fit their assigned cells. Compare identical installed
families, physical font sizes and DPI before attributing every difference to AA.

Recommended implementation order:

1. Maintain grayscale reference renders against DirectWrite on light/dark
   backgrounds, including colored text at 100%, 125%, 150% and 200% DPI.
   The WARP fixture compares actual composited pixels for Cascadia Mono
   (or its monospace fallback) and Consolas, all four styles, and cache reuse.
2. Keep raw glyph coverage separate from display-dependent gamma/contrast.
   Do not apply another arbitrary gamma curve to already corrected D2D masks,
   or apply text correction to procedural box/block graphics.
3. Add optional ClearType only with RGB coverage and appropriate compositing.
   Merely switching `SetTextAntialiasMode` while retaining alpha-only upload
   cannot preserve ClearType. Retain grayscale for unsupported surfaces.
4. Add a color-glyph path and test emoji, variation selectors, combining marks,
   fallback baselines and private-use icons.

## Resize and output ordering

`TerminalSession.resize` now distinguishes grid changes from pixel-metric
changes: only rows/columns require a ConPTY resize. The emulator and DEC 2048
clients still receive pixel-metric updates.

The larger risks remain:

- `WM_SIZE` waits for the GPU and recreates swap-chain buffers before updating
  workspace metrics. This serializes resize with rendering work.
- The PTY reader has one 64 KiB handoff block, then waits for the UI to parse
  and acknowledge it. The UI also submits ConPTY resizes. The upstream resize
  implementation synchronously writes a signal packet; success is **not**
  confirmation that OpenConsole has finished resizing or drained old output.
- Ghostty resizes immediately, while old-grid output may still be pending in
  the handoff or pipe. Multiple intermediate sizes can accumulate during a drag.
  This is a source-level ordering risk, not proof of a particular observed
  screen corruption or a reproduced deadlock.

Do not fix this by dropping output, clearing the screen, injecting a terminal
reset, or requesting an undocumented universal TUI redraw.

First add an end-to-end transport regression that interleaves sustained output,
rapid width/height changes and a final cursor/size query. Cover both the primary
and alternate screen, native Windows console APIs and WSL TUIs. Check actual
screen contents and cursor placement after settling, not just process survival.
Then introduce bounded latest-size coalescing and an explicit output/resize
ordering policy. Moving resize to a worker alone does not solve stale-grid
output or establish a resize acknowledgement boundary. Keep teardown and
pane-local error handling covered.

## Other compatibility differences to isolate

- `app/src/zig/windows/windows_ffi.zig` deliberately remaps dominant
  opposite-theme RGB palettes and boosts text contrast on light cell backgrounds
  (relative luminance at least 0.5). Dark and midtone cell backgrounds preserve
  their text colors, including intentionally dim text. The threshold uses the
  actual resolved cell background, not merely the host theme name.
  These are presentation policies, not VT decoding. They can make an application's
  explicit colors look different from Windows Terminal. A faithful-color
  diagnostic mode would help separate this behavior from rendering defects;
  do not silently remove these existing policies.
- Neutral program backgrounds are no longer guessed to be host selections.
  That heuristic recolored text/blank cells but exempted block glyphs, breaking
  half-block borders that use a foreground matching the neighboring background.
  Host selection colors still apply to actual selected cells. A real-parser
  regression checks text, blank cells, half-block borders and edge padding
  consistently across all themes.
- `app/src/zig/terminal/terminal_feed.zig` filters known ConPTY-stripped query
  payloads. This is a lossy transport workaround, not full capability negotiation.
  Extend real-child query/reply tests before advertising additional protocols.
- ConPTY starts with default width-measurement flags. Verify its treatment of
  emoji, ZWJ sequences and ambiguous-width characters against Ghostty and the
  application before selecting a different measurement mode globally.

## References

- [Windows Terminal Atlas pixel shader](https://github.com/microsoft/terminal/blob/main/src/renderer/atlas/shader_ps.hlsl)
- [DirectWrite blending helpers](https://github.com/microsoft/terminal/blob/main/src/renderer/atlas/dwrite_helpers.hlsl)
- [ConPTY resize signal implementation](https://github.com/microsoft/terminal/blob/main/src/winconpty/winconpty.cpp)
- [Windows Terminal ConPTY connection](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalConnection/ConptyConnection.cpp)

Upstream links describe the current upstream implementation, not a guarantee
that every detail matches the pinned binary in `app/vendor/conpty/`.
