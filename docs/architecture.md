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
under-reporting. Within one snapshot, rows reported as unstyled share decoded
non-grapheme cells; styled rows retain page-local cell and style-ID caches.
Style IDs never cross rows or snapshots. Background colors still come from the
public API per decoded cell because equal style IDs can carry different content
backgrounds; inverse and faint colors are applied only after that lookup.
The shared cache is discarded between snapshots, including palette changes.
Cursor drawing and workspace transitions reuse cached geometry.
One D3D12 device and command queue serve the application's window, including
terminal panes, UI and capture. All terminal panes share one bounded 1024×1024
glyph atlas; UI fonts use a separate atlas and share the terminal pipeline state.
Opening a pane does not create a device, queue, pipeline or atlas for that pane.
Only active workspace panes and explicitly requested previews materialize frames.
Terminal text atlas entries use DirectWrite ink bounds independently of cell
advances, preserving italic and combining-mark overhangs. All four font styles
share a baseline and line height measured across their faces. Private-use font
icons are fitted to their allocated cells; procedural box/Powerline graphics
retain exact cell edges. Backgrounds precede text so adjacent cells cannot erase
overhanging ink.

ASCII programming ligatures use the selected font's OpenType substitution inputs
and DirectWrite's standard/contextual ligatures and contextual alternates.
Eligible input/punctuation spans share the glyph atlas; ordinary text keeps its
single-cell cache. Runs stop at row, style, selection and cursor-cell boundaries.
Cursor position participates in the geometry key, while blinking and cursor shape
do not. The regular face defines cell width; wider synthetic faces fit their
advances to that grid. Long spans use pixel-aligned atlas tiles and a bounded
eight-entry DirectWrite layout cache, cleared when fonts change.

After the prepared startup frame, hidden, minimized and DWM-cloaked windows stop
painting and cancel pending output, synchronized-output and animation frames.
PTY output continues to update emulator state. Show/resize events and the existing
250 ms maintenance tick detect visibility changes; resuming invalidates the window
to paint current state. Covering the window with another window does not itself
suspend rendering.
Maintenance retires completed image transfers even without a subsequent frame.
Textures unused for over two seconds leave the GPU image cache; in-flight frame
slots retain their own references until their fences complete. Upload pages unused
for two seconds shrink to a small warm reserve while visible, or to zero while
suspended. The shared glyph atlases stay warm for wakeup and new panes.

Terminal geometry uses pane-local coordinates; pane movement and fractional scrolling
replay cached geometry with a translation. Dragging never resizes a PTY;
committing a pane move resizes it to the final grid once while its bounds settle.
Wheel scrolling retains fractional-row targets and retargets a 100 ms transition.
Snapshots change only at integer row boundaries; a cached extra row fills the
exposed edge. Typing, selection, resize and external viewport changes cancel stale
scroll targets. TUIs with mouse tracking receive their original wheel events.

## Dependencies and output

Runtime binaries, Ghostty headers and Lua sources live in `app/vendor/`.
Keep provenance and licenses with updates; see
[third-party notices](../THIRD-PARTY-NOTICES.md).

Build output (`app/zig-out/`, `app/.zig-cache/`) and packaging staging
(`app/publish/`) are ignored, not source directories.
See [configuration](configuration.md) and [testing](testing.md) for operational details.
