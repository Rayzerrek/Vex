# Vex

**Vex** is a native Windows terminal workspace built with WPF and Windows ConPTY. It brings low-latency terminal sessions, tabbed multi-project management, split views, a file tree, and a built-in editor into a single desktop workspace.

---

## Features

- ConPTY Terminal Sessions: Native Windows Pseudoconsole integration supporting PowerShell Core (`pwsh.exe`) and PowerShell (`powershell.exe`).
- Split Panes & Tabs: Split active panes vertically or horizontally, rename tabs, and navigate between sessions.
- Multi-Project Sidebar: Workspace management for switching between multiple open project folders.
- File Tree Explorer: Browse project directories and open files directly within workspace panes.
- Built-in Code Editor: Edit source files with syntax highlighting and file saving (`Ctrl+S`).
- Command Palette: Quick action overlay accessible via `Ctrl+P`.
- Settings Overlay: Configure shell defaults and workspace preferences.
- Workspace Session Persistence: Automatically saves and restores projects, tabs, and layout state on restart.

---

## Architecture & Project Structure

```text
windows/
  Vex.slnx              Visual Studio Solution File
  Vex.App/              WPF Shell: sidebar, tabs, split views, file tree, editor, command palette
  Vex.Terminal/         ConPTY interop and process lifecycle management
Vendor/
  XtermSharp/           Vendored xterm terminal emulation library with WPF rendering
```

### Technical Stack

| Area | Solution / Technology |
|---|---|
| UI Framework | WPF on .NET 8 |
| PTY Layer | Windows ConPTY (`kernel32.dll` APIs) |
| Terminal Renderer | XtermSharp (vendored) + WPF Native DrawingContext |
| Default Shell | `pwsh.exe` (PowerShell Core) with fallback to `powershell.exe` |

---

## Requirements

- Operating System: Windows 10 (Build 1809 or later, required for ConPTY) or Windows 11.
- SDK: .NET 8.0 SDK or higher.
- Runtime: WebView2 Runtime (pre-installed on Windows 10/11).

---

## Build and Run

```powershell
# Build the solution
dotnet build windows/Vex.slnx

# Run the desktop application
dotnet run --project windows/Vex.App
```

---

## License

Distributed under the [GPL-3.0 License](LICENSE).

