# Architecture

Vex is a Windows terminal written in Zig and C/C++:

```text
Shell → ConPTY → feed filter / libghostty-vt → terminal snapshot → D3D12
```

Win32 owns the window; D3D12/Direct2D render terminal and workspace content.
The app has no WPF, CLR or Electron dependency.

## Source boundaries

| Path | Responsibility |
| --- | --- |
| `app/src/zig/app/` | Lifecycle, session ownership, input, settings and painting |
| `app/src/zig/workspace/` | Tabs, pane trees, overlays and themes |
| `app/src/zig/shell/` | Launch arguments, profiles, directories and WSL |
| `app/src/zig/terminal/` | Emulator state, feed filtering and output interpretation |
| `app/src/zig/windows/` | Adapters to the C ABI in `app/src/windows/platform/windows_platform.h` |
| `app/src/windows/` | Win32 resources, dialogs, rendering, ConPTY and process probes |
| `app/src/lua/` | Bounded configuration evaluation using statically linked Lua |

## Ownership and failure boundaries

Zig owns workspace and session state. Native pipe/probe workers do not borrow
Zig owners. Closing removes the native token before releasing the session;
ConPTY teardown runs off the UI thread and shutdown joins cleanup workers.

Expected input, clipboard, settings and launch failures use nonmodal toasts.
Transport failure isolates its pane and preserves its screen for copying.
Unknown failures, broken invariants and allocation failures are not silently
ignored. WSL process metadata is advisory; missing/stale data retains close
protection.

Foreground metadata uses one shared schedule for icons, titles and close-state
tracking. Visible idle shells poll once per second; foreground applications retain
200 ms polling. Submission, interruption, EOF and pane activation enable a bounded
50 ms burst, while ordinary typing and layout changes do not force scans. OSC
identity/status changes update presentation immediately without a process query.
WSL queries remain asynchronous, serialized across panes and failure-backed-off;
after input/activation their minimum interval is 200 ms, otherwise one second.
Completion posts a token-scoped window event so results do not wait for UI polling.
A pending query cannot enqueue duplicates, and late events for closed panes are
ignored. The startup delay still avoids spawning probes for short-lived WSL tabs.
These are scheduling intervals, not guaranteed end-to-end detection latencies.

Dirty snapshots read every row to compensate for Ghostty scroll dirty-state
under-reporting. Cursor drawing and workspace transitions reuse cached geometry.

## Dependencies and output

Runtime binaries, Ghostty headers and Lua sources live in `app/vendor/`.
Keep provenance and licenses with updates; see
[third-party notices](../THIRD-PARTY-NOTICES.md).

Build output (`app/zig-out/`, `app/.zig-cache/`) and packaging staging
(`app/publish/`) are ignored, not source directories.
See [configuration](configuration.md) and [testing](testing.md) for operational details.
