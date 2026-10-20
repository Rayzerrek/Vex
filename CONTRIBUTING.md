# Contributing to Vex

## Prerequisites

- **Windows 10 1809+ or Windows 11 x64** to run the application and compiled tests.
- **Zig 0.15.2 and Python 3.11+** to build the app in `app/`. `nix develop`
  provides the pinned toolchain on Linux/WSL, including cross-compilation and
  portable ZIP packaging. .NET is needed only for WiX/MSI packaging on Windows.
- **Node.js 24 and pnpm** for the website in `web/`. The pinned pnpm version is
  in `web/package.json`.

## Building and testing

From the repository root:

```sh
nix develop
cd app
zig fmt --check build.zig src
zig build
zig build test-build
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 tools/package.py
```

On Windows, run `zig build test`, or run the cross-compiled
`zig-out/bin/vex-native-tests.exe` with its adjacent DLLs and
`vex-conpty-fixture.exe`. A Linux cross-build is not a Windows test pass.
Interactive GPU/desktop checks require a local Windows session and are not
claimed by headless CI.

See [app/README.md](app/README.md) for source layout, controls, configuration and
packaging, and [docs/testing.md](docs/testing.md) for verification details.
The old WPF implementation is available in Git history, not in the current tree.

## Website

```sh
pnpm --dir web install --frozen-lockfile
pnpm --dir web run check
pnpm --dir web run build
```

`check` includes formatting, lint and types. Use `pnpm --dir web exec vp` for
local Vite+ commands without a global CLI.

The app's Zig/Python toolchain is separate and provided by `nix develop`.

## CI and releases

- **Native Zig**: formatting, packaging tests, Linux Debug/ReleaseSafe builds,
  then execution of both compiled Zig test suites on Windows.
- **Web**: formatting, lint, types and production build.
- **Native packages**: reuses application checks, creates ZIP/MSI from tested
  ReleaseSafe binaries, verifies checksums and compares extracted MSI files
  against staging. The entry point is `app/tools/build-installer.ps1`.

`app/VERSION` is the version source of truth. A matching `vX.Y.Z` tag starts
`.github/workflows/release.yml`, which resolves the exact commit and publishes
verified assets. Existing executable names, installer identity and
`Vex-Native-<version>-win-x64` package names remain unchanged.

A branch preview creates Actions artifacts, not a tag or GitHub Release:

```sh
gh workflow run release.yml --ref <branch> -f mode=preview
```

Artifacts and source provenance are retained for 14 days. Preview MSI uses the
stable install location: use an isolated test machine. Stable retries use
`-f mode=release -f tag=vX.Y.Z` on a workflow revision containing the current pipeline.

## Guidelines

- Keep unrelated refactors in separate changes and match the surrounding style.
- Explain _why_ in comments, rather than restating code.
- Add regression tests for behavior changes and do not add runtime dependencies
  without discussing them first.
- Start every commit message with a conventional-commit prefix: `feat:`, `fix:`,
  `refactor:`, `perf:`, `docs:`, `chore:`, `ui:`, `vendor:`, `style:`, `test:`,
  `build:`, `ci:`, or `revert:`. An optional scope is encouraged.

## Reporting problems

Use the bug report template with the Vex version, Windows build, reproduction
steps, expected behavior and actual result. For rendering issues, include the
shell, font and TUI application. Follow [SECURITY.md](SECURITY.md) for security
reports; do not open a public issue for a vulnerability.
