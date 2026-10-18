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
-- init.lua: vex.config starts with the defaults (and any saved GUI preferences).
vex.config.theme = "Vex Obsidian" -- use a name from Theme Studio
vex.config.font_family = "Cascadia Mono"
vex.config.font_size = 14
vex.config.cursor_blink = true
vex.config.default_shell = "startup"

-- Ordinary Lua expressions, functions, loops and modules work.
-- require("appearance") -- loads appearance.lua beside init.lua
```

Use the current theme names shown in Theme Studio (see
`src/workspace/terminal_theme.zig`). Font size must be an integer from 9 to 32, cursor blink a
boolean, and shell one of `startup`, `cmd`, `powershell`, `pwsh`, `nu`, `gitbash`,
`wsl`. `startup` respects the CLI launch choice. Shell changes affect new tabs and
splits, not existing processes. Unknown settings and invalid values are errors.

Settings written by the UI are saved atomically to `init.gui.lua` beside `init.lua`
(or `<name>.gui.lua` for a custom configuration path).
Vex applies that GUI file first, then your `init.lua`, so explicit script settings win
on the next load. UI edits apply immediately, but script-owned values revert to
the script on reload. Saving in Settings never destroys your code or comments.
To configure a value exclusively through the UI, leave it out of `init.lua`.

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
