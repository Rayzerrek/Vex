# Performance measurements

The [terminal benchmark runner](TerminalBenchmarks.md) measures VT feeding,
viewport snapshots, WPF/GlyphRun redraws, and responsiveness during output floods,
scrollback and a real Neovim session.
The [latest startup comparison](StartupOptimization.md) covers bundle loading
and background initialization of WPF composition.

Run from `windows/` in PowerShell 7 on a Windows desktop:

```powershell
dotnet publish Vex.App -c Release -o Vex.Performance/bin/published
./Vex.Performance/Measure-Startup.ps1 `
    -Executable ./Vex.Performance/bin/published/Vex.App.exe `
    -OutputDirectory ./Vex.Performance/bin/startup
dotnet run --project Vex.Performance -c Release
./Vex.Performance/Test-PublishedApp.ps1
```

The startup script creates a separate profile for every sample, clears inherited
`VEX_*` diagnostic modes, waits for both `ContentRendered` and first PTY output,
and closes each app. It keeps logs, profiles and `results.json` under the output
directory. Repeated invocations create separate run directories. It never uses
the user's profile. `-TerminalShellId nu` measures Nushell instead of the default
Command Prompt fixture.

Times include process startup, using the app's process-start timing mark to
translate diagnostic timestamps. First output can precede the visible window;
it is not a measurement of a fully initialized interactive shell prompt. OS file
caches are not cleared, so these are fresh-profile launches, not cold-disk boots.
Compare published executables; development builds have different JIT costs.

## 2026-10-02 results

Baseline: `d40e11d`. Windows 11 build 22621, .NET 10.0.10, SDK 10.0.302,
default font and dark appearance, one Command Prompt pane. Seven alternating
before/after pairs, with no concurrent builds or test runs:

| Median | Before | After | Reduction |
| --- | ---: | ---: | ---: |
| Process start to first window frame | 953.7 ms | 700.5 ms | 26.5% |
| Process start to first PTY output | 345.8 ms | 174.4 ms | 49.6% |
| Published EXE size | 46.7 MB | 128.0 MB | larger |

Individual first-frame samples, in pair order:

```text
before: 960.6 914.0 947.8 928.4 953.7 955.1 964.0
after:  858.4 719.5 714.7 700.5 678.3 662.9 650.4
```

Compression alone changed the median of nine sequential launches from 896.0
to 765.1 ms. Composite ReadyToRun also precompiles the WPF startup path.
Closed project/confirmation popups are created from templates on first use.
This first iteration favored launch latency over download size;
compression and composite R2R remain overridable MSBuild properties.

## Startup and package size followup

Baseline: `38ff3a7`, compared with the startup-roots/text-prewarm configuration.
Same machine, SDK, appearance, font and Command Prompt fixture as above. Nine
fresh-profile pairs with no concurrent builds or tests; pair order reverses on
alternate pairs. These measurements are a separate session, so compare values
within this table rather than combining percentages with the earlier session.

| Median / size | Before | After | Reduction |
| --- | ---: | ---: | ---: |
| Process start to first window frame | 657.3 ms | 623.0 ms | 5.2% |
| Process start to first PTY output | 163.9 ms | 122.9 ms | 25.0% |
| Published EXE | 128.0 MB | 92.3 MB | 27.9% |
| Complete published app, excluding PDBs | 139.7 MB | 104.0 MB | 25.5% |

Sizes use decimal MB. Individual first-frame samples in pair order, including
the first launch of the new executable:

```text
before: 646.3 657.3 768.9 681.0 777.3 644.2 640.9 638.5 674.5
after:  833.9 634.2 622.5 628.5 659.5 611.0 611.7 620.8 623.0
```

Profile I/O, font lookup and shell prewarm now start before WPF's base
constructor. `TERM` and `COLORTERM` are set before those tasks can create a
child. Shared Segoe UI/icon text-layout caches are warmed in the background
alongside the terminal font cache. There is no duplicate settings/session
parser; the UI and prewarmer consume the same task results.

