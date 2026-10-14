# Vex native rewrite — milestone 1

Work on `feat/zig-native`, alongside the unchanged `../windows/` application.
The target is Windows + WSL; native Linux/macOS ports are out of scope for now.
This is not yet a daily-driver replacement. The full behavior/visual contract
and remaining work are in [REWRITE.md](REWRITE.md).

## Build and run

Requirements: Windows x64 (Windows 10 1809+ / Windows 11), **Zig 0.15.2**, and an interactive
desktop for GUI tests. Zig was already installed through Scoop on this machine.

```powershell
cd native
zig build
zig build run
```

Build defaults to **ReleaseSafe**. Explicit `-Doptimize=Debug` and
`-Doptimize=ReleaseSafe` are supported; ReleaseFast/ReleaseSmall are rejected.
Zig runtime safety is never disabled. Native C++ compiles with
`-Wall -Wextra -Werror`, no exceptions and no RTTI.

Or run `zig-out/bin/vex-native.exe` with `ghostty-vt.dll` beside it. No CLR/WPF,
Electron, external packages, or separately installed C++ toolchain is used.

## Shell profiles

```powershell
zig build run -- --shell pwsh
zig build run -- --shell powershell
zig build run -- --shell nu
zig build run -- --shell gitbash
zig build run -- --program 'C:\Windows\System32\cmd.exe' --arguments /d
```

`system` on a Windows project, and explicit `cmd`, resolve to the actual Windows
system cmd. A WSL UNC project instead makes `system` choose that distribution's
Linux login shell. Built-ins are resolved
once using canonical locations and absolute PATH entries; missing Windows
built-ins fall back to cmd. Custom programs must be absolute and available;
custom arguments are an explicit raw Windows command-line fragment, not shell
text guessed by the app. These presets are not a whitelist: any ConPTY-compatible
executable can use the custom launch path; the emulator/workspace do not branch
on a shell's name and custom programs are not wrapped in cmd.
The selected profile applies to all tabs/splits in this checkpoint; profile UI,
per-pane overrides and persisted settings remain pending.

cmd gets OSC 133 prompt markers; PowerShell 5/7 get OSC 7 directory reporting
and OSC 133 command lifecycle while retaining the user's prompt/history handler.
PSReadLine-unavailable sessions report degraded start tracking explicitly.
Nushell enables its own OSC 7/133 integration; Git Bash and custom programs retain
their existing integration, as in WPF. No user shell startup file is rewritten.
cmd does not automatically report OSC 7. cmd bootstrap paths containing `%` or
quotes reject explicitly instead of silently expanding them.

Launch storage is application-owned. Each ConPTY start borrows fresh, validated
UTF-16 executable/command/directory buffers only until CreateProcessW returns;
no reader/writer retains them. TERM defaults to `xterm-256color` when absent/dumb
and COLORTERM to `truecolor` when absent, preserving explicit user overrides.
The normalized environment belongs to the child launch, not global app state.

## WSL launches

```powershell
zig build run -- --shell wsl
zig build run -- --shell wsl --distribution Ubuntu
zig build run -- --shell wsl:/bin/bash --arguments '-l'
zig build run -- --shell wsl:/usr/bin/fish --distribution Ubuntu
```

WSL uses the actual system `wsl.exe`. An explicit `--distribution` must match a
WSL UNC project; otherwise that project's distribution is used, or the current
user's registered default for a Windows project. Launching from a WSL UNC
project with no shell flags selects Linux automatically; explicit non-system
Windows/custom shell requests still reject rather than silently remap.

Default metadata is read once from `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss`,
without an enumeration/helper process, booting a distribution or changing the
registry. The selected name is owned until all tabs/panes close. Missing,
inaccessible or malformed metadata is an error, not a guessed first distro or
cmd fallback; use explicit `--distribution` if default discovery is unavailable.
This reads the existing Lxss format, not a guaranteed version-independent WSL API.

`wsl` starts the distribution's `$SHELL` (or `/bin/sh`) as a login
shell; `wsl:/absolute/program` directly executes any Linux program, without a
shell-name whitelist, interpolated bootstrap or guessed login arguments.
`--arguments` remains a raw Windows argv fragment and requires an explicit
program. An unavailable launcher errors; missing distributions/programs report
WSL's failure in the pane, never fall back to cmd.

