# Testing

The detailed .NET test descriptions below cover the legacy WPF app. Native Zig
build/test instructions are in [native/README.md](../native/README.md). Desktop
PowerShell harnesses and benchmark scripts are maintained locally outside this
repository; historical commands referring to them are not CI entry points.
Renderer tests need an interactive desktop.

## Unit and integration tests

```powershell
dotnet test windows/Vex.slnx
```

### Vex.App.Tests

Covers the shell's pure logic, real ConPTY integration, and renderer
regressions in Debug. The test project's `win-x64` runtime identifier keeps
`conpty.dll` beside the packaged OpenConsole host. Without this layout the DLL
can silently fall back to the system conhost and exercise a different transport
from the application.

| Area | File |
|---|---|
| Key events to VT byte sequences | `TerminalKeyMapTests` |
| Fuzzy search scoring and ordering | `FileSearchEngineTests` |
| Path picker ranking, shell quoting, directory indexing, cancellation, and search latency | `PathCompletionTests` |
| Debounce timing and coalescing | `HalfDebouncerTests` |
| Theme resolution and color parsing | `TerminalThemeTests` |
| Perceptual ANSI/RGB contrast, dimming, faithful-color mode and settings persistence | `TerminalPaletteTests` |
| Compact pane structure, header colors, outline-only blur and splitter hit targets | `CompactLayoutTests`, `ChromePaletteTests` |
| Rounded overlays/popups, clipped content, preserved shadows and antialiased corners at 100–200% DPI | `RoundedSurfaceTests`, `WindowFrameTests` |
| Live preview lifecycle without realizing new panes | `TabPeekPreviewTests` |
| Distinct task states, attention routing and visible-only working animation | `ProgramStatusPresentationTests` |
| Displayed version derivation | `AppInfoTests` |
| Real-window rendering and corrupt-profile startup (Debug) | `TerminalRendererRegressionTests` |
| Recovery of null session entries and selected indices | `SessionRecoveryTests` |
| Shell-profile normalization without save timers | `AppSettingsRecoveryTests` |
| Bounded prewarm buffers, ownership, live ConPTY output flood, and cold retry after failed prewarm | `TerminalPrewarmBufferTests`, `TerminalPrewarmFailureTests` |
| Output pacing, parser contention, coalesced title floods and queued callbacks after disposal | `TerminalOutputPerformanceTests` |
| Project defaults, immutable pane overrides, mixed Windows/WSL sessions, saved shell layouts and Home reuse | `ProjectShellSelectionTests` |
| Installed shell choices, login alias deduplication, actual system default, and three-choice primary lists | `ShellChoiceListTests` |
| WSL path conversion, project discovery/home resolution and Linux foreground applications | `WslShellPathsTests`, `WslProjectTests`, `WslProcessTrackerTests` |
| Real Ubuntu home projects under WSL/Windows global shells, OSC 7 directories and app icons (opt-in) | `WslUbuntuIntegrationTests` |
| OSC 7501 detection before DA1, direct/prewarmed startup, shell command execution and prompt status | `ProgramStatusTransportTests` |
| Saved layout validation, pane limits, focus, relative directories, and status origins | `SavedTerminalLayoutTests` |
| Controlled DA1 startup without personal shell profiles, buffered input, and disposal | `TerminalStartupRegressionTests` |
| Closing the first tab after path completion, followed by deferred WPF focus loss/unload | `TerminalCloseTabRegressionTests` |
| Rejected/replaced session exits and early-exit replay | `TerminalSessionExitRegressionTests` |
| Bounded crash reports and fatal-process termination (Debug) | `AppCrashLogTests` |

Filesystem cases use a temp directory. Renderer tests launch the built app
with an isolated temporary profile, enforce a 60-second timeout, and retain
reports and screenshots on failure. They never touch the user's profile.
Renderer subprocesses clear inherited `VEX_*` flags and assert the selected
scenario's report header, so clipboard/fatal/replay diagnostics cannot silently
replace the intended test.

WSL integration fixtures opt in with `VEX_TEST_UBUNTU=1` and require an installed
`Ubuntu` distribution. They resolve the default user's home rather than assuming
a username. Use an isolated `VEX_PROFILE_DIR` when running these fixtures locally.

### Vex.Libghostty.Tests

Exercises the real `ghostty-vt.dll` through the wrapper. The DLL is checked in
under `Vendor/libghostty/` and copied to the test output by
`Vex.Libghostty.csproj`, so no extra setup is needed.

| Area | File |
|---|---|
| DCS stripping by ConPTY | `FeedFilterTests` |
| Local and WSL working directories reported by chunked OSC 7 sequences | `WorkingDirectoryTests` |
| OSC 7501 parsing, atomic limits, reset/lifetime rules, inheritance, Unicode sanitization, and legacy progress | `ProgramStatusTests` |
| Legacy/xterm/Kitty keyboard encoding | `KeyboardTests` |
| Mouse tracking modes and wire encoding | `MouseTests` |
| Ad-hoc encoder inspection | `Probe` |

`Probe` is a scratch test used while investigating encoder behaviour. It is
allowed to be verbose and is not part of the correctness contract.

## Renderer self-test

The renderer runs inside the Debug app. Debug `dotnet test` launches both
the core and incremental scenarios automatically. Release tests omit these
scenarios because CI runners have no desktop session. You can also launch
the harness manually and choose the report path.

Set `VEX_SELFTEST` to a report path and launch the app:

```powershell
$env:VEX_SELFTEST = "$PWD\selftest.txt"
$env:VEX_PROFILE_DIR = "$env:TEMP\vex-selftest-profile"
dotnet run --project windows/Vex.App
```

