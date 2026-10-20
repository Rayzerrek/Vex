# Vex Native

Windows terminal workspace written in Zig, with Win32, ConPTY, libghostty-vt and
an instanced D3D12 renderer. Tabs, split panes, shell profiles, themes and settings
are owned by the native app. No WPF, CLR, Electron or .NET runtime is required.

Requires Windows 10 1809+ / Windows 11, x64. The build uses **Zig 0.15.2** (pinned
in `.zig-version`) and defaults to **ReleaseSafe**. Debug is supported;
ReleaseFast/ReleaseSmall are deliberately rejected.

## Build in Nix / WSL

From the repository root:

```sh
nix develop
cd native
zig build
zig build test-build
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 tools/package.py
```

The last command creates `publish/Vex-Native-<VERSION>-win-x64.zip`, its SHA-256
file, and `publish/app/`. It uses only Python 3.11+; Windows tools are unnecessary.
The ZIP contains `Vex.exe`, `vex-native.exe`, `ghostty-vt.dll`, the icon and
license notices, plus Microsoft's `conpty.dll` and `OpenConsole.exe` console-host
pair, but no test executables or debug symbols. CMD, PowerShell and Nushell
integration is built in; no `ShellIntegration` folder beside the executable is
needed. Other shells (including Git Bash, WSL and custom programs) can run without
these files; command/prompt and directory tracking depends on the shell emitting
the supported OSC sequences.

Run `zig-out/bin/vex-native.exe` and `zig-out/bin/vex-native-tests.exe` on Windows,
keeping `ghostty-vt.dll`, `conpty.dll` and `OpenConsole.exe` beside them. The tests
also need the adjacent `vex-conpty-fixture.exe` produced by `test-build`. Copying `zig-out/bin` to a Windows directory avoids
UNC working-directory behavior in cmd. Windows does not need Zig or .NET to run
the app or the compiled tests. Linux compilation alone is not a Windows test run.

On Windows with the pinned Zig installed:

```powershell
zig build
zig build test
zig build run
```

## Shells and controls

```powershell
zig build run -- --shell pwsh
zig build run -- --shell cmd
zig build run -- --shell nu
zig build run -- --shell gitbash
zig build run -- --shell wsl
zig build run -- --program 'C:\Windows\System32\cmd.exe' --arguments /d
```

Shell selection, themes, font family, font size and cursor blinking are configurable in
Settings. Custom programs use an absolute executable path and a raw Windows
argument string. Available shells depend on the local Windows/WSL installation.

| Action | Keys |
| --- | --- |
| Command palette | Ctrl+Shift+P |
| New / close tab | Ctrl+Shift+T / Ctrl+Shift+W |
| Next / previous tab | Ctrl+Tab / Ctrl+Shift+Tab |
| Split right / down | Ctrl+Shift+R / Ctrl+Shift+D |
| Theme picker | Ctrl+Shift+M |
| Tab overview | Ctrl+Shift+Space |
| Path completion | Ctrl+Shift+F |

The tab overview uses terminal frames (including split panes), not window captures.
Use arrows or Tab/Shift+Tab to choose, Home/End to jump to the first/last tab, Enter or a
card click to switch, and Escape to return without switching. Each card has a close
button (except the final tab); split previews also have individual close buttons
when large enough for a usable target. Delete closes the selected tab;
Ctrl+Shift+W closes its active split pane (or the tab if it has no splits).
The final tab cannot be closed. Closing keeps the overview open and uses
the usual running-process confirmation, including when closing an inactive tab.
The centered grid shows all tabs together (the workspace limit is eight), with
uniform card sizes and a centered final row. Columns adapt to window proportions.
It prefers three-fifths of the available area, growing on smaller windows to keep
close controls and terminal content usable rather than hiding tabs on pages. Opening and
selection use a monitor-paced 180 ms scale transition, respecting Windows' animation preference.
PTY sizes stay unchanged, and previews share the normal glyph atlas and cached
geometry. Output from visible previews uses the existing output frame scheduler;
a settled overview has no animation loop.

