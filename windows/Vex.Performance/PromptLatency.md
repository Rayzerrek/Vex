# Prompt latency and responsibility-based refactor

## Reproduced delay

The report concerns Nushell in NixOS/WSL on Windows 10, particularly after exiting
Neovim; the same shell does not stutter in Windows Terminal, and removing Nu's
configuration does not help. That exact OS/distribution combination is not
available locally. The local reproducer uses Windows 11, Windows Nushell, and a
real Neovim `--clean -i NONE -n` session through Vex's normal ConPTY pipeline.

Previously, output following a keystroke used the same 16 ms visible-pane pacing
timer as bulk output. A subsequent character arriving soon after the previous
paint therefore waited for the timer instead of immediately drawing its echo.
The scheduler now gives the first output batch after user input Input priority
and cancels any already armed output timer. Later output is coalesced into WPF
composition frames at the terminal window's display refresh rate, rather than a
fixed 16 ms timer. Native query responses do not receive user-input priority. Rendering still
uses nonblocking parser-lock acquisition and synchronized-output protection.

Two short sequential runs per variant, Debug, Cascadia Mono, 134 x 37 viewport,
with no concurrent builds or tests:

| Input to painted prompt row | Before, run 1 / 2 | After, run 1 / 2 |
| --- | --- | --- |
| Before Neovim, p50 | 31.44 / 31.30 ms | 1.32 / 1.58 ms |
| After Neovim, p50 | 30.94 / 30.52 ms | 1.19 / 1.13 ms |
| After Neovim, p95 | 47.94 / 53.45 ms | 2.24 / 7.99 ms |

These are synthetic PTY-input-to-row-paint measurements, not physical keyboard
latency or GPU presentation. Each sample sends 30 characters one at a time and
waits for the **current cursor row**, its content hash, and its render version to
match the expected text. Earlier scrollback text must not satisfy the check.
Completion is observed at the end of the real redraw, not by a polling timer.
Raw reports: `bin/prompt-latency-comparison/before-{1,2}.jsonl` and
`after-{1,2}.jsonl` (local, including the final `done` line).
The before/after difference establishes a scheduler delay on the local machine;
it does not establish that every Windows 10/WSL hitch is fixed.

Two deterministic regression cases fail when output uses the old paced path:
input echo with no timer armed, and input echo with an existing timer armed. They
check completion before dispatcher idle, rather than fragile millisecond limits.
The existing flood, hidden-pane, parser-contention, and title-coalescing tests
remain enabled.

## Reproduce on Windows, including NixOS/WSL

Use a Debug app or a Release app compiled with `VEX_SELFTEST`. Neovim must be on
the selected shell's PATH. From the repository root:

```powershell
dotnet build windows/Vex.App
./windows/Vex.Performance/Measure-PromptLatency.ps1 `
    -AppExecutable windows/Vex.App/bin/Debug/net10.0-windows/win-x64/Vex.App.exe `
    -OutputDirectory windows/Vex.Performance/bin/prompt-latency `
    -TerminalShellId 'wsl:/run/current-system/sw/bin/nu' `
    -WorkingDirectory '\\wsl.localhost\NixOS\home\YOUR_USER'
```

Replace the distribution, home directory, and Nu executable with the actual
installation. For Windows Nushell omit `-TerminalShellId` and `-WorkingDirectory`.
The script creates isolated settings/session profiles, retains each raw report,
and never changes the normal Vex profile. Keep the window visible. Do not compare
runs collected alongside builds or other performance tests.

## Display-paced continuous output

Visible continuous output waits for a WPF composition callback. WPF may emit
extra callbacks between display frames, so the scheduler also maintains a
refresh-rate deadline. Its millisecond tolerance accommodates rounded 60 Hz
timestamps without accidentally halving the redraw rate. The display rate is
queried at most once per second while output is active; detached/unsupported
displays default to 60 Hz. This also picks up moving the window between monitors.

User-input echo bypasses this pacing. The first output after 100 ms of quiet is
immediate. Hidden panes retain 100 ms pacing for mode notifications. A 100 ms
watchdog covers visible windows whose composition callbacks stop, such as a
minimized window. Both the watchdog and composition subscription stop after the
pending redraw or disposal; idle panes have no output-pacing callbacks.

Local Debug comparison, two runs per variant in before/after/after/before order,
Windows 11, 60 Hz display, Cascadia Mono, five seconds per scenario, no concurrent
builds/tests. Both variants already contain the immediate input-echo fix:

| Scenario / metric | 16 ms timer, two-run range | Display-paced, two-run range |
| --- | --- | --- |
| Output flood, redraws/s | 34.4–35.7 | 55.0–55.4 |
| Output flood, input dispatcher delay p95 | 18.6–22.2 ms | 1.7–2.0 ms |
| Output flood, process CPU per 5 s | 1672–1891 ms | 2266–2734 ms |
| Neovim scrolling, redraws/s | 34.3–35.1 | 49.6–52.2 |
| Neovim scrolling, input dispatcher delay p95 | 14.0–18.0 ms | 1.0–1.8 ms |
| Neovim scrolling, process CPU per 5 s | 1078–1188 ms | 1266–1297 ms |

More frequent visible updates cost CPU and allocations: this is a responsiveness
improvement, **not** a lower-CPU claim. Scrollback-only results showed no clear
improvement. Redraw counts are not GPU-presented FPS, and dispatcher probes are
not physical-keyboard-to-screen latency. Raw reports (local):
`bin/frame-pacing-deadline-comparison/{0-before,1-after,2-after,3-before}/report.txt`.

Two prompt-latency runs with the display-paced scheduler retained a p50 of
1.39 / 1.64 ms before Neovim and 1.15 / 1.02 ms after Neovim. Raw reports:
`bin/prompt-frame-pacing-deadline/run-feb8579c570541ee91bdad4758a94979/`.
These local results still do not establish performance on Windows 10/NixOS/WSL.

## Refactor scope

The four largest handwritten C# files now have responsibility-named partials:

- `NativeTerminalControl`: appearance/font caches, layout, output scheduling,
  and row rendering.
- `GhosttyTerminal`: feed filtering, frame snapshots, selection, and mouse encoding.
- `MainWindow`: keyboard routing, overlays, projects, pane dragging, and tab closing.
- `RenderSelfTest`: live scenarios, core scenarios, and pixel inspection.

This is an organizational refactor, not new object ownership or runtime
abstraction. It retains the existing native handles, lock ordering, field
initialization, pooled buffers, caches, and public interfaces. Large classes
remain stateful; smaller files alone do not make their architecture independent.
The existing test cases were not removed to meet a line-count target.

## Startup measurement

Five isolated-profile launches of the local **Release build** (not the MSI's
self-contained publish), Command Prompt, no concurrent builds/tests:

- First frame: 2106.7, 628.1, 622.5, 626.6, 639.2 ms; median 628.1 ms.
- First PTY output: 1457.2, 150.6, 154.1, 149.4, 153.8 ms.
- A warm sample spent 149.6 ms in `MainWindow.InitializeComponent`, while settings,
  fonts, composition, and the first shell were already warming in parallel.

Raw logs: `bin/startup-refactor/run-77f7aec71ac648618836a2c52d54201b/` (local).
The slow first sample is retained, not discarded. These profiles do not clear
OS caches and are not fresh-Windows benchmarks. There is no paired startup
optimization comparison, so **no faster-startup claim** is made for this refactor.
Existing startup prewarming, lazy overlays, and publish optimizations are preserved.
Further startup work should measure the published app's XAML construction and
first-frame layout/composition, rather than removing tests or runtime safety checks.
