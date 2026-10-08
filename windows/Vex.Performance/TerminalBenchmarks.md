# Terminal performance baseline

Run these commands from the repository root on a Windows desktop, using PowerShell 7:

```powershell
dotnet publish windows/Vex.App -c Release -p:DefineConstants=VEX_SELFTEST `
    -o windows/Vex.Performance/bin/terminal-app
dotnet build windows/Vex.Performance -c Release
./windows/Vex.Performance/Measure-Terminal.ps1 `
    -AppExecutable windows/Vex.Performance/bin/terminal-app/Vex.App.exe `
    -ParserExecutable windows/Vex.Performance/bin/Release/net10.0/Vex.Performance.exe `
    -OutputDirectory windows/Vex.Performance/bin/terminal-results
```

The runner creates unique, isolated profiles and preserves raw reports. It runs
VT feed throughput, forced viewport snapshots, forced WPF redraws, then three
five-second interactive workloads. It discovers `nvim` on PATH; supply
`-NeovimExecutable` to select another installation. A missing Neovim produces
an explicit skipped result. The Neovim workload launches a real ConPTY session,
opens a generated 10,000-line file with `--clean -i NONE -n`, verifies its alternate
screen output, scrolls with `10j`/`10k`, and exits without saving.

Instrumentation is compiled only in Debug or `VEX_SELFTEST` builds. The app and
CLI should come from the same revision. Keep the benchmark window visible and
avoid concurrent builds or tests when comparing runs. The runner sets its launch
window style to Hidden, but the WPF window calls `Show()` and must remain visible
for valid composition measurements. Repeat and alternate variants; these short
workloads are not statistical evidence of a whole-application speedup.

## 2026-10-09 measurements

Release, Cascadia Mono 14, 135 × 36 WPF viewport, 160 × 50 forced snapshots.
Raw run: `bin/terminal-baseline/run-2e5dcddf3c804486947dd9a6268b15a4`.
The baseline includes output coalescing and nonblocking parser-lock acquisition.

| Operation | Median time | Managed allocation |
| --- | ---: | ---: |
| Feed 63,000 bytes of ASCII | 0.325 ms / 194 MB/s | 0 bytes/iteration |
| Feed 57,000 bytes of ANSI | 0.796 ms / 72 MB/s | 0 bytes/iteration |
| Force ASCII viewport snapshot | 0.264 ms/frame | 0 bytes/frame |
| Force Unicode viewport snapshot | 0.278 ms/frame | 0 bytes/frame |
| Force grapheme viewport snapshot | 0.284 ms/frame | 0 bytes/frame |
| WPF ASCII redraw | 0.212 ms/frame | 30,320 bytes/frame |
| WPF ANSI redraw | 0.502 ms/frame | 124,416 bytes/frame |
| WPF grapheme redraw | 2.760 ms/frame | 380,160 bytes/frame |

Feed timing measures the production managed `GhosttyTerminal.Feed` boundary,
including the status filter and native libghostty-vt call. It does not isolate
native parser CPU time. Status-flood samples are also saved; their allocations
and JIT warmup variance should be evaluated separately from ordinary VT output.
Snapshot and redraw costs are separate: redraw includes GlyphRun construction
and retained WPF drawings, but excludes parsing, snapshot extraction and GPU
presentation.

| Interactive workload | Flush p95 | Snapshot p95 | Input dispatch p95 | WPF callback interval p95 |
| --- | ---: | ---: | ---: | ---: |
| Output flood (~4.36 MB/5 s) | 1.384 ms | 0.310 ms | 31.227 ms | 23.903 ms |
| Scrollback | 0.475 ms | 0.237 ms | 37.403 ms | 24.660 ms |
| Neovim scrolling | 0.764 ms | 0.338 ms | 40.103 ms | 22.902 ms |

Input dispatch measures the queue delay of a coalesced Input-priority callback
posted from a background thread every 10 ms. This is a UI responsiveness probe,
not physical key-to-pixel latency. WPF intervals come from
`CompositionTarget.Rendering`, not presented frames or GPU FPS. Reports include
p50/p95/p99/max, changed rows, skipped busy-parser flushes, UI allocations and
Vex process CPU time; Neovim child CPU is excluded. Redraw count reflects changed
content and scheduler pacing, so it is not the display refresh rate.
These diagnostic CPU/allocation totals include sample collection and, for the
synthetic flood, the in-process producer; they are not isolated rendering costs.

All workloads passed again on the final published app, including real Neovim.
Run: `bin/terminal-final/run-17e06186ac1f49a3ba97ff82a2ab9110`.
Input-dispatch p95 was 29.1 ms for the flood, 37.4 ms for scrollback and 36.5 ms
for Neovim. These single short runs show the same responsiveness bottleneck;
they do not establish a terminal-rendering speedup from the startup changes.

The largest measured drawing cost is shaped grapheme output, while interactive
queue delays exceed the cost of individual snapshots and flushes. These results
provide reproducible targets for further optimization. They do not establish
parity with Ghostty's native frontend; that would require matched workloads,
fonts, dimensions, hardware, and actual presentation/input tracing.
