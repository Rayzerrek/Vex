# Changelog

## 2.1.1

- Make recursive path picking breadth-first, publish partial results, cache recent folders, and filter extended queries incrementally.
- Keep the selected path stable as partial and final indexes arrive.
- Skip dependency, VCS and WSL system trees while keeping their directories available for explicit navigation.
- Follow the WSL shell's current directory without requiring OSC 7 and harden path decoding, navigation and shell quoting.
- Open Linux directory symlinks on Windows 10 through on-demand WSL resolution without probing every link during indexing.
- Improve shell-handoff and orphaned-command detection.

## 2.1.0

- Display direct Kitty Graphics RGB/RGBA/PNG images with cropping, scaling, transparency, z-order and scrolling.
- Reuse generation-cached GPU textures and batch consecutive placements of the same image.
- Deliver Kitty protocol replies correctly through ConPTY using literal character records.
- Bound image storage and transfers, isolate image uploads from text buffers, and retain in-flight GPU resources safely.
- Keep filesystem/shared-memory transfers, animation and Unicode placeholders outside the initial support scope.
- Reduce application icon detection latency.
- Record local hardware benchmarks and add Windows image, PNG, transport and offscreen GPU regression coverage.

## 2.0.3

- Automatically dismiss the up-to-date notification after five seconds without hiding pending updates.
- Add Windows desktop tests for notification expiry and repeated update checks.

## 2.0.1

- Inline tab renaming with UTF-8 cursor navigation, word deletion, clipboard shortcuts and automatic-title reset.
- Settings grouped into labeled cards, with shared compact layout and inline font-size validation.
- Reuse tab rename text layouts and avoid redundant settings-page refreshes and invisible exit animations.
- Preserve tab names when clipboard writes fail; reject control characters in pasted names.
- Improve application icon contrast and soften TUI highlights across theme changes.
- Clear terminal selection after copying.

## 2.0.0

- Rewritten in Zig/Win32 with ConPTY, libghostty-vt and D3D12; no .NET runtime.
- Lua preferences and custom Windows/WSL shells, with atomic saves and backups.
- Live tab overview, adaptive appearance, copy feedback and pane-local recovery.
- Linux/WSL cross-builds, Windows tests and verified ZIP/MSI releases.
- **Breaking:** WPF JSON settings and sessions are not imported. Project grouping,
  saved layout templates and session restoration are not available.
  See [migration notes](docs/releases/2.0.0.md).

## 1.7.0 — legacy WPF

- Per-project WSL shell defaults and compact installed-shell pickers.
- Rounded overlays/popups with refined UI surfaces.
- Redesigned website and static documentation.

## 1.6.2 — legacy WPF

- Preserved block-graphic colors and removed seams at fractional DPI.
- Moved program status badges to the right side of tab headers.

## 1.6.1 — legacy WPF

- Softer window frame and quieter inactive tabs, with gradients limited to active tabs.

## 1.6.0 — legacy WPF

- Refined tabs, dividers and Windows 11 window corners.
- Clearer titles, more reliable icons and foreground editor process detection.
- Improved WSL paths, shell detection and dedicated artwork.
- Clipboard feedback for terminal applications, including OSC 52.

## 1.5.0 — legacy WPF

- Saved pane layouts, startup commands and OSC 7501 program status.
- Application icons across panes and refined workspace controls/motion.
- Faster startup and more responsive input during heavy output.
- Path-completion teardown fixes and preserved nested release payload paths.

## 1.4.0 — legacy WPF

- Sharper fonts, fuzzy path picker, safer closing and configurable copy-on-select.
- Project picker and improved workspace shortcuts.
- Startup, terminal rendering, session teardown, clipboard and DPI fixes.
- Built-in editor/file tree removed; legacy editor panes migrated to terminal panes.

Historical WPF releases use `Vex-<version>-win-x64` packages and `Vex.App.exe`.
Native packages use `Vex-Native-<version>-win-x64` and `Vex.exe`.
Full release history: <https://github.com/Rayzerrek/Vex/releases>.
