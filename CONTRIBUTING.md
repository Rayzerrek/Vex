# Contributing to Vex

Thanks for taking the time to contribute. This document covers how to build
Vex, what is expected of a change, and how to get it merged.

## Prerequisites

- **Windows 10 1809+ or Windows 11.** Vex is a WPF application and does not
  build or run on other platforms.
- **.NET 10 SDK.** Check with `dotnet --list-sdks`; the project targets
  `net10.0` and `net10.0-windows`.
- **Node.js 24 and pnpm 11.5.2** — only needed for the landing page in `web/`.
  Vite+ resolves pnpm through `packageManager` in `web/package.json`.

## Building

```powershell
dotnet build windows/Vex.slnx
dotnet run --project windows/Vex.App
```

To produce the self-contained executable and the MSI installer:

```powershell
pwsh windows/build-installer.ps1
```

This publishes `Vex.App` for `win-x64`, builds the WiX package, and writes
`windows/publish/installer/Vex.msi`.

## Testing

```powershell
dotnet test windows/Vex.slnx
```

Two projects hold tests:

- `windows/Vex.App.Tests` — key encoding, fuzzy search scoring, theme
  resolution, color parsing, and debounce behaviour.
- `windows/Vex.Libghostty.Tests` — VT emulation, feed filtering, and mouse
  encoding against the vendored `ghostty-vt.dll`.

Rendering has a separate self-test harness that is not part of `dotnet test`
because it needs a real window. See [docs/testing.md](docs/testing.md).

For the landing page:

```powershell
cd web
pnpm install --frozen-lockfile
pnpm run check
pnpm run build
```

## What CI enforces

Every pull request must pass both workflows in `.github/workflows/`:

- **Windows** — restore, Release build, and `dotnet test` across the solution.
- **Web** — formatting, lint, type checking, tests, and a production build.

`pnpm run check` also enforces formatting. Run it before pushing; CI fails on
unformatted files.

## Guidelines

- Keep changes surgical. Every changed line should trace to the issue or
  feature being addressed, and unrelated refactors belong in their own PR.
- Match the style of the file you are editing.
- Write comments that explain *why*, not *what*. Comments that restate the code
  should be deleted.
- Add tests for behaviour you change. Bug fixes should come with a test that
  fails before the fix.
- Do not add new runtime dependencies without discussing it in an issue first.

## Commit messages

Commits must start with a conventional-commit prefix:

`feat:`, `fix:`, `refactor:`, `perf:`, `docs:`, `chore:`, `ui:`, `vendor:`,
`style:`, `test:`, `build:`, `ci:`, or `revert:`

A scope is optional but encouraged, for example `fix(terminal):`. A plain
message with no prefix will be rejected.

## Reporting bugs

Open an issue using the bug report template and include:

- Vex version (shown in Settings → About) and Windows build.
- The exact steps that reproduce the problem.
- What you expected and what happened instead.

If the bug involves terminal rendering, mention the shell, the font, and any
TUI application involved — those three account for most rendering reports.

## Security

Do not open a public issue for a security problem. See
[SECURITY.md](SECURITY.md).
