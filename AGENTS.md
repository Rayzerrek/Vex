# AGENTS.md

Vex is a native Zig/Win32 terminal workspace: ConPTY sessions, tabs and split panes.
The application lives in `app/`; the website lives in `web/`. The legacy WPF app
has been removed and is available only in Git history.

## Build and verify application code

Use `nix develop` on Linux/WSL for the pinned Zig/Python toolchain. From `app/`:

```sh
zig fmt --check build.zig src
zig build
zig build test-build
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 tools/package.py
```

Run the compiled `vex-native-tests.exe` with its adjacent DLLs and
`vex-conpty-fixture.exe` on Windows, or use `zig build test` on a Windows
development machine. Linux compilation is not a Windows test pass. Interactive
desktop tests and benchmark tools are local, not repository scripts or hosted CI
coverage. MSI packaging runs on Windows CI via `app/tools/build-installer.ps1`;
local WSL ZIP packaging does not need .NET. See `app/README.md`.

## Layout

- `app/src/zig/`: application, shell, terminal, workspace and Windows Zig adapters.
- `app/src/windows/`: C++ platform, window, dialogs, renderer and PTY backends.
- `app/src/lua/`: C configuration bridge.
- `app/assets/`, `app/tests/`, `app/tools/`: resources, tests and developer tools.
- `app/packages/windows/`: WiX installer sources.
- `app/vendor/`: bundled runtimes, Lua sources and Ghostty headers.
- `docs/`: architecture, configuration, correctness testing and releases.

Keep machine-specific desktop automation, benchmark harnesses, captures and local
measurement reports outside this repository. Retain genuine unit, integration,
portable regression and packaging tests here. Diagnostic hooks consumed by local
tools remain application infrastructure, not evidence that a desktop check ran.

## Website

- Use Node.js 24 and the pnpm version pinned in `web/package.json`.
- Install dependencies with `pnpm --dir web install --frozen-lockfile`.
- Checks: `pnpm --dir web run check` and `pnpm --dir web run build`.
  Use the scripts in `web/package.json`; no globally installed build tools are needed.

## Conventions

- Match the style of the file you're editing.
- Comments explain _why_, not what.
- Every commit message must start with a conventional-commit prefix: `feat:`,
  `fix:`, `refactor:`, `perf:`, `docs:`, `chore:`, `ui:`, `vendor:`, `style:`,
  `test:`, `build:`, `ci:`, or `revert:` (optionally scoped, e.g. `fix(terminal):`).
  Never commit with a plain message.