Windows desktop verification: exercise different split layouts, Unicode/wide and
box-drawing glyphs, a continuously updating inactive tab, both themes, mixed DPI,
keyboard navigation with seven/eight tabs, cancellation, process exit, and disabled system animations. Check
that opening/closing the overview never emits terminal resizes. This is a local
interactive check, not covered by Linux cross-compilation.

## Rendering and motion

Dirty terminal snapshots still read every row to handle Ghostty's scroll dirty-state
under-reporting. Repeated non-grapheme cells share decoded text/style within each
row; the cache never crosses rows or snapshots. Grapheme clusters always use the
public UTF-8 decoder. Selection remains cell-local.

All cursor shapes, including inverted block cursors, draw over cached terminal
geometry, so blinking does not rebuild the text grid. Tab selection and hover use
short, interruptible ease-out transitions, paced to the monitor through the existing
high-resolution output scheduler. They do not resize the terminal or keep a render
loop running after settling. Windows' client-area animation preference is respected;
unfocused windows and tab-strip resizes snap immediately.

## Lua configuration

Vex runs without a configuration file. A missing file on first launch is normal:
no warning is shown and nothing is created until you save settings.

Configuration lives at `%LOCALAPPDATA%\Vex\init.lua`. Set `VEX_CONFIG` to an
absolute path to use another file. This replaces the native `settings.json`
format; JSON configuration is no longer read.

```lua
-- init.lua: vex.config starts with the defaults.
vex.config.theme = "Vex Obsidian" -- use a name from Theme Studio
vex.config.font_family = "Cascadia Mono"
vex.config.font_size = 14
vex.config.cursor_blink = true
vex.config.copy_on_select = true -- false: selecting text leaves the clipboard unchanged
vex.config.default_shell = "startup"

-- Ordinary Lua expressions, functions, loops and modules work.
-- require("appearance") -- loads appearance.lua beside init.lua
```

Use the current theme names shown in Theme Studio (see
`src/workspace/terminal_theme.zig`). Font size must be an integer from 9 to 32, cursor blink a
boolean, `copy_on_select` a boolean (also available in Settings → General), and shell one of `startup`, `cmd`, `powershell`, `pwsh`, `nu`, `gitbash`,
`wsl`, or a saved custom profile reference such as `custom:256`. `startup` respects the CLI launch choice. Shell changes affect new tabs and
splits, not existing processes. Unknown settings and invalid values are errors.

Settings written by the UI are saved atomically to `init.lua`
(or the file selected by `VEX_CONFIG`), replacing its contents.

Select **Configuration: Reload init.lua** in the command palette
(`Ctrl+Shift+P`) to reload without restarting. A configuration error keeps the
previous settings; at startup it uses defaults instead. A nonblocking, themed
notification reports Lua source/line diagnostics. Files are never rewritten to
repair errors. There is no per-frame file polling or background Lua execution.
Unchanged reload results do not rebuild fonts or reapply themes; unchanged GUI
preferences do not write to disk.

Lua 5.4.8 is statically linked. The VM is created only when a file exists, runs
once per load and is destroyed before applying preferences. The bridge limits
source files to 64 KiB, VM allocations to 8 MiB and Lua execution to roughly one
million instructions per configuration evaluation (including its modules). Base, table, string, math, UTF-8 and Lua `require`
are available. Process/IO/debug/coroutine APIs, native module loading, dynamic
loaders, protected calls and `__gc` finalizers are deliberately unavailable. Configuration is
local user code, not an untrusted-code security sandbox. Rex's keybinding,
callback and server APIs are not implemented by this configuration migration.

### Custom shells

Open **Settings → General → Add custom shell…**. The shell picker only launches
profiles; it does not contain an add action. Enter a name, select an executable
with **Browse…**, or enter a command available on `PATH` (for example `fish.exe`).
Keep arguments in the separate field; executable paths with spaces do not need
quotes. Vex passes arguments directly to that program and does not guess startup
flags or modify the shell's user profile.