Each pane builds its own `--cd`: Windows drive directories are passed unchanged
for WSL to resolve with its mount configuration; `\\wsl.localhost\DISTRO\...`
and `\\wsl$\DISTRO\...` preserve Linux paths. A mismatched distribution or a
WSL directory with a Windows shell is rejected. CreateProcessW starts the
launcher in its Windows directory, not a Linux filesystem view.

Linux OSC 7 reports become owned paths in the selected distribution. Splits
inherit them; new tabs retain the project root. Local hosts are empty,
`localhost` or the Win32 machine name. A differently configured Linux hostname
is not yet recognized; remote/malformed reports preserve the previous path.
Linux backslash filenames reject rather than undergoing a lossy conversion.
No Linux prompt integration is injected and no user startup file is rewritten;
existing OSC 7/133/7501 integration remains optional. Profile UI and Linux
foreground-process tracking remain pending.

## Implemented workspace

One project, with up to **8 tabs**, each owning independent terminal panes:

- `Ctrl+Shift+T`: new tab; **+** does the same.
- `Ctrl+Tab` / `Ctrl+Shift+Tab`: next/previous tab; click selects a tab.
- Drag a tab body to reorder: press selects it, movement of 6 DIPs starts
  capture, and release commits the indicated insertion slot. The strip accepts
  drops within an 8-DIP vertical tolerance; home/outside drops are no-ops.
  Escape, capture/focus loss, resize/DPI and workspace actions cancel without
  changing order. ×/middle-click do not arm a drag. Sessions, split focus,
  directory and unread status remain attached to the same tab.
  This checkpoint draws an insertion marker, not WPF's animated ghost/push preview.
- `Ctrl+Shift+R` / `Ctrl+Shift+D`: split the focused pane right/down (also exits focus/zoom mode if active so the new split is visible).
- Custom Windows DWM titlebar chrome (`#181A1F` top bar, DWM dark mode, rounded corners, border color, and bundled `app.ico`), left project pill with Lucide Folder glyph, project folder name, and live `.git/HEAD` branch name, hoverable `+` new-tab button, and native Windows Minimize / Maximize-Restore / Close (`#E81123` hover) caption buttons.
- Pane header action buttons (Focus/Zoom toggle when split, Split Right, Split Down, and Close) with hover states; tabs render an overlapping `TabIconStack` (up to 3 `18×18` rounded icon tiles at `10 DIP` stride plus a `+N` overflow badge) and pane headers render a `14×14` favicon resolved from a 73-entry Lucide + SimpleIcons vector catalog (`app_icon.zig`) using zero-allocation cached-PID ConPTY foreground process inspection (`vex_pty_foreground_process`), OSC 0/2 titles, and OSC 7501 `app` metadata, backed by a per-DPI Direct2D GPU `ID2D1Bitmap` cache.
- Click a pane to focus it. Drag a right/down divider to resize its two subtrees
  live, without changing terminal focus. Ratios stay in 0.1..0.9; interactive
  dragging also keeps each neighbor at least 100 DIPs. Too-small containers
  disable dragging rather than overflowing. Existing split/layout geometry
  retains its 6-DIP gap; it is not the WPF splitter's final visual styling.
- Escape, capture/focus loss, cancel mode, window/DPI resize and tab/tree changes
  cancel a divider drag and restore its original ratio. Release commits; a captured or
  cancelled release over caption buttons cannot activate them.
- `Ctrl+Shift+W`: close a split or tab; a tab's **×** / middle-click or a pane header's **×** closes it.
- The last tab stays open, as in WPF. Closing a split focuses the first remaining
  leaf. Closing an active tab selects the left neighbor, or right for tab zero.
- Each pane uses the selected shell profile (integrated cmd by default). New tabs use the project launch
  directory; splits inherit the focused pane's last valid local OSC 7 directory,
  otherwise its starting directory (matching WPF, not a guessed prompt). Navigation
  and function keys use Ghostty's encoder (`Ctrl+Backspace` emits `0x17`); character input is UTF-8.