Self-contained deployment remains the default. The publish target roots the
startup assemblies, including reflection-driven WPF converters, DirectWrite
and UI Automation. Other shipped assemblies retain their IL/resources and
crossgen compiles transitive calls into them, following the
[SDK's composite-roots behavior](https://github.com/dotnet/sdk/blob/v10.0.302/src/Tasks/Microsoft.NET.Build.Tasks/PrepareForReadyToRunCompilation.cs).
Unused assemblies are compiler references rather than composite inputs, so
their discarded native code no longer remains in the EXE. Networking libraries
are retained: `CursorConverter`'s fallback JIT path references `WebRequest`,
even when the XAML specifies a local cursor such as `Hand`.

`-p:VexUseStartupReadyToRunRoots=false` restores full composite compilation.
The target also works around SDK 10.0.302's duplicate unrooted publish entries
and records compiler inputs/options so switching variants without cleaning
cannot reuse the wrong image. Default → full → default publishing was exercised.
The compiler input record is only rewritten when its contents change.
Native DLLs and external symbols are excluded before bundling so an unchanged
publish also copies them; the SDK otherwise loses its bundler-produced sidecar
list and can remove those files from the destination. Published self-tests
repeat publication and check the native payload before launching the app.

Whole-bundle compression reduced an experimental full-composite EXE to 53.4 MB,
but its five-launch median was 920.5 ms. App-only roots produced a 60.1 MB EXE
with a 1433.5 ms median. Those variants were rejected for the default; these
exploratory samples were sequential and are not the paired comparison above.

Followup validation: Release solution build with no warnings/errors; all 399
Debug app tests, 391 Release app tests and 92 Release emulator tests passed, including the clipboard
test that the earlier desktop session could not establish. The published
Release self-test script passed core/incremental rendering, corrupt-profile
recovery, and light-appearance startup. Startup checks open settings, the
command palette, theme switcher and confirmation/project popups in the bundled
app. Light startup uses text/clear parity against full repaints; the older core
pixel heuristics assume a dark background and are not used for that scenario.

The terminal benchmark forces a complete 160-by-50 viewport snapshot, excluding
VT feeding and WPF drawing. Medians of five samples, 500 frames per sample after
200 warmup frames, measured in separate Release processes:

| Workload | Before (ms/frame) | After (ms/frame) | Reduction |
| --- | ---: | ---: | ---: |
| ASCII | 0.462 | 0.212 | 54.1% |
| Unicode | 0.499 | 0.201 | 59.7% |
| Grapheme clusters | 0.417 | 0.189 | 54.7% |

All workloads retain zero managed bytes allocated per repeated snapshot. These
numbers describe viewport extraction, not end-to-end application throughput.
The complete cell walk on partial frames is preserved to avoid stale rows after
scrolls. Direct scalar/width/default-style decoding matches the pinned
[Ghostty cell layout](https://github.com/ghostty-org/ghostty/blob/f64f4aca/src/terminal/page.zig).
Only the constant-time raw-cell copy and iterator advance suppress GC transitions;
style, grapheme, feed and allocating native calls retain normal transitions, as
required by the [runtime's constraints](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.suppressgctransitionattribute).

## 2026-10-03 renderer followup

Renderer baseline: `52ec471`. Release builds with `VEX_SELFTEST` enabled,
.NET 10.0.12, SDK 10.0.303,
Cascadia Mono 14, 135-by-36 cells, DPI 1. Five alternating before/after pairs,
three samples of 100 forced redraws per process after warmup, with isolated
profiles and no concurrent builds or tests. Medians across 15 samples:

| Workload | Before (ms/frame) | After (ms/frame) | Before (bytes/frame) | After (bytes/frame) |
| --- | ---: | ---: | ---: | ---: |
| ASCII | 0.673 | 0.624 | 69,216 | 30,320 |
| ANSI | 0.871 | 0.893 | 124,416 | 124,416 |
| Grapheme clusters | 2.051 | 1.608 | 380,160 | 380,160 |

Long single-column glyph segments share immutable advance arrays, bounded to
256 cached shapes per pane and invalidated on font/DPI changes. Glyph indices
still receive independent arrays for WPF's retained drawings. Short segments and
wide glyphs keep the copy path. ASCII allocation falls by 56.2%, with a 7.3%
reduction in this session's median redraw time. Timing for ANSI/graphemes varied
between runs; no speed improvement is claimed for those workloads. These are
UI-thread drawing costs, excluding VT parsing and GPU composition.

The startup test now uses a profile-free PowerShell fixture that waits for a
valid DA1 reply before emitting its ready marker, which the test reads from the
emulated viewport. The previous test selected the user's configured shell and
looked for three prompt characters in individual output chunks. A missing
Nushell autoload file prevented the prompt and produced a misleading DA1 timeout.
Session/prewarm cleanup now runs even when the assertion fails.

Validation: all 517 Debug tests and 509 Release tests passed across the app and
emulator suites. Debug includes the real WPF renderer, path picker navigation,
retained glyph-spacing/DPI checks, and the controlled DA1 startup fixture.

## 2026-10-08 program status and saved layouts

Program status and saved layouts have a separate
[before/after comparison](ProgramStatusComparison.md), covering Release startup,
ASCII/ANSI feed throughput, viewport snapshots, report floods and managed
allocation. Run `dotnet run --project windows/Vex.Performance -c Release -- --feed`
to reproduce the feed workloads.

## Validation

Release solution build: no errors or warnings. Debug tests: 490 passed across
the app and emulator suites when excluding the clipboard-lock test. They cover
real WPF rendering, restored/corrupt profiles, deferred popup bindings and close
confirmation, startup input, output floods, Unicode/styles/background erases,
late prewarm rejection and concurrent ConPTY disposal.
Release tests: 482 passed with the same clipboard exclusion. A published
single-file composite-R2R Release app with `VEX_SELFTEST` compiled in also passed
both the core and incremental renderer scenarios, including popup confirmation
and pixel comparisons against full repaints.

The clipboard-lock test was also run separately in Debug and Release. This
desktop denied `OpenClipboard` with Win32 error 5 and no open-clipboard owner,
so its prerequisite could not be established. The test remains unchanged.
