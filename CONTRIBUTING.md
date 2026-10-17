# Contributing to Vex

Thanks for taking the time to contribute. This document covers how to build
Vex, what is expected of a change, and how to get it merged.

## Prerequisites

- **Windows 10 1809+ or Windows 11** to run the native application and compiled
  Zig tests. Linux/WSL can cross-compile both and create portable ZIPs.
- **Zig 0.15.2 and Python 3.11+** for the active app in `native/`. `nix develop`
  provides the pinned toolchain on Linux/WSL. .NET is needed only for WiX/MSI
  packaging on Windows CI, or development of the legacy WPF app in `windows/`.
- **Node.js 24 and pnpm 11.5.2** — only needed for the landing page in `web/`.
  Vite+ resolves pnpm through `packageManager` in `web/package.json`.

## Building

```sh
nix develop
cd native
zig build
zig build test-build
python3 tools/package.py
```

Run the resulting Windows executables with their adjacent DLL on Windows.
The portable ZIP is created entirely in WSL. Windows CI consumes the tested
cross-build output and calls `native/build-installer.ps1 -SkipBuild -SkipTests`
to create and verify the MSI. No Windows toolchain installation is needed locally.
See [native/README.md](native/README.md) for commands and supported configurations.

For legacy WPF development, use `dotnet build windows/Vex.slnx` and
`dotnet run --project windows/Vex.App`; its packager is not used for new releases.

### Legacy WPF/web Amp orbs (Linux)

Amp runs the executable `.agents/setup` hook to prepare a project snapshot:
Debian prerequisites, .NET 10, Node.js 24, pnpm from `web/package.json`, locked
web dependencies, and NuGet packages including Windows reference packs.
Matching snapshots skip setup; warm setup reuses installed tools and caches.
Toolchains are stored under `~/.local/share/vex` and added to login-shell PATH
through `~/.bash_profile`.

The setup hook enables `vm.overcommit_memory=1` inside the orb because Oxlint's
JS plugins reserve large virtual-memory arenas that fail under the no-swap
orb's default policy. `.agents/resume` repairs this setting if needed and
checks cached tools; it performs no downloads or dependency installation.
Neither hook requires personal credentials or starts background services.

From the repository root in a prepared orb:

```sh
dotnet build windows/Vex.slnx --no-restore -c Release -p:EnableWindowsTargeting=true
pnpm --dir web run check
pnpm --dir web run build
```

The .NET command compiles the managed projects, not the MSI. WPF, ConPTY, and
the vendored Windows `ghostty-vt.dll` cannot execute on Linux, so use Windows
for application testing. The web checks and build run normally in the orb.
Use `pnpm --dir web exec vp` for local Vite+ commands without a global CLI.

To validate hook changes, run `.agents/setup` twice and `.agents/resume`,
then verify tools in a new login shell. An already-running agent may still
have the old PATH; use `bash -lc '<command>'` rather than reinstalling tools.
The hooks become available to future project orbs once committed to the
default branch; a local commit alone does not activate them.

## Testing

For native code, run `zig fmt --check build.zig src`, `zig build test` on Windows,
or `zig build test-build` in WSL followed by the compiled `vex-native-tests.exe`
on Windows. Validate portable packaging with
`python3 -m unittest discover -s native/tests -p 'test_*.py' -v` from the repo root.
Headless CI does not claim interactive rendering coverage; desktop tooling is local.

The legacy WPF suite uses `dotnet test windows/Vex.slnx`. Two projects hold tests:

- `windows/Vex.App.Tests` — key encoding, fuzzy search scoring, theme
  resolution, color parsing, and debounce behaviour.
- `windows/Vex.Libghostty.Tests` — VT emulation, feed filtering, and mouse
  encoding against the vendored `ghostty-vt.dll`.

Debug `dotnet test` also launches the renderer self-test harness in a real
window with an isolated temporary profile. Release tests omit these checks
because CI has no desktop session. See [docs/testing.md](docs/testing.md).

For the landing page:

```powershell
cd web
pnpm install --frozen-lockfile
pnpm run check
pnpm run build
```

## What CI enforces

Path-filtered workflows in `.github/workflows/` check the relevant implementation:

- **Native Zig** — formatting, packaging tests, Linux cross-builds in Debug and
  ReleaseSafe, then both compiled Zig test suites on Windows.
- **Legacy WPF** — restore, Release build and `dotnet test` for `windows/` changes.
- **Web** — formatting, lint, type checking and a production build.

`pnpm run check` also enforces formatting. Run it before pushing; CI fails on
unformatted files.

## Releasing

`native/VERSION` is the version source of truth. A matching `vX.Y.Z` tag starts
`.github/workflows/release.yml`, which resolves the exact tag commit, runs native
checks and publishes the ZIP, MSI and both SHA-256 files. Publishing uses a draft
until assets are uploaded; reruns can repair uploads. The WPF project version is
not consulted.

A branch preview does not create a tag or GitHub Release and does not modify main:

```sh
gh workflow run release.yml --ref feat/zig-native -f mode=preview
```

Download packages and source provenance from the workflow artifacts (14 days).
Preview MSI uses the stable install location, so install it only on an isolated
test machine. Stable retries use `-f mode=release -f tag=vX.Y.Z` on a workflow
revision containing the native pipeline.

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
