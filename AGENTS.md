# AGENTS.md

Vex is a WPF terminal workspace: ConPTY sessions, split panes, a file tree, and an editor.
The app lives in `windows/`.

## Build

```sh
dotnet build windows/Vex.slnx
dotnet run --project windows/Vex.App
```

## Verify

Build, run the app, exercise the change.

## Conventions

- Match the style of the file you're editing.
- Comments explain _why_, not what.
- Every commit message must start with a conventional-commit prefix: `feat:`, `fix:`, `refactor:`, `perf:`, `docs:`, `chore:`, `ui:`, `vendor:`, `style:`, `test:`, `build:`, `ci:`, or `revert:` (optionally scoped, e.g. `fix(terminal):`). Never commit with a plain message.