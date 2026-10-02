# Performance measurements

Run from `windows/` in PowerShell 7 on a Windows desktop:

```powershell
dotnet publish Vex.App -c Release -o Vex.Performance/bin/published
./Vex.Performance/Measure-Startup.ps1 `
    -Executable ./Vex.Performance/bin/published/Vex.App.exe `
    -OutputDirectory ./Vex.Performance/bin/startup
dotnet run --project Vex.Performance -c Release
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
The default publish configuration favors launch latency over download size;
compression and composite R2R remain overridable MSBuild properties.

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
