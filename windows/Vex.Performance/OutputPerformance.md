# Output scheduling and startup investigation

The reported trigger was `windows/build-installer.ps1`: Release publication,
ZIP compression, and a WiX MSI build. CPU consumed by those child processes is
separate from Vex's WPF renderer. The original installer interaction was not
replayed, so the fixes below address reproduced terminal bottlenecks rather
than identify which process caused the reported 30% CPU reading.

## Reproduced terminal bottlenecks

`ScheduleRedraw` previously queued a new background dispatcher operation as
soon as the preceding operation started. Continuous output could therefore
produce hundreds of UI callbacks per second, including for hidden panes.
Output now respects a minimum interval between passes: 16 ms for visible panes,
100 ms for hidden panes. Quiet output is dispatched immediately; only bursts
start a one-shot timer for the remaining interval. Parsing, terminal query
replies, and buffering remain continuous. The
pending flag is cleared before a flush so output received during that flush
schedules another frame. Idle and disposed panes have no timer ticks.

`FlushRedraw` also waited for the emulator lock held by the PTY reader. That
wait blocks input and all other tabs sharing the UI thread. It now tries the
lock without waiting and schedules another frame if parsing is busy. A
successful pass retains the lock through drawing so subsequent terminal reads
cannot reintroduce a wait midway through the pass.

Windows, .NET 10.0.12 / SDK 10.0.303, Debug WPF control, 1000-by-600 window.
The output fixture feeds bursts of eight chunks, each containing 32 ASCII
lines, for approximately one second. These are individual diagnostic samples,
not paired throughput/CPU benchmarks; producer throughput varies with load.

| Pane | Before: chunks / background callbacks | After: chunks / background callbacks |
| --- | ---: | ---: |
| Hidden | 520 / 393 | 512 / 18 |
| Visible | 248 / 214 | 528 / 78 |

Both final-frame checks passed, including revealing the hidden pane after
output stopped. The fixtures also assert that quiet output is applied before
dispatcher idle rather than waiting for a timer. A separate deterministic contention fixture holds the real
emulator lock for up to 500 ms: before the change, redraw waited 519.9 ms;
after the change, it returned in less than 0.1 ms. That fixture demonstrates
the blocking mechanism, not a measured parser duration for installer output.

Run the Windows regression fixtures with:

```powershell
dotnet test windows/Vex.App.Tests --filter FullyQualifiedName~TerminalOutputPerformanceTests --logger 'console;verbosity=detailed'
```

The existing renderer input-order fixture now waits for the paced output to
finish instead of assuming dispatcher idle means a frame has been drawn.
Input precedence and the final cell content remain asserted.
Synthetic link-hover fixtures also drain pending frames before positioning
their fake pointer: a delayed real redraw otherwise refreshes hover from the
desktop pointer and invalidates the fixture.

## Startup findings

Five alternating pairs of isolated-profile published Release launches, with
pair order reversed on alternate pairs, produced:

| Median | Baseline | Final build |
| --- | ---: | ---: |
| First window frame | 1392.1 ms | 1377.2 ms |
| First PTY output | 796.7 ms | 799.2 ms |

First-frame samples in pair order:

```text
baseline: 1343.5 1467.6 1392.1 1347.0 1468.1
final:    1513.7 1377.2 1424.1 1353.8 1356.5
```

The difference is small relative to the sample spread; no startup improvement
is claimed. The baseline snapshot also predates a concurrent tab-header change,
so this is a whole-build comparison, not isolated attribution to scheduling.
No builds or test runs ran concurrently with this final comparison.

A representative 1424 ms final launch spent approximately 746 ms before the
app's first startup timing mark and 296 ms in
`MainWindow.InitializeComponent`. Font and shell prewarming already overlap
initialization; first shell output arrived before the visible window. The
first-output metric is not interactive prompt readiness.

This change targets responsiveness during output, not startup speed. Further
startup work should first attribute the interval before app initialization and
the main window's XAML construction cost. Existing font/shell prewarming,
deferred overlays, and lazy pane realization should be preserved. These
fresh-profile launches retain OS file caches and do not represent cold-disk
startup. Earlier measurements in this investigation included concurrent
repository work; neither those nor earlier README sessions should be compared
with this table as a regression.

## Validation

- Solution build passed; its snapshot had one analyzer warning in the
  concurrent tab-header test.
- Final Debug output/renderer fixtures: 10 passed, including both real WPF
  rendering modes, input precedence, hidden-pane catch-up, quiet output,
  synthetic link preview, and corrupt-profile startup.
- Final Release output/session startup/exit fixtures: 19 passed.
- Emulator tests: 127 passed.
- Broad final Debug app run, excluding clipboard tests: 464 passed and one
  failed. `ProgramStatusTransportTests.SavedLayoutCommand_StartsOnceAndShellReportsPrompt("nu")`
  timed out despite emitting its ready marker and a prompt. That fixture uses
  `GhosttyTerminal` and `TerminalSession` directly, without the modified WPF
  renderer. The failure is recorded rather than treated as a passing suite.
