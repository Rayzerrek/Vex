# Vex

Native Windows terminal workspace (WPF, .NET 10): ConPTY sessions, tabs, split panes, file tree, file search, and a built-in editor.

## Features

- Terminal: ConPTY + libghostty-vt emulation, WPF rendering. URL underline with Ctrl+Click, keyboard selection, Ctrl+Backspace word delete.
- Shells: auto-detects PowerShell 7, Nushell, Windows PowerShell, cmd, Git Bash, WSL; plus custom shells (program + args) in Settings.
- Tabs & panes: split right/down, focus mode, drag reorder, custom titles, tab peek preview, pane state indicators.
- Workspace: multi-project sidebar, lazy file tree, quick file search, session restore (projects, tabs, splits, focus).
- Editor: AvalonEdit, lazy file load, dark/light syntax themes, dirty indicator, `Ctrl+S` to save.
- Themes: Dark/Light appearance (follows OS), 11 built-in terminal themes + custom theme editor, live picker.

## Shortcuts

| Action | Keys |
|---|---|
| Command palette | `Ctrl+Shift+P` |
| Theme picker | `Ctrl+Shift+M` |
| Tab peek | `Ctrl+Shift+Space` |
| New tab / close | `Ctrl+Shift+T` / `Ctrl+Shift+W` |
| Split right / down | `Ctrl+Shift+R` / `Ctrl+Shift+D` |
| Save file (editor) | `Ctrl+S` |

Plain `Ctrl+<key>` goes to the terminal (TUI apps keep their own bindings).

## Structure

```text
windows/
  Vex.slnx            solution
  Vex.App/            WPF shell: sidebar, tabs, splits, tree, search, editor, overlays
  Vex.Terminal/       ConPTY process lifecycle
  Vex.Libghostty/     libghostty-vt wrapper + WPF rendering
  Vex.Setup/          WiX installer
Vendor/libghostty/    ghostty-vt native lib
```

Config: `%LocalAppData%\Vex\settings.json`, `%LocalAppData%\Vex\session.json`.

## Requirements

- Windows 10 1809+ / Windows 11
- .NET 10 SDK

## Build and Run

```powershell
dotnet build windows/Vex.slnx
dotnet run --project windows/Vex.App
```

## License

[GPL-3.0](LICENSE)
