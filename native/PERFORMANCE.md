# Native performance checkpoint — 2026-10-17

Measured on Windows 11 Pro 22621, Intel Core i5-10310U, Intel UHD Graphics
(driver 31.0.101.2135), approximately 60 Hz. ReleaseSafe builds retain Zig
runtime safety, the DebugAllocator and the real ConPTY/libghostty terminal.
These are local comparisons, not universal latency guarantees.

## Changes

- Ordinary frame requests use one coalesced posted window message rather than
  waiting for a nominal 8 ms Win32 timer. A normal OS paint consumes the queued
  frame, avoiding a second redundant presentation. Continuous pipe output retains
  a deadline check so queued input/output cannot starve presentation.
- Synchronized output retains its 100 ms starvation bound without repeatedly
  extending that deadline on every new output chunk. Stale timer/posted messages
  cannot create idle repaint loops.
- Modal cards, rows, navigation and buttons have no hover drawing. Native hot
  tracking is suppressed too; click/pressed state, keyboard focus, selection,
  dragging and accessibility cursor feedback remain. Theme cards are drawn into
  an offscreen buffer before presentation.
- A live shell alone in its non-breakaway job skips the full-system process
  snapshot. Foreground-child discovery still uses the existing scan when children
  exist or the job query cannot establish this fast path.
- Terminal/UI renderers share immutable root signature and pipeline state, with
  independent COM ownership, glyph atlases, raster targets and frame uploads.
- Dialog-only GDI+ initialization is deferred until the first modal is opened.

## Measurements

Versions alternate within each trial. Baselines are saved executables from this
working tree, not release or WPF builds. The startup/renderer baseline predates
these performance changes. The scheduling baseline has the identical timing
probes and pipeline/GDI+ changes, but retains the old scheduler and process scan.

| Measurement | Baseline | Candidate |
| --- | ---: | ---: |
| Warm Zig-main → first-present median, 7 launches | 127.216 ms | 116.295 ms |
| Warm first-present range | 113.882–128.215 ms | 112.098–118.111 ms |
| Isolated frame-request → scene-start median, 3 × 120 requests | 14.820–15.039 ms | 0.421–0.428 ms |
| Isolated request p95, per trial | 15.935–16.404 ms | 0.572–0.598 ms |
| Isolated scene-preparation p95, per trial | 1.016–1.056 ms | 0.940–0.976 ms |
| Back-to-back request p95, per trial | 15.003–26.465 ms | 14.062–14.633 ms |
| Idle paints over 2 seconds, animation disabled | 0 | 0 |
| Idle CPU over 2 seconds, isolated-request trials | 15.625–62.500 ms | 0 ms at process-counter resolution |
| Plain terminal process CPU per forced paint, median of 3 trials | 1.042 ms | 0.347 ms |
| Styled terminal process CPU per forced paint, median of 3 trials | 1.128 ms | 1.215 ms |

The styled workload ranges overlap (baseline 1.128–1.302 ms; candidate
0.781–1.736 ms): this does **not** establish a styled-rendering speedup.
The 180-frame forced-paint samples still take roughly 3 seconds, consistent with
vsync pacing; the changes do not disable vsync or introduce a continuous loop.
Zero measured idle CPU means below process-counter resolution, not literally no
instructions. Metadata/upload maintenance and legitimate cursor/status animations
remain.

The candidate also passes these behavioral checks:

- Twenty synthetic mouse moves, crossing and leaving a modal control produce
  zero owner-draw/surface paints in both appearances. Covered controls include
  Theme Studio, settings navigation, buttons, cursor toggle, shell combo,
  command palette, shell picker and tab overview.
- Twenty frame requests followed by an OS paint produce exactly one frame.
- Continuous synchronized requests produce five frames over 600 ms rather than
  starving indefinitely, and presentation stops when requests stop.

## Modal update follow-up

A separate regression reproduced unnecessary work outside hover: changing `split`
to `split ` with identical results repainted five owner-drawn controls and the
popup surface. Active modal updates now compare content/selection/palette before
resetting controls. Command Palette keeps its initial size and anchor while
filtering; it no longer repositions, reapplies its Win32 theme or invalidates all
children on every query. Empty-result transitions update only the results area.

