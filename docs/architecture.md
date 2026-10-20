# Architecture

Vex is a native Windows terminal workspace written in Zig and C/C++. Win32 owns
the window, ConPTY runs shell sessions, libghostty-vt interprets terminal output,
and D3D12/Direct2D render the workspace. There is no WPF or CLR runtime dependency.

## Repository layout

```text
app/
  build.zig              Application and test build graph
  .zig-version           Pinned compiler version
  VERSION                Release version
  src/
    zig/
      main.zig           Entry point and embedded-test imports
      app/               Lifecycle, sessions, input, painting and settings
      shell/             Profiles, launch arguments, WSL and shell integration
      terminal/          Ghostty bindings, state, feed filtering and identities
      workspace/         Tabs, panes, overlays, themes and interaction models
      windows/           Zig adapters for the Win32 backend
    windows/
      platform/          Shared C ABI, COM ownership and system services
      window/            Window lifecycle, chrome, capture and scrolling
      dialogs/           Dialogs, controls, painting and notifications
      renderer/          GPU renderers, shaders and generated icon geometry
      pty/               ConPTY lifecycle and process probes
    lua/                 Bounded configuration bridge
  assets/                Application icon and licensed icon sources
  tests/                 Python packaging tests and C++ test fixtures
  tools/                 ZIP/MSI packaging and shader generation
  packages/windows/      WiX project and MSI manifest
  vendor/                Ghostty/ConPTY binaries, Ghostty headers and Lua source
web/                     Website
docs/                    Guides, historical releases and review reports
.github/workflows/       Application, website and release CI
```

Build output stays in `app/zig-out/` and `app/.zig-cache/`; staged packages and
release artifacts stay in `app/publish/`. These are ignored, not source folders.
The removed WPF implementation remains available in Git history. Historical
release notes and reviews may describe that implementation.

## Runtime responsibilities

The Zig application layer coordinates terminal sessions and workspace state.
Workspace modules model tabs, pane trees and UI interactions. Shell modules
construct launch commands and handle Windows/WSL paths and custom profiles.
Terminal modules own emulator state and interpret status, titles and output.

`src/zig/windows/` adapts those models to the native Windows C ABI in
`src/windows/platform/windows_platform.h`. The C++ backend handles OS resources,
ConPTY, dialogs and rendering. The C Lua bridge evaluates configuration against
the statically linked vendored runtime.

## Output and rendering

```text
Shell process → ConPTY output → terminal feed / libghostty-vt
             → terminal snapshot → cached geometry / glyph atlas → GPU
```

Terminal responses travel back to the PTY. The feed layer handles ConPTY's
transport quirks; terminal tests exercise parser and state behavior. Dirty
snapshots still read every row because Ghostty's scroll dirty-state can
under-report changes. Cursor drawing and workspace transitions reuse cached
terminal geometry rather than rebuilding the text grid for every animation.

## Configuration and dependencies

Settings live in `%LOCALAPPDATA%\Vex\init.lua`, or the absolute path selected
by `VEX_CONFIG`. Managed custom profiles live in the adjacent shells Lua file.
Missing configuration is normal; failed reloads preserve valid settings. See
[configuration.md](configuration.md) for the current configuration reference.

`app/vendor/libghostty/` and `app/vendor/conpty/` contain checked-in Windows
runtime binaries. `app/vendor/include/` supplies Ghostty headers and
`app/vendor/lua/` supplies Lua sources. Keep provenance and licenses with these
files; redistribution notices are in the root
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