For a Linux shell in the default WSL distribution, enter a program such as
`wsl:/usr/bin/fish` and arguments such as `-l`. This uses the WSL backend, including
its directory handling, instead of treating the Linux path as a Windows program.
WSL checks whether the Linux executable exists when the session starts.

Saved profiles appear in the picker and the default-shell dropdown. IDs and
default selections survive restarts; profiles are not compiled-in shell names.
Names, program lookup, argument lengths and duplicate names are validated before
saving. Errors remain in the editor without discarding its fields. Missing
programs at launch report an error without terminating existing sessions; an
unavailable custom startup default falls back to the system shell.

Close protection trusts the selected profile's root as a shell, not an executable
name allowlist. Startup arguments alone do not indicate live work: child processes,
OSC 133 command activity and explicit OSC 7501 application activity supply that
information. Nested Windows shells matching a live profile are recognized by
executable file identity, not just their filename. An unfamiliar nested process
without integration remains protected as an application; Windows has no universal
interactive-shell flag. WSL work detection relies on terminal integration because
the Windows process tree cannot enumerate Linux applications.

Profiles are saved atomically to `init.shells.lua` beside `init.lua` (or
`<name>.shells.lua` for `VEX_CONFIG`). Settings manages this file separately from
hand-written `init.lua`; adding a profile never rewrites user Lua. The format is
bounded Lua, for example:

```lua
vex.add_shell(256, "My shell", "C:\\\\Tools\\\\shell.exe", "--interactive")
```

The configuration reload action also reloads custom profiles, retaining the last
valid catalog on failure. Settings rereads the profile file before appending, so
external profile changes are preserved. The managed file is regenerated by
Settings; comments and Lua expressions in that file are not preserved. Existing sessions retain their own launch
metadata. The native dialog budget permits 56 custom profiles; profile source is
limited to 64 KiB, with the same Lua execution/memory limits as preferences.

## CI and packages

- **Native Zig**: Linux cross-builds Debug and ReleaseSafe, checks Zig formatting
  and portable packaging, then Windows executes both compiled Zig test suites,
  including headless child-process ConPTY erase/scrollback regressions.
- **Native packages**: runs the same checks before packaging ReleaseSafe into a
  ZIP and MSI. WiX/.NET are installed only on the Windows packaging runner.
- **Legacy WPF** and **Web** have separate path-filtered workflows. Changes to
  native code do not require a WPF build.

To create a **test package from a branch**, without merging to main:

```sh
gh workflow run release.yml --ref feat/zig-native -f mode=preview
```

Download ZIP, MSI, checksums and `build-info.json` from that run's Actions artifacts
(retained for 14 days). Preview filenames include the source commit. No tag,
GitHub Release, "Latest" update or main-branch commit is created. Preview MSI uses
the stable install location; use an isolated Windows machine for installation tests.

Stable releases require a `vX.Y.Z` tag matching `native/VERSION`. Pushing the tag
runs the tested native release pipeline, not the old WPF packager. A failed stable
release can be retried manually:

```sh
gh workflow run release.yml --ref main -f mode=release -f tag=v1.7.0
```

The selected workflow revision must contain the native pipeline. Existing tags
are resolved to exact commits; the branch tip cannot silently replace tag content.

For an MSI build on a Windows machine with Python, WiX's .NET SDK and either Zig
or prebuilt output:

```powershell
./native/build-installer.ps1
# No Zig needed when using the already tested cross-build output:
./native/build-installer.ps1 -SkipBuild -SkipTests
```

The packager validates x64 PE headers, ZIP contents, both checksums and every file
extracted from the real MSI cabinet. Administrative extraction does not register
or install the application. Artifacts land in `native/publish/`.

Desktop smoke tests and performance tools are maintained locally outside the
repository; hosted CI does not claim GPU rendering or interactive desktop coverage.
Generated icon geometry and shader bytecode are checked in. Licensing and icon
source attribution are retained in `assets/icons/README.md` and the root notices.
