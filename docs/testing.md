# Testing

Vex has three layers of tests. The first two run in CI; the third needs a real
window and is run manually.

## Unit and integration tests

```powershell
dotnet test windows/Vex.slnx
```

### Vex.App.Tests

Covers the shell's pure logic, with no window and no PTY:

| Area | File |
|---|---|
| Key events to VT byte sequences | `TerminalKeyMapTests` |
| Fuzzy search scoring and ordering | `FileSearchEngineTests` |
| Debounce timing and coalescing | `HalfDebouncerTests` |
| Theme resolution and color parsing | `TerminalThemeTests` |
| Displayed version derivation | `AppInfoTests` |

These use a temp directory for filesystem cases and never touch
`%LocalAppData%`, so they are safe to run in parallel.

### Vex.Libghostty.Tests

Exercises the real `ghostty-vt.dll` through the wrapper. The DLL is checked in
under `Vendor/libghostty/` and copied to the test output by
`Vex.Libghostty.csproj`, so no extra setup is needed.

| Area | File |
|---|---|
| DCS stripping by ConPTY | `FeedFilterTests` |
| Mouse tracking modes and wire encoding | `MouseTests` |
| Ad-hoc encoder inspection | `Probe` |

`Probe` is a scratch test used while investigating encoder behaviour. It is
allowed to be verbose and is not part of the correctness contract.

## Renderer self-test

The renderer needs a real window, so it is not part of `dotnet test`. It runs
inside the app and writes a report to a path you choose.

Set `VEX_SELFTEST` to a report path and launch the app:

```powershell
$env:VEX_SELFTEST = "$PWD\selftest.txt"
dotnet run --project windows/Vex.App
```

Instead of driving a shell, it feeds scripted VT output straight into the
emulator through the same pump the live shell uses, then renders the control to
a bitmap and checks the pixels: a full screen of text, a clean screen after
ED2 (no ghost rows), wide-character rendering, and pixel-identical snap-back
after scrolling.

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

```powershell
cd web
pnpm install --frozen-lockfile
pnpm run check   # formatting, lint, types
pnpm run test    # vitest, no DOM
pnpm run build   # production build
```

The landing page is static, and the failures worth catching are content
failures rather than rendering ones. `web/src/app.test.ts` therefore reads
`app.tsx` and the repository files directly and asserts on facts that have
either broken here before or would break silently:

- every screenshot path referenced by the page resolves to a real file, and
  every `<img>` carries alternative text
- the license the page advertises matches `LICENSE`, and it never says MIT
  again (it did once, while `LICENSE` was GPL-3.0)
- every `to="..."` target is a route declared in `router.tsx`, and the
  download links point at the GitHub repository

This keeps the suite dependency-free: no jsdom, no DOM query library, and no
React runtime in the test path. Vitest is the runner and comes from `vite-plus`,
so `vp test` needs no extra runtime dependency.

Tests use Vitest globals (no `import { expect } from "vitest"`); the types come
from `vite-plus/test/globals` in `tsconfig.app.json`. Importing from `vitest`
directly fails, because pnpm resolves a second copy of the package that does
not share the runner's context.

## What CI runs

`.github/workflows/windows.yml` restores, builds in Release, and runs
`dotnet test` over the solution. `.github/workflows/web.yml` installs with a
frozen lockfile, then runs `check`, `test`, and `build`.

Neither workflow runs the renderer self-test: GitHub runners have no desktop
session capable of creating the window it needs.
