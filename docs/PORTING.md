# Kero — Windows port

This repository is a fork of [egoist/kero](https://github.com/egoist/kero)
("Kero", a native terminal workspace for macOS) that reimplements the product
for Windows. The upstream macOS sources are kept in the tree as the reference
implementation; the Windows app lives under [`windows/`](../windows).

## What upstream Kero is

Analysis of the upstream tree (commit `fb7610c`, ~20k lines of Swift):

| Area | Upstream implementation | macOS coupling |
| --- | --- | --- |
| App shell | SwiftUI `WindowGroup`, hidden title bar (`keroApp.swift`) | SwiftUI, AppKit |
| Terminal rendering | libghostty by default, optional Alacritty backend with a custom Metal renderer (`kero/Alacritty/`) | Metal, libghostty (no Windows support) |
| PTY | POSIX `forkpty`-style session (`TerminalSession.swift`, `Darwin`) | POSIX only |
| Workspace model | `TerminalManager` → `Project` → `PaneTab` → panes (sessions, files, diffs) | portable concept |
| File tree / Git | `FileTreeModel`, `GitStatusModel` (git porcelain v2 over the `git` CLI) | concept portable |
| Editor / diffs | STTextView + tree-sitter, WKWebView diff viewer | macOS frameworks |
| Updates / release | Sparkle appcast, Homebrew cask (`scripts/`) | macOS only |
| Website | Vite + React on Cloudflare Workers (`web/`) | already cross-platform |

A line-by-line translation is not possible: SwiftUI, AppKit, Metal, Sparkle
and libghostty do not exist on Windows. The port therefore reimplements the
**product** (see `PRODUCT.md`) on a Windows-native stack, mirroring upstream's
architecture and naming where it makes sense.

## Windows stack

| Concern | Windows choice | Why |
| --- | --- | --- |
| UI framework | WPF on .NET (`windows/Kero.App`) | Native Windows, builds from the plain .NET SDK, no extra workloads |
| PTY | ConPTY (`windows/Kero.Terminal`) | The real Windows pseudo-console API; runs PowerShell/cmd/WSL faithfully |
| Terminal emulation + rendering | xterm.js inside WebView2 | The same emulator VS Code uses; full VT support, GPU-rendered |
| Git integration | `git` CLI, porcelain v2 | Same approach as upstream |
| Default shell | `pwsh.exe` if present, else `powershell.exe` | Closest analogue of "login shell" |

Architecture mapping (upstream → port):

- `TerminalManager` → `Workspace`
- `Project` → `Project` (groups tabs, one sidebar row, anchors file tree/git)
- `PaneTab` / panes → `WorkspaceTab` / `TerminalPane` (splits via `GridSplitter`)
- `TerminalSession` → `TerminalSession` (ConPTY process) + `TerminalControl` (xterm.js view)
- `GitStatusModel` → `GitStatusService` (porcelain v2, event-driven refresh)

## What is intentionally not ported (yet)

- The ghostty/Alacritty renderer duo — xterm.js is the single backend.
- The text editor and diff viewer panes.
- Sparkle/appcast release pipeline and the `web/` marketing site.
- Session restore across launches.

## Layout

```
kero/              upstream macOS app (reference, untouched)
windows/
  Kero.sln
  Kero.App/          WPF shell: sidebar, tabs, split panes, file tree, git panel
  Kero.Terminal/     ConPTY interop + session management
docs/PORTING.md    this file
```

## Build and run

Requires Windows 10 1809+ (for ConPTY), the .NET SDK, and the WebView2
runtime (preinstalled on current Windows 10/11):

```sh
dotnet build windows/Kero.sln
dotnet run --project windows/Kero.App
```