Settings visibility changes are batched, page painting is restricted to the
content area, and clicking the current page is a no-op. Popup surfaces and native
owner-drawn controls are buffered. Theme-card bitmaps are cached per selection,
size and DPI, invalidated on palette/content changes, and freed on modal close.
Selected cards retain the same preview geometry instead of shifting by a pixel.

`Test-NativeModalUpdates.ps1` checks equivalent queries produce zero surface or
owner-draw paints, changed results do not repaint the terminal, popup bounds stay
fixed, empty results recover, selected pages do not redraw, and repeated page
switches reuse theme rasters. It also measures 80 page switches through synchronous
Win32 command/paint round trips. The local baseline median/p95 was 43.908/54.747 ms;
the latest follow-up run measured 11.579/17.550 ms (earlier follow-up runs varied,
with medians around 15–17 ms and p95 around 22–26 ms). The first uncached
Theme Studio page was still slower at that checkpoint; these are not physical click-to-photon timings
or a guarantee that every page switch meets a display-frame deadline.

Artifacts: `zig-out/verification/settings-baseline.json` and
`zig-out/verification/modal-updates/{modal-updates.json,palette-*.png}`. The
candidate hash above identifies the earlier scheduling checkpoint, not this
follow-up build.

## Composed modal transitions and live settings

A device-context regression confirmed that owner-draw callbacks leaked their font,
text color and background mode into Win32 painting. All owner-draw callbacks now
save/restore the complete GDI context; custom combo and print paths restore theirs
as well. `Test-NativeThemeHover.ps1` checks this contract for native lists, buttons
and combos rather than relying only on screenshots.

Owned modal descendants now compose as one surface during page/control transitions.
Command Palette deliberately retains its cheaper stable-layout dirty-list path:
compositing that popup also repainted its background while typing, so that variant
was rejected. Settings prepares its bounded theme-card cache before its first show
instead of rasterizing gradients on the first Appearance click. This moves finite
work to opening Settings; it does not add a timer or continuous rendering.

The latest local 80-switch settings sample measured median 10.883 ms, p95 13.141 ms
and maximum 25.208 ms, including the first Appearance visit. This maximum exceeds a
16.7 ms, 60 Hz frame budget; the result is not a perfect-frame guarantee. These are
synchronous command/paint round trips, not scanout. The latest sample is in
`zig-out/verification/modal-updates/modal-updates.json`; an earlier run is preserved
in `composed-sample.json`.

`Test-NativeShellPainting.ps1` exercises 24 dark/light shell-picker row and segmented
mode states. After initial window-show settling, live incremental pixels match a
forced full repaint of the same state with zero differing pixels across all 24
dark/light normal/compact cases. It requires an
unobscured desktop, excludes four outer-frame pixels, and does not verify every
transient scanout frame or multi-monitor presentation. The normal shell popup fits
its rows using DPI-rounded row heights instead of unnecessarily showing a clipped
last row and scrolling; compact windows still scroll when required. Artifacts are under
`zig-out/verification/shell-painting/`.

Settings now applies and persists each theme, shell, valid font-size or cursor
change immediately, without Save/Cancel buttons. Closing does not roll back changes.
Failed writes retain the runtime/file values and restore the controls after the
warning is dismissed. Tests cover immediate dark/light theme application, restart
persistence, valid/invalid font edits, cursor/default-shell updates and read-only
write failure while the settings window stays open.

