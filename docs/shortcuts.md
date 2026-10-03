# Shortcuts

Two rules decide which key goes where:

1. **Plain `Ctrl+<key>` belongs to the terminal.** TUI applications such as
   nvim, tmux, and fzf keep their own bindings; Vex only intercepts `Ctrl+C`
   when text is selected, so copy still works.
2. **Vex uses `Ctrl+Shift+<key>`.** That namespace is reserved for the
   workspace. These shortcuts apply universally across all panes, shells,
   full-screen TUIs (such as nvim).

## Workspace

| Action | Keys |
|---|---|
| Command palette | `Ctrl+Shift+P` |
| Theme picker | `Ctrl+Shift+M` |
| Tab peek (visual overview) | `Ctrl+Shift+Space` |
| Insert file or directory path | `Ctrl+Shift+F` |
| New tab | `Ctrl+Shift+T` |
| Close pane or tab | `Ctrl+Shift+W` |
| Next tab | `Ctrl+Tab` |
| Previous tab | `Ctrl+Shift+Tab` |
| Split right | `Ctrl+Shift+R` |
| Split down | `Ctrl+Shift+D` |
| Close overlay / settings | `Esc` |

`Ctrl+Tab` is the exception used for tab navigation. `Ctrl+S` goes to the
terminal/TUI, including its save binding or terminal flow control.

## Path picker

`Ctrl+Shift+F` opens a compact path picker at the terminal cursor, including
inside Claude and full-screen applications. Type to fuzzy-search file and
directory paths. Search runs in memory on a background thread; it does not
launch a process per keystroke.

| Action | Keys |
|---|---|
| Select match | `Up` / `Down`, `PageUp` / `PageDown`, mouse wheel |
| Insert selected path without executing the command | `Enter` |
| Insert raw path for application prompts, without shell quoting | `Ctrl+Enter` |
| Enter selected directory / insert selected file | `Tab` |
| Enter selected directory | `Right` |
| Parent directory | `Shift+Tab`, `Alt+Left`, or `Backspace` with an empty query |
| Clear query | `Ctrl+Backspace` |
| Paste a query or directory path | `Ctrl+Shift+V` |
| Close | `Esc` or `Ctrl+Shift+F` |

Typing a directory prefix followed by `/` or `\` navigates there; absolute
paths, `..`, and `~/` are supported. Double-click enters a directory or inserts
a file. Insertions use absolute paths and bracketed paste when supported.
PowerShell, Nushell, cmd, Git Bash, and WSL paths use their respective quoting
and drive syntax. Alternate-screen applications receive raw paths.

The picker starts from the last local directory reported by the shell using
OSC 7, or a recognized default cmd/PowerShell prompt. Otherwise it uses the
pane's starting directory; custom prompts and remote shells may require
manual navigation. WSL and Git Bash can insert paths to Windows files, but the
index searches the Windows filesystem, not a remote/Linux filesystem.

Indexing publishes immediate directory entries first and then discovers
descendants. Recursive search skips `.git`, `node_modules`, build output,
virtual environments, and junctions; these directories remain available for
explicit navigation. Scans are bounded to 100,000 entries and 32 levels. Each
pane retains up to four directory snapshots for 30 seconds, refreshing older
ones when opened. The footer indicates indexing or a truncated scan.

## Terminal

These are handled by the terminal surface and only apply on the primary screen.
Full-screen applications own the key instead, because they use arrows, paging,
and Shift combinations themselves.

| Action | Keys |
|---|---|
| Copy selection | `Ctrl+C` or `Ctrl+Shift+C` |
| Paste | `Ctrl+Shift+V` |
| Cut selection | `Ctrl+Shift+X` |
| Delete selected input ending at the shell cursor | `Backspace` |
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

`Backspace` deletes a keyboard selection ending at the live shell cursor within
one logical line, including text soft-wrapped across rows. It sends one backspace
per selected grapheme, preserving spaces. Selections in scrollback, across hard
newlines, or beyond the cursor keep ordinary Backspace behavior. Cut uses the
same deletion rules; terminal output itself is not editable.

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

Mouse selections are automatically copied and cleared on release by default.
Disable **Settings → Terminal → Selection → Copy on select** to keep selections
highlighted until you explicitly copy them. Keyboard selections are always copied
explicitly. The setting takes effect immediately and is saved across restarts.

When a TUI requests mouse reporting (vim with `mouse=a`, tmux, htop), clicks,
drags, and wheel events are encoded in the negotiated format and sent to it, and
Vex's own click-to-position is suppressed until the application releases the
mouse.