Instead of driving a shell, it feeds scripted VT output straight into the
emulator through the same pump the live shell uses, then renders the control to
a bitmap and checks the pixels: a full screen of text, a clean screen after
ED2 (no ghost rows), wide-character rendering, and pixel-identical snap-back
after scrolling. It also checks glyph alignment, combining marks, font and
DPI changes, copy feedback, link previews, and renderer lifecycle cleanup.
Copy checks reject blank/offscreen sources and deleted text even when the editor's
output repaint is pending. Preview checks cover hidden live frames, atomic
synchronized output, immutable cached images and unchanged terminal geometry.
Overview opening/closing exercises the actual window and refresh-timer lifecycle.
Startup checks also invoke the actual new-tab shell dropdown and split submenu,
verify menu rebuilding does not duplicate actions, and check that changing a
project default leaves existing pane shells intact. They also check the project-style
active row, separate project shell settings, and WSL selectors hidden for one
choice but shown for two. Temporary tabs/splits cannot
schedule a second self-test while the main scenario pumps the dispatcher.
Failures produce a nonzero exit code.

The core scenario also checks that retained glyph spacing stays immutable across
redraws and follows DPI changes. It opens the path picker in the real terminal
control, types overlapping queries, navigates directories while preserving the
query when moving up, captures the popup, and checks the exact quoted/raw PTY
input on primary and alternate screens.
The popup screenshot is retained beside the report as `.path-completion.png`.

Set `VEX_SELFTEST_BENCH=1` to measure full redraw time and allocation for
ASCII, ANSI-colored text, and graphemes. The report includes font, grid size,
and DPI so results can be compared with the same settings.

Set `VEX_SELFTEST_INCR=1` to run only the incremental-repaint scenario. It
streams chunked output, scrolls, resizes the grid, commits synchronized-output
frames, and overwrites lines in place; after every phase it compares the pixels
the incremental redraw produced against a full repaint of the same buffer, and
scans cells the buffer says are blank for leftover ink. That is the harness for
duplicated/ghost text: a stale row, a half-applied synchronized-output frame,
or a redraw pass that threw all show up as a phase that fails.

### Published clipboard regression

The clipboard also needs testing in a published single-file Release build: unit
tests do not exercise the framework-assembly pruning target. On an interactive
Windows desktop, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File windows/test-published-clipboard.ps1
```

This publishes a disposable app with the self-test harness enabled, replays a
mouse selection and release through the real copy path, and checks the clipboard
text and selection cleanup. It uses an isolated profile but **replaces the system
clipboard text**. Failures retain the report and published app in the printed
temporary directory. `-AppPath` can reuse a build published with
`-p:DefineConstants=VEX_SELFTEST`.

### Live scenarios

These drive a real ConPTY session and pixel-check the result against the
emulator buffer.

| Variable | Effect |
|---|---|
| `VEX_LIVE=1` | Drive a real PTY instead of the deterministic replay. |
| `VEX_LIVE_PROMPT=1` | Enter a TUI, exit, and type; reproduces duplicate-prompt bugs. |
| `VEX_LIVE_STRESS=1` | Output flood (`nu`, `gh help`) to test the pump under load. |
| `VEX_LIVE_MOUSE=1` | Mouse reporting scenario. |
| `VEX_LIVE_SHELL` | Shell to launch for the live scenarios. |
| `VEX_LIVE_DIR` | Working directory for the live session. |
| `VEX_LIVE_FILE` | File the file-edit scenario opens. |
| `VEX_SELFTEST_REPLAY` | Replay a captured VT stream from a file. |

Example — the duplicate-prompt scenario:

```powershell
$env:VEX_SELFTEST = "$PWD\prompt.txt"
$env:VEX_LIVE = "1"
$env:VEX_LIVE_PROMPT = "1"
dotnet run --project windows/Vex.App
```

### Diagnostics

| Variable | Effect |
|---|---|
| `VEX_DIAG` | Emit redraw and pump diagnostics while running. |
| `crash.log` in the profile directory | Latest managed fatal exception; handlers log but do not suppress the crash. |
| `VEX_STARTUP_DIAG` | Print startup timing marks gathered by `StartupMark`. |

## Landing page

The landing page in `web/` is static markup with no logic of its own, so it
has no test suite. `pnpm run check` is the gate there: it verifies formatting,
lint rules, and types. The production build is checked too, since a type error
that only surfaces in `tsc -b` would otherwise reach the deploy step.

```powershell
cd web
pnpm install --frozen-lockfile
pnpm run check   # formatting, lint, types
pnpm run build   # production build
```

Useful when reviewing a change to that page:

- New screenshots must exist under `web/public/shots/` and carry `alt` text.
  Nothing enforces this automatically, so check it by eye.
- The advertised license must agree with `LICENSE`. It is GPL-3.0; the page
  once said MIT while `LICENSE` said otherwise.
- Every `to="..."` target must be a route declared in `router.tsx`.

## What CI runs

`.github/workflows/native.yml` checks Zig formatting and portable packaging,
cross-compiles Debug/ReleaseSafe on Linux, then executes both compiled test suites
on Windows. It does not require a Windows Zig or .NET installation.

`.github/workflows/windows.yml` runs the legacy .NET suite only for relevant
`windows/` changes. `.github/workflows/web.yml` checks and builds the landing page.

`.github/workflows/release.yml` reuses native CI, builds a ZIP and WiX MSI from the
tested ReleaseSafe output, verifies checksums and compares the administratively
extracted MSI files with staging. Stable tags must match `native/VERSION`. Manual
preview runs produce branch artifacts without creating tags or GitHub Releases.

Hosted CI does not run interactive GPU/desktop tests. Package verification and
Windows unit tests are not a substitute for a local renderer smoke test.
