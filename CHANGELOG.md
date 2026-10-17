# Changelog

## Unreleased — native application

- Zig/Win32 terminal workspace with ConPTY, libghostty-vt and native D3D12 drawing.
- Cached terminal geometry and contrast correction; display-paced continuous output.
- Native ZIP/MSI packaging, independent of the legacy WPF application.
- Linux/WSL cross-builds, Windows Zig tests and branch-only Actions preview packages.
- Desktop test tools, benchmarks and implementation reports moved out of the repository.

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
