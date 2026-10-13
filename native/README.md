# Vex native rewrite — milestone 1

Work on `feat/zig-native`, alongside the unchanged `../windows/` application.
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

## Implemented workspace

One project, with up to **8 tabs**, each owning independent terminal panes:

- `Ctrl+Shift+T`: new tab; **+** does the same.
- `Ctrl+Tab` / `Ctrl+Shift+Tab`: next/previous tab; click selects a tab.
- `Ctrl+Shift+R` / `Ctrl+Shift+D`: split the focused pane right/down.
- Click a pane to focus it.
- `Ctrl+Shift+W`: close a split or tab; a tab's **×** / middle-click closes it.
- The last tab stays open, as in WPF. Closing a split focuses the first remaining
  leaf. Closing an active tab selects the left neighbor, or right for tab zero.
- Each pane currently starts `cmd.exe /d` in the launch directory. Navigation
  and function keys use Ghostty's encoder; character input is UTF-8.
- Mouse wheel scrolls the focused viewport; `Ctrl+Shift+V` pastes plain text.
- Window resizing, caption controls and per-monitor DPI handling use Win32.

The palette, default fonts and geometry follow Vex Dark: Segoe UI chrome,
14-DIP Cascadia Mono, 44-DIP tab strip, 24-DIP pane headers, rounded panes and
focus outline. Font/cell metrics come from DirectWrite. Initial window size is
1120 × 700 DIPs, minimum 640 × 400. This is **not yet pixel-identical UI parity**.

## Ownership and rendering

- `src/workspace_model.zig`: tab selection/close/focus rules, capped at 8 tabs.
- `src/pane_tree.zig`: allocator-owned recursive split tree; transactional edits,
  stable IDs, ratio clamp 0.1..0.9, finite DIP geometry, 64-level depth guard.
- `src/terminal_app.zig`: application/session owners, main-thread orchestration.
- `src/api.zig`: checked symbol resolution and structured terminal API errors.
- `src/terminal.zig`: one emulator/iterator/key encoder per pane,
  reusable parsed snapshots, typed colors/styles/widths/cursor, validated UTF-8.
- `src/windows_ffi.zig`: callback-pointer conversion, opaque PTY owners,
  validated window/input seams and presentation of parsed snapshots.
- `src/windows_platform.cpp`: Win32 event loop and Direct2D/DirectWrite COM.
- `src/windows_pty.cpp`: per-session ConPTY worker/queue ownership and job objects.

Each terminal owns and frees its native handles and Zig buffers. Workspace trees
contain IDs, not pointers into movable session arrays. All emulator/UI access is
on the window thread; pipe workers never dereference a Zig object. Session IDs
are never reused, and queued output carries an ID rather than a heap pointer.
Workers are joined before freeing their buffers. ConPTY output is drained during
close; a kill-on-close job object owns the shell's descendant process tree.

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
zig fmt --check build.zig src/*.zig
zig build test -Doptimize=Debug
zig build
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-NativeWorkspace.ps1
```

Unit tests use the **real checked-in DLL**: ABI/Unicode/colors/wide cells,
independent terminals, clean snapshot reuse, alternate screen, scrollback,
application cursor encoding and PTY replies. Tree/workspace fixtures port WPF
close/focus/tab-limit semantics. All Zig allocation points in the tested tree,
workspace and frame workflows are exercised with allocator failure injection.
This does not inject failures into libghostty/Win32's own allocators.

The Windows test types commands via native character messages, checks the
**parsed viewport**, resizes, floods output, uses three nested panes, switches
independent tabs, verifies hidden output, ignores stale output tokens, exercises
the 8-tab limit, kills a real child process with its pane, and closes a flooding
tab/window. Sentinels are assembled by the shell so echoed input cannot pass.
The test also checks that hidden output causes zero active-window paints.
Diagnostic probes/actions require `VEX_NATIVE_SELFTEST=1`. Synthetic messages
are not physical keyboard/IME or accessibility tests.

Ignored artifacts under `zig-out/verification/`:

- `native-window.png`: one-pane output fixture.
- `native-workspace.png`: three live panes with distinct markers.
- `result.json`, `startup.txt`: smoke verification/measurements.

`VEX_NATIVE_METRICS=<absolute-path>` enables local milestone logs. Timing starts
at Zig main, before DLL initialization, not OS process creation. The test also
reports launch-to-window polling time. These numbers are **not matched WPF
benchmarks**, and working set excludes GPU allocations and child processes.

## Still pending

See the full ledger in [REWRITE.md](REWRITE.md): multiple projects/WSL and shell
profiles, inheriting a pane's live working directory on split, profile/session compatibility and persistence, saved layouts,
settings/themes, palette/tab peek/overlays, divider dragging/reorder, icons and
program status, selection/copy/cut, hyperlinks/path completion, bracketed paste,
IME/full mouse/key protocols, ConPTY DCS feed-filter parity, custom glyphs/color
emoji, true block cursor/blink, accessibility and packaging. Automatic close on
process exit and running-program close confirmation are also not yet migrated.
Native currently does not read or overwrite the production WPF profile.

Before replacing WPF, all ledger rows need functional/visual parity and matched
cold/warm startup, frame-time/input-to-present and CPU/GPU/child-memory evidence.
