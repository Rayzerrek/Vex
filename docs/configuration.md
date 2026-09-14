# Configuration

Vex keeps its state in `%LocalAppData%\Vex\`:

| File | Contents |
|---|---|
| `settings.json` | Appearance, theme, font, cursor, shell profiles, sidebar. |
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
| `SidebarVisible` | bool | `true` | Show the project sidebar. |

Example:

```json
{
  "Appearance": "Dark",
  "ThemeName": "One Dark",
  "FontFamily": "Cascadia Mono",
  "FontSize": 14,
  "CursorBlink": true,
  "ShellId": "nu",
  "SidebarVisible": true,
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

## Resetting

To return to defaults, close Vex and delete both files:

```powershell
Remove-Item "$env:LocalAppData\Vex\settings.json", "$env:LocalAppData\Vex\session.json"
```