- Mouse selection (cell click-drag, word double-click, line triple-click, captured outside-pane drag clamping, copy-on-select) and keyboard selection (`Shift+Left/Right/Home/End`, `Ctrl+Shift+Left/Right/Home/End`) highlight selected cells; `Ctrl+C` (when a selection is active) / `Ctrl+Shift+C` copy, `Ctrl+Shift+X` cuts, and `Backspace` deletes a selection anchored at the live cursor.
- TUI mouse reporting (`DEC 1000` normal, `DEC 1002` button-event, `DEC 1003` any-event, and `DEC 1006` SGR / `DEC 1016` SGR-pixels / UTF-8 / urxvt encodings) forwards left/middle/right button presses, releases, motion, and wheel scroll to the PTY when active; holding `Shift` overrides TUI mouse capture to select text, scroll the viewport, or right-click paste. Right-click pastes clipboard text when mouse tracking is inactive or `Shift` is held.
- Interactive scrollback scrollbar overlay per pane renders a proportional thumb when scrollback history is present, expands on hover/drag, supports thumb dragging and track page-up/page-down clicks, and yields right-edge clicks to TUI mouse tracking unless `Shift` is held.
- `DEC 2048` in-band resize notifications (`\x1b[48;rows;cols;height_px;width_pxt`) are sent on resize when enabled, and `DEC 2026` synchronized output batches frame invalidation with a 100 ms starvation cap.
- Mouse wheel and primary-screen scroll shortcuts (`Shift+PageUp/PageDown`, `Ctrl+Shift+Up/Down`, `Ctrl+Shift+Home/End`) scroll the focused viewport; `Ctrl+Shift+V` and `Shift+Insert` paste text with newline normalization and DEC 2004 bracketed paste; write-only OSC 52 / OSC 1337 Copy sequences update the system clipboard.
- Window resizing, caption controls and per-monitor DPI handling use Win32.

The palette, default fonts and geometry follow Vex Dark: Segoe UI chrome,
14-DIP Cascadia Mono, 44-DIP tab strip, 24-DIP pane headers, rounded panes and
focus outline. Font/cell metrics come from DirectWrite. Initial window size is
1120 × 700 DIPs, minimum 640 × 400. This is **not yet pixel-identical UI parity**.

## Ownership and rendering

Code is grouped by responsibility, not by one class per file:

```text
src/
  main.zig
  app/         workspace/session owners
  workspace/   pure tab and pane-tree models
  terminal/    emulator, typed ABI, feed/status parsing
  shell/       launch/directory models and integration scripts
  windows/     Win32/ConPTY/rendering seams
tests/         live desktop fixtures and shared harness
```

Unit `test` blocks remain beside their implementation. `src/main.zig` explicitly
references the test modules so moving a file cannot silently drop it from the
suite. The application owns sessions; pure workspace models never import the
window adapter. Terminal ABI resolution uses its C seam directly, not a
circular dependency through the rendering/window adapter.

- `src/workspace/workspace_model.zig`: stable tab IDs, per-tab focus/zoom mode, allocation-free reorder and
  selection/close/focus rules, capped at 8 tabs.
- `src/workspace/tab_strip.zig`: one DIP geometry definition for tab widths,
  body/close/gap hit testing and insertion slots; Win32 copies its parsed projection.
- `src/workspace/tab_drag.zig`: typed none/armed/dragging transitions and commit-on-drop.
- `src/workspace/pane_tree.zig`: allocator-owned recursive split tree; transactional edits,
  stable IDs, ratio clamp 0.1..0.9, finite DIP geometry, single-leaf zoom layout, 64-level depth guard.
- `src/workspace/divider_drag.zig`: allocation-free divider hit/drag lifecycle,
  grab offset, minimum-size limits and rollback using stable divider IDs.
- `src/main.zig`: allocator/error boundary and explicit root test imports.
- `src/app/workspace_application.zig`: main-thread workspace orchestration.
- `src/app/terminal_session.zig`: stable per-pane ownership and teardown.
- `src/shell/shell_launch.zig`: startup selection, executable resolution,
  encoded Windows bootstraps and owned child environment.
