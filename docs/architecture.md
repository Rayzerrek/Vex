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

## Failure boundaries and teardown

Expected input, clipboard, shell-launch and dialog failures use nonmodal toasts;
corrupt invariants, allocation failures and unknown renderer/parser failures are
not silently ignored. Transport failure quarantines only its pane, preserving
its screen for copying. A full input queue rejects an enqueue atomically;
protocol replies have reserved space in the same FIFO.

PTY close removes the native token on the window thread before releasing Zig
owners. Detached pipe/probe workers carry only native storage. Up to four native
reapers perform ConPTY teardown outside the UI thread, with a one-second watchdog
that can terminate the client job if graceful disconnect stalls. New launches
apply backpressure while the teardown backlog is large. Application shutdown
joins reapers before allocator diagnostics. A cancellation loop handles the race
between cancelling synchronous IO and a pipe worker entering its next IO call.

WSL process metadata is advisory: queries run only on demand after a startup
delay, with one query at a time across panes and exponential failure backoff.
Closing a pane cancels its probe immediately. Main clients and probes enter jobs
atomically at process creation, without a suspended-launcher interval.

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
