# Configuration

Vex keeps its state in `%LocalAppData%\Vex\`:

| File | Contents |
|---|---|
| `settings.json` | Appearance, theme, font, cursor, shell profiles. |
| `session.json` | Projects, tabs, split tree, divider ratios, and focus. |

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
| `ShellId` | string | `"system"` | Id of the shell profile to launch. |
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

`ShellId` refers to a profile id. Detected profiles use fixed ids:

| Id | Shell |
|---|---|
| `system` | Whatever the OS default shell is (the default). |
| `pwsh` | PowerShell 7 |
| `nu` | Nushell |
| `powershell` | Windows PowerShell |
| `cmd` | Command Prompt |
| `gitbash` | Git Bash |
| `wsl` | WSL |

Detection runs once per start and only offers shells that are actually
installed: Git Bash is looked for in three common install roots, and Windows
PowerShell is resolved from `System32`. If an id is present but its executable
is gone, Vex falls back to the system default rather than failing to start.

Custom profiles are matched by `Id` and may use any `Program` and `Arguments`.
Arguments are parsed as a command line, so quoting works the way it does in a
shell.

### Saved pane layouts

Right-click a tab and choose **Save layout…**. The editor keeps the split tree,
split ratios and focused pane; each pane has a directory and an optional startup
command. Relative directories resolve against the project where the layout is
opened. Use **Saved layouts** in the tab menu, or search the existing command
palette for the layout name, to open it in a new tab. **Edit layout…** in the
same menu changes or deletes a saved layout.

`SavedLayouts` in `settings.json` stores these templates. Saving or editing never
executes commands; opening a template explicitly starts them once in new shells.
Ordinary session restoration does not replay template commands. Vex validates
every directory before adding the new tab and lets you repair a missing path in
the editor. Up to 32 templates with 32 panes each are supported.

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
project's tabs, each tab's split tree, and which pane held focus. It is written
when the layout changes and on exit.

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
