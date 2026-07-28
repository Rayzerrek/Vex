# Vex for Windows

A Windows port of [egoist/Vex](https://github.com/egoist/Vex) — a native terminal workspace.

This project reimplements Vex on a Windows-native stack (WPF + ConPTY) inspired by the original macOS app.

## Requirements

- Windows 10 1809 or later (required for ConPTY)
- [.NET SDK](https://dotnet.microsoft.com/download)
- WebView2 runtime (pre-installed on Windows 10/11)

## Build and run

```sh
dotnet build windows/Vex.slnx
dotnet run --project windows/Vex.App
```

## Layout

```
windows/
  Vex.slnx
  Vex.App/        WPF shell: sidebar, tabs, split panes, file tree, git panel, editor
  Vex.Terminal/   ConPTY interop and session management
```

## Stack

| Concern | Implementation |
|---|---|
| UI framework | WPF on .NET |
| PTY | ConPTY |
| Terminal emulation | XtermSharp (vendored) with a native WPF renderer |
| Git integration | git CLI, porcelain v2 |
| Default shell | pwsh.exe, fallback to powershell.exe |

## Credits

Based on [egoist/Vex](https://github.com/egoist/Vex) by [@egoist](https://github.com/egoist).

## License

GPLv3
