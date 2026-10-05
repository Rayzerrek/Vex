# AGENTS.md

Vex is a WPF terminal workspace: ConPTY sessions, tabs, and split panes.
The app lives in `windows/`.

## Build

```sh
dotnet build windows/Vex.slnx
dotnet run --project windows/Vex.App
```

## Verify

Build, run the app, exercise the change.

```sh
dotnet test windows/Vex.Libghostty.Tests
```

## Linux orbs

- `.agents/setup` caches .NET 10, Node.js 24, the pnpm version from `web/package.json`, and NuGet/web dependencies. `.agents/resume` only checks cached tools and repairs the kernel's Oxlint memory setting; do not reinstall on every wake.
- Login shells load the toolchains from `~/.bash_profile`. In an already-running agent with an older environment, use `bash -lc '<command>'`.
- Cross-compile managed projects with `dotnet build windows/Vex.slnx --no-restore -c Release -p:EnableWindowsTargeting=true`.
- WPF/ConPTY execution, MSI packaging, and native `ghostty-vt.dll` tests still require Windows. Do not report Linux compilation as passing those tests.
- Web checks: `pnpm --dir web run check` and `pnpm --dir web run build`. Use `pnpm --dir web exec vp` for Vite+ commands; no global `vp` is needed.

## Conventions

- Match the style of the file you're editing.
- Comments explain _why_, not what.
- Every commit message must start with a conventional-commit prefix: `feat:`, `fix:`, `refactor:`, `perf:`, `docs:`, `chore:`, `ui:`, `vendor:`, `style:`, `test:`, `build:`, `ci:`, or `revert:` (optionally scoped, e.g. `fix(terminal):`). Never commit with a plain message.
