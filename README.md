# Kero for Windows

A Windows port of [egoist/kero](https://github.com/egoist/kero) — a native terminal workspace.

This project reimplements Kero on a Windows-native stack (WPF + ConPTY) inspired by the original macOS app.

## Requirements

- Windows 10 1809 or later (required for ConPTY)
- [.NET SDK](https://dotnet.microsoft.com/download)
- WebView2 runtime (pre-installed on Windows 10/11)

## Build and run

```sh
dotnet build windows/Kero.slnx
dotnet run --project windows/Kero.App
```

## Layout

```
windows/
  Kero.slnx
  Kero.App/        WPF shell: sidebar, tabs, split panes, file tree, git panel, editor
  Kero.Terminal/   ConPTY interop and session management
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

Based on [egoist/kero](https://github.com/egoist/kero) by [@egoist](https://github.com/egoist).

## License

GPLv3
