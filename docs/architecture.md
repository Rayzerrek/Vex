# Architecture

Vex is a native Windows terminal workspace. It is a WPF shell around a ConPTY
process, with Ghostty's terminal core doing the VT emulation.

## Projects

```text
windows/
  Vex.App/           WPF shell: window, tabs, splits,
                     overlays. Owns all UI state.
  Vex.Terminal/      ConPTY process lifecycle: spawn, resize, resize-safe
                     teardown, process tree cleanup.
  Vex.Libghostty/    P/Invoke wrapper over the vendored libghostty-vt, plus the
                     grid model the renderer reads.
  Vex.App.Tests/     Unit tests for the shell's pure logic.
  Vex.Libghostty.Tests/  Tests against the real ghostty-vt.dll.
  Vex.Setup/         WiX MSI packaging.
Vendor/libghostty/   ghostty-vt.dll, checked in and redistributed unmodified.
web/                 Landing page (React, TanStack Router, Tailwind, Vite+).
```

The dependency direction is one-way:

```text
Vex.App ──> Vex.Terminal
        └─> Vex.Libghostty ──> ghostty-vt.dll
```

Neither `Vex.Terminal` nor `Vex.Libghostty` references WPF. `Vex.Libghostty`
targets `net10.0` (not `-windows`) precisely so its tests and the emulator
wrapper stay free of UI dependencies.

## How a byte gets to the screen

```text
shell process
   │  writes to the ConPTY output pipe
   ▼
Vex.Terminal        reads the pipe on a background thread
   │
   ▼
Vex.Libghostty      Feed() parses the VT stream, marks dirty rows, raises
   │                events for the title, bell, and PTY-bound responses
   ▼
NativeTerminalControl   pulls frame rows, builds GlyphRuns, draws them
   │
   ▼
WPF visual tree     OnRender into the terminal surface
```

Two details worth knowing before changing this path:

- **Dirty rows.** The emulator marks which rows changed. The control re-renders
  only those and keeps a per-row hash to skip untouched work, so a full-screen
  repaint is not the default.
- **Responses.** Some VT queries (device attributes, XTGETTCAP) must be answered
  by writing back to the PTY. ConPTY strips the DCS wrapper from those queries,
  so only the middle reaches the emulator. The feed filter in `Vex.Libghostty`
  reconstructs them; see `FeedFilterTests` for the cases it covers.

## State and persistence

State lives in `%LocalAppData%\Vex\`:

| File | Contents |
|---|---|
| `settings.json` | Appearance, theme, font, cursor, shell profiles. |
| `session.json` | Projects, tabs, split tree, divider ratios, focus. |
| `crash.log` | Latest managed fatal exception and stack trace; best-effort, local only. |

Both are written by source-generated `System.Text.Json` contexts
(`VexJsonContext`), not reflection. Writes are debounced through
`HalfDebouncer` — leading edge fires immediately, the trailing edge coalesces
further edits — and flushed on window close so the last change is never lost.

Session snapshots are repaired before shell prewarm inspects them: null list
entries are skipped while preserving the selected project/tab, and missing lists
or pane leaves fall back to fresh terminals. Settings normalize custom shell
profiles before the UI uses them; only the loaded singleton may schedule writes,
so deserialization does not start save timers for partially populated objects.

Each unattached prewarm session retains at most 1 MiB of pooled buffer capacity.
It transfers buffer ownership without copying. Overflow invalidates the entire
VT stream and disables prewarm for that directory/shell until the configuration
changes (or the app restarts); panes cold-start instead of replaying truncated
output. Closing an invalidated ConPTY session happens off the reader thread and
outside its output lock. A pane subscribes to process exit only after accepting
the prewarm stream. Cached exit codes preserve very fast process exits, while
queued callbacks from replaced sessions are ignored on the UI thread.

Fatal exception handlers do no normal-startup I/O and do not mark exceptions as
handled. They synchronously overwrite a bounded `crash.log` on the first managed
fatal exception. Native crashes and severe resource exhaustion may not produce
a report; disk failures must never replace the original exception. Reports may
contain local paths from stack traces; they are not uploaded.

`AppSettings` is a singleton loaded on a background thread during startup so
the UI thread does not block on disk I/O. A legacy `Shell` name is migrated
into `ShellId` on load; the migration is a no-op when the file already has a
`ShellId` key.

## Threading

- The UI thread owns all WPF objects and most model state.
- PTY reads happen on a thread-pool thread; bytes enter the emulator under the
  terminal's lock, and the control marshals frame updates to the UI thread.
- `HalfDebouncer` deliberately uses `System.Threading.Timer` rather than
  `DispatcherTimer`: it has no thread affinity, so settings writes never
  require a UI dispatch. Serialization is marshalled back to the UI thread
  when the timer fires, because `CustomShells` is UI-bound.