References: [Win32 composited window style](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)
and [owner-draw device-context contract](https://learn.microsoft.com/en-us/windows/win32/controls/wm-drawitem).

## Reproduce

```powershell
zig build
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-NativeThemeHover.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Measure-NativeStartup.ps1 -BaselineExecutable C:\saved-baseline\vex-native.exe
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Measure-NativeRenderer.ps1 -BaselineExecutable C:\saved-baseline\vex-native.exe
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Measure-NativeFrameScheduling.ps1 -Executable zig-out\bin\vex-native.exe -BaselineExecutable C:\instrumented-baseline\vex-native.exe
```

The scheduling comparison requires both builds to implement the same gated timing
probes. Add `-FrameSpacingMs 0` and a separate `-OutputDirectory` for burst samples.
The default 50 ms spacing isolates individual requests from previous vsync waits.

Raw artifacts under ignored `zig-out/verification/`:

- `perf-startup-complete/startup-comparison.json`
- `perf-renderer-complete/comparison.json`
- `frame-scheduling-complete/frame-scheduling.json`
- `frame-scheduling-burst/frame-scheduling.json`
- `theme-hover/theme-hover.json`
- `perf-unit-{debug,release}.log` and `perf-final-*.log`

SHA-256 provenance:

- Startup/renderer baseline: `3701872b71e199db8019be6e1370b79419eca1adc86666973932739de541d60a`
- Instrumented scheduling baseline: `573fb2ec3192e9a0cd81442bc650db85b6207b7a93a62b700863ddd497f99e38`
- Candidate: `4668b9c7edc2cca6f18ee48c4c7f8618b01f775344c88e8863cd09fc9f829cd3`

## Verification and limits

All 134 tests passed in Debug and ReleaseSafe. Live Windows checks passed for
modal hover, renderer (including WARP and device recreation), native UI pixels,
process exit, close confirmation, icons, cursor blink, modals, workspace, tabs,
dividers, Windows shells, path completion and universal-program fixtures.

Timing probes measure window-thread request-to-scene start and scene preparation;
they exclude GPU execution and scanout. Scene preparation also excludes end-pass
submission and Present. Startup measurements are warm, start at Zig main and
exclude OS process-creation time. No cold-cache, GPU-timestamp, keyboard-to-photon,
scrolling/resize frame-time distribution or physical high-refresh/multi-monitor
claim is made. A real display frame can still miss its deadline under system load.

Desktop tests require a quiet interactive desktop. Initial runs also observed
focus/cursor interference and nonzero idle paints; those runs are not presented
as clean idle samples. The table uses the completed alternating runs above.

## Terminal rendering follow-up — Windows 10, 240 Hz

Built in WSL with Zig **0.15.2**, executed on the native Windows desktop through
Windows PowerShell. This checkout does not build with the globally installed Zig
0.16; no language migration or Windows compiler installation was made. Machine:
Windows 10 Pro 10.0.19045, NVIDIA GeForce GTX 1060 3GB, reported 1920×1080 / 240 Hz.
All performance binaries are ReleaseSafe without graphics debugging.

### Retained changes

- Bounded, optional per-pane CPU quad cache. Warm panes skip the Zig cell/style/run
  walk and native glyph lookups; instances are copied in bulk into fence-protected
  frame uploads. Cursor overlays stay separate. Full GPU frames are still drawn.
- Exact contrast correction is cached across frames in a bounded four-way cache;
  colors, not theme identities, form its complete key.
- Continuous visible output is paced to the monitor using a one-shot high-resolution
  waitable timer integrated with the message pump. It does not pace ordinary UI
  requests, alter global timer resolution, or leave an idle render loop. Frame work
  and blocking Present count toward the period, avoiding an extra vsync delay.
- The ConPTY queue experiment was **discarded**: three alternating real-output
  trials did not show a repeatable benefit. Production pipe ownership is unchanged.

### Measurements

Baseline is HEAD `ce67d8e`, before these changes, rebuilt with the same compiler.
Three alternating trials per workload, same Windows cwd, raw cmd, 1120×720 window,
steady cursor. The warm fixture's JSON retains its historical `atlas` candidate label.
Ranges below are the three trial results, not confidence intervals.

| Measurement | Baseline | Candidate |
| --- | ---: | ---: |
| Plain warm scene preparation, trial medians | 0.298–0.344 ms | 0.104–0.106 ms |
| Styled warm scene preparation, trial medians | 1.386–1.451 ms | 0.105–0.111 ms |
| Styled warm scene preparation, trial p95 | 1.589–1.685 ms | 0.153–0.161 ms |
| Plain warm process CPU/forced frame | 0.608 ms | 0.347–0.434 ms |
| Styled warm process CPU/forced frame | 1.562–1.823 ms | 0.260–0.521 ms |
| 6,000 styled scrolling lines, process CPU | 1,953–2,328 ms | 1,266–1,391 ms |
| Styled output process CPU, median trial | 2,187.5 ms | 1,328.1 ms |
| Styled output wall time | 2,504–2,633 ms | 2,515–2,634 ms |
| Styled output presentation count | 1,386–1,454 | 453–475 |
| Isolated ordinary request → scene-start p95 | 0.182–0.210 ms | 0.181–0.193 ms |
| Idle presents / process CPU over 2 s, scheduling trials | 0 / 0 ms | 0 / 0 ms |

The styled stream uses about **39% less process CPU at the median**, not a proven
throughput speedup. Plain output is less conclusive: CPU ranges are 781–828 ms
versus 438–750 ms, while wall ranges are 1,164–1,278 ms versus 1,258–1,311 ms.
Do not generalize this fixture to every shell/TUI. Its wall time includes command
submission, child startup, ConPTY and up to 100 ms marker polling delay; text probes
may force snapshots at 10 Hz. Child process CPU is excluded. Forced-frame CPU
counters are coarse. Preparation excludes GPU execution, submission and Present;
request timing also excludes the earlier frame-slot fence wait. No physical
input-to-photon, GPU-duration or variable-refresh measurement was made.

The warm 28×96 fixture retains 160 KiB of CPU geometry. The eight-entry cache is
bounded at 20 MiB total and is optional under allocation pressure. The persistent
contrast table is fixed-size (about 72 KiB). These are additional CPU caches, not
claims about driver/GPU allocations or total application memory.

### Verification

- **137/137 tests pass** in both Debug and ReleaseSafe on Windows.
- Native renderer passes on hardware and forced WARP: byte-identical GPU captures
  with cache enabled/disabled for plain text, styled Unicode, atlas exhaustion,
  and light/split scenes. Healthy-device recreation, font/DPI changes, resize,
  upload reclamation, minimize/restore and idle suppression also pass.
- Cursor blink, workspace, process exit, universal-program fixtures and native UI
  renderer pass on the final binary.
- Output pacing checks cover stream bounding, the final deferred request waking
  without another output event, ordinary-request priority, OS-paint coalescing and
  no idle repaint loop. The isolated priority probe measured 93 µs, not scanout.
- Three final scheduling trials keep the synchronized-output starvation bound
  (five paints over 600 ms) and zero idle paints. An earlier attempt observed six
  idle paints; its cause was not established and it is not used as a clean sample.
- D3D12 debug-layer validation was **not run**: Windows Graphics Tools / SDK layers
  are not installed. Physical multi-monitor moves and refresh changes are unverified.

Reproduce on Windows (baseline executable must be retained before rebuilding):

```powershell
zig build
zig build test
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-NativeRenderer.ps1 -RequireNativeGlyphAtlas -RequireGeometryCache
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-NativeOutputPacing.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Measure-NativeRenderer.ps1 -BaselineExecutable C:\saved-baseline\vex-native.exe
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Measure-NativeTerminalOutput.ps1 -BaselineExecutable C:\saved-baseline\vex-native.exe
```

For WSL, `zig build test-build` compiles the Windows tests; run the resulting exe
and PowerShell fixtures on Windows. Outputs were staged under
`%TEMP%\Vex-render-perf`, without copying or installing the whole toolchain.
Selected reports, logs, pixel pairs and the baseline are copied into the ignored
`zig-out/verification/terminal-performance/`. Final raw Windows reports are in
`%TEMP%\Vex-render-perf\results\final-*`.

Executable SHA-256:

- Baseline: `601a926f0df6adce276e2c7affe0a5fe1ac7aec93453daf97ee23100a18ee0c3`
- Candidate: `87aa9e3258f75a6ea869f3e4a5d0194bc15d71ee22f42a5e2bcefe743df29d5c`

Remaining targets: full viewport snapshot extraction on dirty frames, atlas churn
for very large Unicode working sets, and real TUI/input-latency distributions.
These changes are measured improvements, not a claim that terminal performance
is now optimal on every workload.
