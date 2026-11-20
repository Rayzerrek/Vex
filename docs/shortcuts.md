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
| Show / hide tab sidebar | `Ctrl+Shift+B` or the sidebar button |
| Path picker | `Ctrl+Shift+F` |
| New tab | `Ctrl+Shift+T` |
| Close active pane or tab | `Ctrl+Shift+W` |
| Close application | `Alt+F4` |
| Next / previous tab | `Ctrl+Tab` / `Ctrl+Shift+Tab` |
| Split right / down | `Ctrl+Shift+R` / `Ctrl+Shift+D` |
| Font size / reset | `Ctrl++` / `Ctrl+-` / `Ctrl+0` |
| Close overlay | `Esc` |

Closing the final tab closes the application. Both paths review active programs
before exiting when close confirmation is enabled; idle shells close immediately.

At eight tabs, **New tab** and the adjacent shell picker are disabled. These
buttons and the sidebar toggle activate on release over the same control.
Releasing outside or pressing `Esc` cancels the click. Holding `Ctrl+Shift+T`
opens just one new tab.

**Preferences → General → Tab layout** selects horizontal tabs, the vertical
sidebar, or the vertical sidebar hidden by default. **Tabs: Use Horizontal
Tabs** and **Tabs: Use Vertical Tabs** in the command palette save the layout.
In vertical mode, **Tabs: Show Sidebar**, **Tabs: Hide Sidebar**, and
`Ctrl+Shift+B` only change the current window. Click the eye again or repeat
`Ctrl+Shift+Space` to close Tab Overview without selecting the preview.

The sidebar shows each tab's application icon, title, and status from
[OSC 7501](https://www.superlogical.com/rex/docs/build/program-status).
Hover over a status to see its message and progress if provided. In vertical
mode, tabs are only shown in the sidebar; hiding it leaves the title bar free
of tabs. A dot on the sidebar button flags other tabs that need attention.
`Ctrl+Tab` and `Ctrl+Shift+Space` work in either layout.

Drag tabs along the strip to reorder them. Double-click a tab title to rename it.
The palette's rename command reveals the sidebar if it is hidden.
Drag a split pane by its header to move it; highlighted edges show the split
destination. Drag over another tab to reveal its panes, or onto the
plus button to detach the pane into a new tab. `Esc` cancels the drag.

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

Pasting keeps line breaks and tabs when the shell or TUI enables bracketed
paste. Otherwise Vex inserts spaces instead of sending Enter or Tab, so a
multiline paste does not submit commands by itself. Check the resulting line
before pressing Enter.

Wrapped URLs are highlighted as one link. `Ctrl+Click` on any part opens the
full destination, including OSC 8 links whose displayed label differs from the URL.
Vex does not open truncated plain-text URLs when a logical line is clipped.

The terminal wheel scrolls smoothly, retaining small touchpad deltas. Windows'
client-area animation preference controls the transition; scrollbar dragging and
keyboard paging remain direct.

Mouse selections copy and clear on release by default; disable **Copy on select**
in Settings to copy explicitly.

See the [website guides](../web/README.md) for path-picker, mouse and selection
behavior.