- `src/shell/windows_arguments.zig`: shared Windows argv quoting/UTF-16 bounds.
- `src/shell/wsl_launch.zig`: owned distribution/Linux argv and per-pane WSL commands.
- `src/shell/wsl_paths.zig`: parsed distribution/Linux paths and lossless UNC mapping.
- `src/shell/directory.zig`: parsed absolute Windows paths, owned pane directories,
  launch-domain file-URI decoding and transactional report/cache updates.
- `src/terminal/program_status.zig`: owned OSC 7501 records, hierarchy, priority,
  BEL (`\x07`) attention, acknowledgement and bounded text parsing.
- `src/terminal/terminal_title.zig`: bounded OSC 0/2 terminal title sanitization and fallback formatting.
- `src/terminal/app_icon.zig`: 73-entry curated Lucide + SimpleIcons vector icon catalog, process/command-line/OSC title resolution, dark-color contrast lifting, and deterministic fallback monograms.
- `src/terminal/terminal_feed.zig`: bounded status and ConPTY-stripped DCS (`+q` XTGETTCAP / `$qm` DECRQSS) interception; unrelated VT is
  forwarded unchanged and plain chunks bypass copying.
- `src/terminal/api.zig`: checked symbol resolution and structured terminal API errors.
- `src/terminal/terminal.zig`: one emulator/iterator/key encoder/mouse encoder/selection gesture per pane,
  reusable parsed snapshots, typed colors/styles/widths/cursor, mouse and keyboard selection/copy/cut, TUI mouse protocol encoding, scrollback scrollbar state and row seeking, ConPTY resize/cursor/key-encoder parity, DEC 2048/2026 modes, bracketed paste encoding, write-only OSC 52/1337 clipboard and OSC 0/2 title callbacks, validated UTF-8.
- `src/windows/windows_ffi.zig`: callback-pointer conversion, opaque PTY owners,
  validated window/input seams and presentation of parsed snapshots.
- `src/windows/wsl_registry.zig`: read-only, bounded/typed default distribution
  discovery; registry unit fixtures use unique volatile keys outside Lxss.
- `src/windows/windows_platform.cpp`: Win32 event loop and Direct2D/DirectWrite COM.
- `src/windows/windows_pty.cpp`: per-session ConPTY worker/queue ownership and job objects.

Each terminal owns and frees its native handles and Zig buffers. Workspace trees
contain IDs, not pointers into movable session arrays. All emulator/UI access is
on the window thread; pipe workers never dereference a Zig object. Session IDs
are never reused, and queued output carries an ID rather than a heap pointer.
Workers are joined before freeing their buffers. ConPTY output is drained during
close; a kill-on-close job object owns the Windows descendant process tree.
The WSL smoke separately verifies Linux shell/child exit on busy pane close;
this is not a guarantee for Linux daemons or deliberately detached processes.

PTY input has a bounded 1 MiB queue; overflow is an error, not dropped input.
Output uses one acknowledged 64 KiB block per session. Copies use contiguous
ring-buffer spans rather than a modulo operation for every input byte. Shutdown
also covers partial process startup.

Clean snapshots reuse their cell/text allocations and skip the cell walk. Hidden
tabs keep consuming VT output but do not materialize/draw their grids until
selected, nor schedule active-window paints just for hidden output. Dirty snapshots read all rows because Ghostty can under-report dirty
rows after scroll shifts. Frame requests coalesce with a deadline check that
prevents continuous posted pipe output from starving WM_TIMER/WM_PAINT. There
is no repeating idle timer.

Directory reports use the synchronous native PWD callback: each report is parsed
before a later one in the same read can replace it. There is no per-output-block
directory query. Empty, remote, malformed, control-bearing and wrong-domain
reports retain the last valid directory. Paths and cached reports are owned per
pane; unchanged reports reuse storage. The local hostname comes from Win32,
not an environment variable. WSL reports use the selected distribution without
Windows path normalization, preserving POSIX symlink/parent path spelling.

