# Startup: bundle loading and WPF composition initialization

## Changes

The large composite ReadyToRun image now ships as `Vex.App.r2r.dll` beside the
EXE. Other managed assemblies remain bundled, and deployment remains
self-contained. Preserve the DLL when distributing the app. The repository's
ZIP and MSI payloads already enumerate published files recursively. The publish
self-test checks the image and native dependencies after two consecutive publishes.
`-p:VexExternalReadyToRunImage=false` restores the previous bundled image.

An explicit STA entrypoint marks the start of managed execution before loading
WPF application types. This separates runtime/bundle startup from application
initialization in `VEX_STARTUP_DIAG` logs.

WPF composition initialization starts on a temporary background STA before the
Application base constructor. Its independent visual/context warms the shared
process render engine while settings, fonts, shell creation and application XAML
initialize. No window/control crosses thread boundaries. The temporary Dispatcher
stays alive until the first window frame, then shuts down; application exit also
requests cleanup, including exits before that frame. Optional prewarm failures
are observed and written to startup diagnostics; normal window initialization
continues through WPF's ordinary path.

## Evidence

A sampled managed-thread trace of first-frame startup showed approximately
266 ms below `MediaContext` construction during `MainWindow.InitializeComponent`,
including 211 ms in `DUCE.Channel.SyncFlush` while connecting native render
channels. This is sampled wall time, including waits, not CPU time spent parsing
BAML. Trace: `bin/xaml-trace/startup.nettrace` and its Speedscope conversion.

Seven alternating before/after pairs on 2026-10-09, Release with `VEX_SELFTEST`,
fresh isolated profiles, Command Prompt, dark appearance, no concurrent builds
or tests. Variant order reverses every pair. OS caches were not cleared, so these
are not cold-disk or post-reboot measurements. Both executables include the same
terminal benchmark instrumentation and output scheduling fixes.

| Median | Before | After | Reduction |
| --- | ---: | ---: | ---: |
| Process start → managed entrypoint | 711.8 ms | 402.9 ms | 43.4% |
| Main window InitializeComponent | 280.8 ms | 164.8 ms | 41.3% |
| Process start → first window frame | 1335.1 ms | 913.8 ms | 31.6% |
| Process start → first PTY output | 774.1 ms | 496.6 ms | 35.8% |

Raw results: `bin/startup-final-pairs/pairs.json`, with per-launch logs beneath
`before/` and `after/`. First-frame samples in pair order:

```text
before: 1871.8 1333.9 1322.3 1349.6 1340.3 1331.9 1335.1
after:   931.5  906.7  909.6  916.5  922.7  913.8  897.8
```

The after EXE is 48.1 MB and the separate image 45.1 MB; the baseline EXE was
93.2 MB. This changes loading layout, not total application download size.
The published EXE must continue to travel with its native and ReadyToRun DLLs.

A fully loose publish had a warm median near 0.86 s but its first launch took
5.74 s in an exploratory run. Externalizing additional framework assemblies
also failed to beat the chosen hybrid variant. Those layouts were discarded.
The retained configuration avoids replacing a measured improvement with a
larger, unverified deployment change.

## Reproduce

Use `Measure-Startup.ps1` as described in [README.md](README.md), alternating
executables from separate publish directories. Do not run benchmarks beside
builds. Diagnostics report the first PTY byte, which can precede an interactive
shell prompt. Performance is hardware, cache and shell dependent.

For a first-frame trace, compile `VEX_SELFTEST`, set `VEX_PROFILE_DIR` to an
isolated profile, `VEX_STARTUP_DIAG` to a log file, and `VEX_STARTUP_PROFILE=1`.
The latter exits the app at ApplicationIdle after ContentRendered, before optional
overlay prewarming. Collect with `dotnet-trace collect --profile
dotnet-sampled-thread-time --format Speedscope --output startup.nettrace --
<published-executable>`. It measures managed sampled thread time; native CPU/GPU
attribution requires a separate Windows performance trace. The switch is absent
from ordinary Release builds.

Validation: two consecutive publishes retained the payload; published core,
incremental, corrupt-profile and light-startup scenarios passed. Thirty Release
output/startup/exit/tab tests and ten Debug WPF/output tests passed. The final
published app also runs the [terminal benchmarks](TerminalBenchmarks.md), including
a real Neovim session.
