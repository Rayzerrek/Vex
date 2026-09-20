# Shortcuts

Two rules decide which key goes where:

1. **Plain `Ctrl+<key>` belongs to the terminal.** TUI applications such as
   nvim, tmux, and fzf keep their own bindings; Vex only intercepts `Ctrl+C`
   when text is selected, so copy still works.
2. **Vex uses `Ctrl+Shift+<key>`.** That namespace is reserved for the
   workspace. Full-screen applications receive these chords too, so a TUI can
   use them when it is on the alternate screen.

## Workspace

| Action | Keys |
|---|---|
| Command palette | `Ctrl+Shift+P` |
| Theme picker | `Ctrl+Shift+M` |
| Tab peek (visual overview) | `Ctrl+Shift+Space` |
| New tab | `Ctrl+Shift+T` |
| Close pane or tab | `Ctrl+Shift+W` |
| Next tab | `Ctrl+Tab` |
| Previous tab | `Ctrl+Shift+Tab` |
| Split right | `Ctrl+Shift+R` |
| Split down | `Ctrl+Shift+D` |
| Save file (editor) | `Ctrl+S` |
| Close overlay / settings | `Esc` |

`Ctrl+S` and `Ctrl+Tab` are exceptions: `Ctrl+S` is safe to take because a shell
does not use it (terminal flow control is `Ctrl+Q`/`Ctrl+S` on some systems,
but Vex consumes it only while the editor has focus). `Ctrl+Tab` is globally
used for tab navigation.

## Terminal

These are handled by the terminal surface and only apply on the primary screen.
Full-screen applications own the key instead, because they use arrows, paging,
and Shift combinations themselves.

| Action | Keys |
|---|---|
| Copy selection | `Ctrl+C` or `Ctrl+Shift+C` |
| Paste | `Ctrl+Shift+V` |
| Cut selection | `Ctrl+Shift+X` |
| Extend selection | `Shift+Arrow`, `Shift+Home`, `Shift+End` |
| Extend selection by word | `Shift+Ctrl+Arrow` |
| Scroll to top | `Ctrl+Home` |
| Scroll to bottom | `Ctrl+End` |
| Page up / down | `Shift+PageUp` / `Shift+PageDown` |
| Open detected URL | `Ctrl+Click` |
| Delete previous word | `Ctrl+Backspace` |

`Ctrl+C` sends `ETX` (interrupt) when nothing is selected, which is what shells
expect; it only copies when a selection exists. `Ctrl+Backspace` sends `0x17`,
which readline and PSReadLine bind to backward-kill-word, matching Windows
Terminal.

Copy, paste, and cut also work while a full-screen application is running,
since `Ctrl+Shift` chords pass through to Vex there.

## Mouse

| Action | Effect |
|---|---|
| Click | Place the caret, or report the click to a TUI that asked for mouse input |
| Drag | Select text (60% cell threshold, Ghostty semantics) |
| Double-click | Select word |
| Triple-click | Select line |
| Drag pane title bar | Split toward an edge, with a per-pane preview |
| Middle-click tab | Close tab |
| Ctrl+Click | Open the URL under the pointer |

When a TUI requests mouse reporting (vim with `mouse=a`, tmux, htop), clicks,
drags, and wheel events are encoded in the negotiated format and sent to it, and
Vex's own click-to-position is suppressed until the application releases the
mouse.
