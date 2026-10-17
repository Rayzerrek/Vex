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
