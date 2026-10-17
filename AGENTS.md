# AGENTS.md

Vex is a native Zig/Win32 terminal workspace: ConPTY sessions, tabs and split panes.
The active app lives in `native/`; `windows/` is the legacy WPF implementation.

## Build and verify native code

Use `nix develop` on Linux/WSL for the pinned Zig/Python toolchain. From `native/`:

```sh
zig fmt --check build.zig src
zig build
zig build test-build
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 tools/package.py
```

Run the compiled `vex-native-tests.exe` with its adjacent DLL on Windows, or use
`zig build test` on a Windows development machine. Linux compilation is not a
Windows test pass. Interactive desktop tests and benchmark tools are local,
not repository scripts or hosted CI coverage. MSI packaging runs on Windows CI;
local WSL ZIP packaging does not need .NET. See `native/README.md`.

## Legacy WPF / web Linux orbs

- `.agents/setup` caches .NET 10, Node.js 24, the pnpm version from `web/package.json`, and NuGet/web dependencies. `.agents/resume` only checks cached tools and repairs the kernel's Oxlint memory setting; do not reinstall on every wake.
- Login shells load the toolchains from `~/.bash_profile`. In an already-running agent with an older environment, use `bash -lc '<command>'`.
- Cross-compile managed projects with `dotnet build windows/Vex.slnx --no-restore -c Release -p:EnableWindowsTargeting=true`.
- WPF/ConPTY execution, MSI packaging, and native `ghostty-vt.dll` tests still require Windows. Do not report Linux compilation as passing those tests.
- Web checks: `pnpm --dir web run check` and `pnpm --dir web run build`. Use `pnpm --dir web exec vp` for Vite+ commands; no global `vp` is needed.

## Conventions

- Match the style of the file you're editing.
- Comments explain _why_, not what.
- Every commit message must start with a conventional-commit prefix: `feat:`, `fix:`, `refactor:`, `perf:`, `docs:`, `chore:`, `ui:`, `vendor:`, `style:`, `test:`, `build:`, `ci:`, or `revert:` (optionally scoped, e.g. `fix(terminal):`). Never commit with a plain message.
