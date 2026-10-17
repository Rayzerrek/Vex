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
