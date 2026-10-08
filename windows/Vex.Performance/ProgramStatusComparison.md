# Program status and saved layouts: performance comparison

Measured on 2026-10-08 against `d8ce04d38159172510784a13afd8e0ea9b77fbe0`.
The after version includes OSC 7501 status tracking, shell launch integration,
static status badges and the deferred saved-layout editor.

Windows 11 x64 (10.0.22621), Intel Core i5-10310U, .NET 10.0.10, SDK 10.0.302.
Both applications use the repository's default self-contained, single-file,
composite ReadyToRun Release publication and the same native libraries.
There were no concurrent builds or tests during measurement.

## Results

| Measurement | Before | After | Difference |
| --- | ---: | ---: | ---: |
| First rendered app frame, cmd profile | 607.1 ms | 645.5 ms | +6.3% |
| First terminal output, cmd profile | 162.3 ms | 168.7 ms | +3.9% |
| ASCII feed, 63,000 bytes | 275.4 MB/s | 270.2 MB/s | -1.9% throughput |
| ANSI feed, 57,000 bytes | 96.2 MB/s | 101.3 MB/s | +5.4% throughput |
| Full 160 × 50 ASCII viewport snapshot | 0.182 ms | 0.176 ms | -3.4% time |
| Feed 1,000 OSC 7501 progress reports | 1.187 ms | 0.787 ms | -33.7% time |
| Repeated ASCII/ANSI feed, managed allocation | 0 B/batch | 0 B/batch | unchanged |
| Repeated viewport snapshot, managed allocation | 0 B/frame | 0 B/frame | unchanged |
| 1,000 status reports, managed allocation | 0 B/batch | 216,000 B/batch | 216 B/report |

The status workload replaces one record's progress and message repeatedly.
The before version forwards and ignores these unknown OSC reports; the after
version consumes them in the managed filter and actually updates status. This
is a comparison of processing the same stream, not equal functionality. The
allocation increase is scoped to reports. Status storage is capped at 256
records, with bounded identifiers, titles and messages.

Ordinary ASCII and ANSI remain allocation-free after warmup. Their
throughput changes and the faster snapshot are observations from this run,
not claims that an unchanged text/snapshot path was optimized. Startup adds
38.4 ms to the median first frame. Across individual launches, first-frame
times ranged from 590.3-631.9 ms before and 619.9-847.2 ms after.

## Method and scope

Startup uses seven before/after pairs with alternating order, a fresh isolated
Vex profile for every launch, and `Measure-Startup.ps1` with `TerminalShellId=cmd`.
Times include the process-start offset and are medians of seven launches per
version. OS file caches were not flushed. First terminal output is the first
ConPTY output, not a guarantee that the shell prompt or a startup command is
ready. PowerShell/Nushell profile loading is not benchmarked here.

Emulator measurements use two series of seven alternating before/after process
pairs, five samples per workload per process, and medians of all 70 samples.
Large short-sample variation in the first series prompted the second; no
samples from the first were discarded. Feed samples
perform 100 iterations after 30 warmup batches. ASCII and ANSI feed continuous
lines into a 160 × 50 terminal. The status batch contains 1,000 reports with
cycling progress and a base64 message (68,909 input bytes total). Snapshot
samples force invalidation and extract the full viewport 500 times after
200 warmup frames. Timing and thread-local allocation exclude construction,
JSON output, ConPTY transport, WPF drawing and GPU composition.

The real WPF self-test separately feeds 1,000 status changes plus a transient
blocked state and verifies one UI callback, the latest progress and latched
attention. Production header updates use a 100 ms one-shot dispatcher timer;
no pending report means no status timer ticks. Indicators are static glyphs.
The saved-layout editor and its bindings are created on first use.

## Reproduction

Publish the baseline and current checkout with identical defaults. Run
`Measure-Startup.ps1` against both executables, alternating their order and
using fresh output directories. Use the current benchmark source with each
version of `Vex.Libghostty.dll` and the same `ghostty-vt.dll` for the feed
comparison:

```powershell
dotnet run --project windows/Vex.Performance -c Release -- --feed current
dotnet run --project windows/Vex.Performance -c Release
```

The first command emits JSON timing/allocation samples for the feed workloads;
the second runs the existing ASCII, Unicode and grapheme snapshot benchmark.
The comparison above uses the same snapshot API on a viewport filled with
50 ASCII lines. Raw samples and the isolated baseline/current publications
from this run remain under ignored `artifacts/perf-comparison`,
`artifacts/perf-before` and `artifacts/perf-after`.

Validation: the Release solution build passed without warnings or errors.
Debug: 443 app tests and 127 emulator tests passed, including actual WPF menu,
editor and rendering checks, real ConPTY capability detection, interactive
PowerShell command failure, and layout commands in cmd, PowerShell and Nushell.
Release: 435 app tests and 127 emulator tests passed. An initial Debug rerun
could not acquire the shared Windows clipboard in an existing fixture;
the final suite rerun passed.

The published app passed core, incremental, corrupt-profile and light-startup
WPF scenarios, including saved-layout editing and status batching. Two unchanged
publications retained native libraries and shell integration scripts. ZIP and
MSI builds passed without warnings; payload inspection verified the nested
OpenConsole and shell integration directories. The web formatting/lint/type
check passed. Detailed corrected review findings are in
[the review report](../../docs/reviews/program-status-and-layouts.md).
