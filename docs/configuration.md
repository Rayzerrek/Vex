# Configuration

Vex keeps its state in `%LocalAppData%\Vex\`:

| File | Contents |
|---|---|
| `settings.json` | Appearance, theme, font, cursor, shell profiles. |
| `session.json` | Projects, shell defaults, pane shells/directories, split tree, divider ratios, and focus. |

Both are written by Vex and are safe to delete: the app recreates them with
defaults on the next start. Close Vex before editing them, or your change is
overwritten by the debounced save.

## settings.json

| Key | Type | Default | Meaning |
|---|---|---|---|
| `Appearance` | `"Dark"` \| `"Light"` | follows Windows | App-wide light or dark chrome. |
| `ThemeName` | string | `"Vex Dark"` | Terminal theme. Must exist in the current appearance, or `"Custom"`. |
| `FontFamily` | string | `"Cascadia Mono"` | Any installed font family. |
| `FontSize` | int | `14` | Terminal font size in points. |
| `CursorBlink` | bool | `true` | Blink the terminal cursor. |
| `ShellId` | string | `"system"` | Default shell profile for new projects. |
| `CustomShells` | array | `[]` | User-defined shell profiles. |

Example:

```json
{
  "Appearance": "Dark",
  "ThemeName": "One Dark",
  "FontFamily": "Cascadia Mono",
  "FontSize": 14,
  "CursorBlink": true,
  "ShellId": "nu",
  "CustomShells": [
    {
      "Id": "custom-1",
      "Name": "Debian (WSL)",
      "Program": "C:\\Windows\\System32\\wsl.exe",
      "Arguments": "-d Debian",
      "IsBuiltIn": false
    }
  ]
}
```

### Shell selection

`ShellId` is the preference for **new projects**, not a live setting for every
terminal. Each project has its own default; each terminal retains the shell it
was created with. Switching projects or changing defaults never restarts or
rewrites existing terminals, including panes not yet displayed.

- **+** and ordinary splits use the project's default shell.
- The arrow beside **+** opens a compact, project-style list of installed shells.
  The current default is marked; at most three common choices are shown directly,
  with remaining installed profiles under **More shells**.
- Change **Default shell** from the project picker, separately from opening a
  terminal. It affects new terminals only.
- Right-click a tab or pane header for shell-specific splits.
- A Nushell-default project can therefore have independent PowerShell tabs and
  panes without changing its default.

Detected Windows profiles use fixed ids:

| Id | Shell |
|---|---|
| `system` | Whatever the OS default shell is (the default). |
| `pwsh` | PowerShell 7 |
| `nu` | Nushell |
| `powershell` | Windows PowerShell |
| `cmd` | Command Prompt |
| `gitbash` | Git Bash |
| `wsl` | The default Linux user's shell inside WSL |

Detection runs once per start and only offers shells that are actually
installed: Git Bash is looked for in three common install roots, and Windows
PowerShell is resolved from `System32`. If an id is present but its executable
is gone, Vex falls back to the system default rather than failing to start,
except for Linux shell launches: those report a launch error rather than silently
starting in a Windows shell.

Custom profiles are matched by `Id` and may use any `Program` and `Arguments`.
Arguments are parsed as a command line, so quoting works the way it does in a
shell.

### WSL projects

Selecting WSL in Settings opens a **Home** project in the default distribution.
A fresh WSL workspace resolves Linux Home asynchronously instead of briefly
launching a Windows terminal. Returning to Home reuses its existing tabs.
Existing Windows projects keep their own defaults.

Open the project picker and choose **Add WSL project…**. Select a distribution,
a default Linux shell and a directory: `~`, `~/src/project`, or an absolute path
such as `/home/you/src/project`. The picker can also browse the distribution's
folders. Vex checks the directory inside Linux; no Explorer shortcuts or mapped
drives are needed. Discovery and validation are cancellable and do not block
the UI. Distribution and shell selectors appear only when there is more than
one available choice; a single installation is shown as a compact environment
summary instead. Login-shell aliases are not listed twice, and compatibility
shells such as sh/dash are omitted unless they are the login shell or already
selected as the project's default.
**Add Windows project…** remains available separately.

Projects are saved as `\\wsl.localhost\Distribution\path` and restore in the same
distribution. The shell menus discover installed Linux shells there on demand;
explicit Linux choices are stored as `wsl:/absolute/path/to/shell`. The default
choice uses the Linux user's `$SHELL`, falling back to `/bin/sh`.

Windows shells are offered separately under **Windows shells**, with each row
clearly labeled **Windows Home**.
Choosing one explicitly creates an independent terminal in your Windows user
folder, not in a Linux directory. Ordinary new tabs and splits still use the
Linux project's default. Prewarming follows the actual pane's shell and directory,
including Windows overrides restored inside a WSL project.

When a shell emits OSC 7 directory reports, Vex maps its Linux directory back
to the project's distribution for new splits and path completion. Without those
reports, the pane retains its launch directory; Vex does not modify Linux shell
profiles.

### Saved pane layouts

Right-click a tab and choose **Save layout…**. The editor keeps the split tree,
split ratios, focused pane and each terminal's shell; each pane has a directory
and an optional startup command. Relative directories resolve against the project where the layout is
opened. Use **Saved layouts** in the tab menu, or search the existing command
palette for the layout name, to open it in a new tab. **Edit layout…** in the
same menu changes or deletes a saved layout.

`SavedLayouts` in `settings.json` stores these templates. Saving or editing never
executes commands; opening a template explicitly starts them once in new shells.
Ordinary session restoration does not replay template commands. Vex validates
every directory before adding the new tab and lets you repair a missing path in
the editor. A saved Windows shell requires a Windows directory; incompatible
Linux directories are rejected before any commands run. Older templates without
shell choices inherit the target project's default. Up to 32 templates with
32 panes each are supported.

### Program status and shell integration

Pane headers, tabs, project rows and the tab overview show a small static glyph:
`◌` working, `?` needs input, `✓` finished, or `!` failed. Hover for the program's
title, message and progress when supplied. Aggregate tooltips identify the
source tab and pane separately from program-supplied text. A blocked pane takes priority over
other statuses, including in background tabs and projects. Typing in a pane
acknowledges completed/failed results; a request for input stays until the
program clears it or returns to the shell prompt.

Built-in PowerShell and Nushell launches provide prompt/command markers without
editing the user's profile files. Short successful shell commands return to idle;
successful commands lasting at least two seconds show a completion result.
Command Prompt provides prompt markers only, so it does not claim command
running/error detection. Other shell profiles retain their launch arguments and
can supply their own OSC 133 or OSC 7501 reports.

Vex consumes [OSC 7501 program status](https://www.superlogical.com/rex/docs/build/program-status)
reports and capability queries, with bounded hierarchical records and messages.
Applications must emit the protocol to report detailed status such as approval
requests. OSC 9;4 progress is a fallback until the pane receives an explicit
program status report. These statuses are separate from alternate-screen mode.
Each pane's header updates are batched at most ten times a second without idle polling or
animated status indicators.

### Appearance and themes

`Appearance` is `"Dark"` or `"Light"`. On first run, and whenever the value is
empty or unrecognised, it follows the Windows
`AppsUseLightTheme` registry setting.

`ThemeName` is only honoured when it belongs to the current appearance: a dark
theme name left over from a light session falls back to that appearance's first
theme rather than re-tinting the app. The value `"Custom"` selects the theme
edited by the built-in theme editor.

The built-in themes are:

| Dark | Light |
|---|---|
| Vex Dark, One Dark, Solarized Dark, Catppuccin Mocha, Rosé Pine | Vex Light, One Light, Solarized Light, GitHub Light, Catppuccin Latte, Rosé Pine Dawn |

Each theme defines a background, foreground, cursor, selection colour, and the
16 ANSI colours as `#RRGGBB` strings.

### Legacy key

Early versions stored the shell as a display name in a `Shell` key instead of
an id. On load, Vex migrates it into `ShellId` when the file has no `ShellId`
key, and leaves files that already have one untouched. The `Shell` key is kept
so older files still deserialize; it is no longer read.

## session.json

`session.json` records what to restore on start: the open projects, each
project's default shell, each tab's split tree, individual pane shells and working
directories, and which pane held focus. It is written when the layout changes
and on exit. Older sessions inherit the current preference for Windows projects
and a Linux shell for WSL directories; the next save records those choices.

Restoring a session only restores layout, not program state. A shell is
restarted in each pane's recorded working directory; whatever was running in it
is not resumed.

If a recorded project directory no longer exists, Vex drops that project rather
than opening an empty workspace. To start clean, delete the file.

Older sessions may contain built-in editor panes ("type": "editor").
They now restore as terminal panes in the project's working directory,
preserving the split layout and focused pane. The next save records them as
terminal panes. Referenced files are not opened or modified during migration.

## Resetting

To return to defaults, close Vex and delete both files:

```powershell
Remove-Item "$env:LocalAppData\Vex\settings.json", "$env:LocalAppData\Vex\session.json"
```
