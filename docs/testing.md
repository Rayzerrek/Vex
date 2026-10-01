# Testing

Vex has three layers of tests. Unit and emulator integration tests run in CI.
Renderer tests need a desktop session and run locally in Debug.

## Unit and integration tests

```powershell
dotnet test windows/Vex.slnx
```

### Vex.App.Tests

Covers the shell's pure logic, with no window and no PTY, plus renderer
regressions in Debug:

| Area | File |
|---|---|
| Key events to VT byte sequences | `TerminalKeyMapTests` |
| Fuzzy search scoring and ordering | `FileSearchEngineTests` |
| Debounce timing and coalescing | `HalfDebouncerTests` |
| Theme resolution and color parsing | `TerminalThemeTests` |
| Dimmed foreground and contrast caching | `TerminalPaletteTests` |
| Displayed version derivation | `AppInfoTests` |
| Real-window core and incremental rendering (Debug) | `TerminalRendererRegressionTests` |

Filesystem cases use a temp directory. Renderer tests launch the built app
with an isolated temporary profile, enforce a 60-second timeout, and retain
reports and screenshots on failure. They never touch the user's profile.

### Vex.Libghostty.Tests

Exercises the real `ghostty-vt.dll` through the wrapper. The DLL is checked in
under `Vendor/libghostty/` and copied to the test output by
`Vex.Libghostty.csproj`, so no extra setup is needed.

| Area | File |
|---|---|
| DCS stripping by ConPTY | `FeedFilterTests` |
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
Failures produce a nonzero exit code.

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

`.github/workflows/windows.yml` restores, builds in Release, and runs
`dotnet test` over the solution. `.github/workflows/web.yml` installs with a
frozen lockfile, then runs `check`, `test`, and `build`.

`.github/workflows/release.yml` runs on a version tag and adds the packaging
gates: the tag must match the project version, the suite must pass, and the zip
must contain every native library the app loads at runtime. The MSI is checked
too — a stale WiX harvest once produced an installer with no files in it.

None of these workflows runs the renderer self-test: GitHub runners have no
desktop session capable of creating the window it needs. That is why the
release job verifies the zip's payload by inspection rather than by launching
it, and why a release candidate is worth starting by hand once.