OSC 7501 is independent of OSC 7. Each terminal owns up to 256 hierarchical
records; reports replace fields atomically, inherit ancestor app names and clear
only their own subtree. Status priority is Blocked > Error > Done > Working >
Idle. Titles/messages decode strict UTF-8 Base64, reject controls and remove
Unicode formatting characters. Reports over 4,096 bytes (including framing)
are discarded through BEL/ST, without leaking text into the grid. Capability
queries reply `ESC ] 7501 ; ? ESC \` through the reporting pane's PTY, in byte
order with emulator responses. Allocation failures preserve previous records
and propagate to the application.

Pane headers show status/progress; tabs aggregate the first highest-priority
pane in tree order. User input acknowledges done/error results, not blocked
records. Selecting a tab alone does not acknowledge results. Background status
changes repaint tab indicators; ordinary hidden output still causes zero
paints. Attention is latched across a pipe batch, even if a later report clears
its record. Exit removes running/blocked/idle records; RIS clears all records,
while soft reset and alternate-screen switches preserve them.

OSC 133 prompt/start/completion markers use a monotonic terminal clock. Routine
successful commands under two seconds return to Idle; longer commands retain
Done and failed commands retain an exit-code message until acknowledged. Prompt
markers drop running/blocked/idle explicit records without erasing unread
results. OSC 9;4 progress remains a fallback only until a valid OSC 7501 report
(including clear), and RIS re-enables that fallback. Semantic markers are
consumed before libghostty to preserve the ConPTY prompt-redraw workaround.

Native limits: 1,000 columns/rows for window grids, 1 Mi cells/frame, 64 KiB per
grapheme and 64 MiB text/frame. Oversized/malformed frames fail explicitly.
Longer graphemes grow a reusable buffer rather than overflowing a fixed stack
buffer. Direct2D may choose a software device fallback; this is **not yet a
custom Direct3D glyph-atlas renderer**.

Zig is not memory-safe. Pinned C ABI calls, GetProcAddress and registered callback
userdata necessarily use pointer conversions at the FFI seam. Runtime checks,
explicit ownership and tests mitigate these risks; they are not a proof that
native dependencies or C++ are memory-safe. DebugAllocator also checks owned Zig
allocations on normal exit; allocation-failure tests use the testing allocator.

The existing `../Vendor/libghostty/ghostty-vt.dll` is redistributed unchanged and
loaded only beside the executable. Headers are pinned to Ghostty `f64f4aca`,
matching `windows/Vex.Libghostty/Native.cs`; the DLL also carries the ConPTY resize
fix from `c55f213aa`. Upstream MIT license: `vendor/LICENSE`.

DLL SHA-256:
`b31c2eca14b95b26187ba492b31be80bedee3bf8185de1734b10b129fd898408`.

## Verify

```powershell
cd native
zig fmt --check build.zig src
zig build test -Doptimize=Debug
zig build test -Doptimize=ReleaseSafe
zig build
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-NativeTabs.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-NativeDividers.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-NativeWorkspace.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-NativeShells.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-NativeWsl.ps1 -Distribution Ubuntu
```

Unit tests use the **real checked-in DLL**: ABI/Unicode/colors/wide cells,
independent terminals, clean snapshot reuse, alternate screen, scrollback,
application cursor encoding and PTY replies. Directory fixtures cover every
OSC 7 chunk boundary, local-host/Unicode/percent decoding, multiple reports in
one read and callback/allocation failure propagation. Status fixtures cover
OSC 7501 framing limits/chunk boundaries, response ordering, replacement,
hierarchy, priority, acknowledgement, text sanitization, record eviction and
OOM rollback (including at capacity). Tree/workspace fixtures port WPF
close/focus/tab-limit semantics. All Zig allocation points in the tested tree,
workspace and frame workflows are exercised with allocator failure injection.
This does not inject failures into libghostty/Win32's own allocators.

The Windows test types commands via native character messages, checks the
**parsed viewport**, resizes, floods output, uses three nested panes, switches
independent tabs, verifies hidden output, inherits a reported Unicode/spaced
folder on split, retains project-root behavior for new tabs, ignores stale
output tokens, exercises
the 8-tab limit, kills a real child process with its pane, and closes a flooding
tab/window. Sentinels are assembled by the shell so echoed input cannot pass.
The test also checks that hidden output causes zero active-window paints,
while a hidden OSC 7501 update repaints its tab indicator. Status/priority and
acknowledgement are exercised through real ConPTY, not injected status state.
The shell test launches real cmd, Windows PowerShell, PowerShell 7, Nushell and
Git Bash where installed (missing profiles are reported as SKIP). It verifies
child environment, integrated prompt state, PowerShell/Nushell command errors
and duration, automatic OSC 7 split inheritance, and cmd bootstrapping from a
Unicode/spaced installation directory. The workspace/status fixture uses a raw
custom cmd to isolate explicit status reports from prompt cleanup.
The WSL test requires a registered default, a POSIX-compatible default shell,
and Bash in its selected distribution. It exercises registry-default selection,
project-aware UNC selection, pinned split distribution, default login and an explicit Linux program at a Unicode/spaced/apostrophe path, child
environment defaults/overrides, Linux OSC 7, split/new-tab directories, UNC
project startup and Linux shell/child exit on busy pane close.

The divider fixture sends DPI-scaled Win32 pointer messages to live right/down
and nested panes. It checks actual window-thread capture, unchanged focus,
100-DIP/ratio limits outside the window, commit and rollback, cancelled release
on chrome, tab/close mutations, window resize, and captured shutdown during
flooding output. Child console dimensions must match the resized emulator.
Its DPI evidence is limited to the monitor used by the test, not a physical
multi-monitor DPI transition or comparative frame-time benchmark.

The tab fixture uses an opt-in atomic replay of press/move/drop or cancellation
through the same Win32 window procedure and Zig handlers. It snapshots real
capture and preview state before drop, then verifies unchanged ConPTY PIDs,
split focus/trees, parsed output and unread status. Eight-tab moves, source close,
DPI cancellation and captured shutdown after output reaches the viewport are covered.
Atomic replay prevents OS-generated mouse moves from interleaving test coordinates;
it neither moves the user's cursor nor changes production gesture handling.

Diagnostic probes/actions/replay require `VEX_NATIVE_SELFTEST=1`. Synthetic
messages are not physical mouse/keyboard/IME or accessibility tests. The separate
multi-message divider fixture still requires a quiet interactive desktop: real
focus/capture changes legitimately cancel its gesture. Such interference was
observed in this session; reruns passed, not a claim of interference-proof automation.

Ignored artifacts under `zig-out/verification/`:

- `native-window.png`: one-pane output fixture.
- `native-workspace.png`: three live panes with distinct markers.
- `result.json`, `startup.txt`: smoke verification/measurements.
- `shells.json`: live Windows shell transport/integration results.
- `wsl.json`: live selected-distribution WSL results.
- `dividers.json`: live divider capture/resize/cancellation results.
- `tabs.json`: live atomic tab-reorder/ownership/capture results.

`VEX_NATIVE_METRICS=<absolute-path>` enables local milestone logs. Timing starts
at Zig main, before DLL initialization, not OS process creation. The test also
reports launch-to-window polling time. These numbers are **not matched WPF
benchmarks**, and working set excludes GPU allocations and child processes.

## Still pending

See the full ledger in [REWRITE.md](REWRITE.md): multiple projects, WSL Linux
process metadata/prompt adapters,
shell-profile UI/per-pane overrides, profile/session compatibility and persistence, saved layouts,
settings/themes, palette/tab peek/overlays, pane reorder, tab drag ghost/push animations and strip scrolling,
final divider styling, shell icons,
status tooltips/origin routing,
hyperlinks/path completion,
IME/click-to-move-cursor/extended key protocols, custom glyphs/color
emoji, true block cursor/blink, accessibility and packaging. Automatic close on
process exit and running-program close confirmation are also not yet migrated.
Native currently does not read or overwrite the production WPF profile.

Before replacing WPF, all ledger rows need functional/visual parity and matched
cold/warm startup, frame-time/input-to-present and CPU/GPU/child-memory evidence.
