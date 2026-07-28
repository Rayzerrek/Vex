# CLAUDE.md

Kero for Windows is a WPF terminal workspace: ConPTY sessions, split panes, a file tree, a git panel, and an editor.
The Windows app lives in `windows/`.

## Build

```sh
dotnet build windows/Kero.slnx
dotnet run --project windows/Kero.App
```

## Verify

Build, run the app, exercise the change.

## Conventions

- Match the style of the file you're editing.
- Comments explain *why*, not what.
