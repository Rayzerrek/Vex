# Shortcuts

Most plain Ctrl bindings go to the shell/TUI. Exceptions include tab navigation,
preferences, font zoom and copying a selection. `Ctrl+S` remains terminal input.

## Workspace

| Action | Keys |
| --- | --- |
| Command palette | `Ctrl+Shift+P` |
| Preferences | `Ctrl+,` |
| Theme Studio | `Ctrl+Shift+M` |
| Tab overview | `Ctrl+Shift+Space` |
| Path picker | `Ctrl+Shift+F` |
| New tab | `Ctrl+Shift+T` |
| Close active pane or tab | `Ctrl+Shift+W` |
| Next / previous tab | `Ctrl+Tab` / `Ctrl+Shift+Tab` |
| Split right / down | `Ctrl+Shift+R` / `Ctrl+Shift+D` |
| Font size / reset | `Ctrl++` / `Ctrl+-` / `Ctrl+0` |
| Close overlay | `Esc` |

## Terminal

| Action | Keys |
| --- | --- |
| Copy selection | `Ctrl+C` / `Ctrl+Shift+C` |
| Paste | `Ctrl+Shift+V` |
| Cut eligible selected input | `Ctrl+Shift+X` |
| Extend selection | `Shift+Arrow` / `Shift+Home` / `Shift+End` |
| Extend by word | `Ctrl+Shift+Arrow` |
| Scroll to top / bottom | `Ctrl+Home` / `Ctrl+End` |
| Page up / down | `Shift+PageUp` / `Shift+PageDown` |
| Delete previous word | `Ctrl+Backspace` |
| Open URL | `Ctrl+Click` |

Selection/scrollback navigation applies on the primary screen; full-screen TUIs
keep their arrows and paging keys. Copy/paste/cut remain available there.
`Ctrl+C` sends an interrupt when nothing is selected. Hold Shift to bypass a
TUI's mouse reporting and select locally.

Mouse selections copy and clear on release by default; disable **Copy on select**
in Settings to copy explicitly.

See the [website guides](../web/README.md) for path-picker, mouse and selection
behavior.
