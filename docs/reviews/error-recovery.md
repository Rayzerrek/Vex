# Native error recovery and WSL teardown

## Changes

- Explicit recovery policy for rejected input, clipboard contention, failed shell
  launches and invalid dialog/input events. Nonmodal themed toasts are throttled
  on repeated errors; unknown errors, allocation failures and broken invariants
  remain fatal rather than continuing with potentially corrupt state.
- Clipboard IO failure no longer poisons the VT parser's sticky callback error.
  Later output and subsequent copies can still succeed.
- Input enqueues are atomic. A maximum 1 MiB clipboard paste fits with DEC 2004
  framing; protocol replies have 64 KiB reserved in the same FIFO. Overflow
  rejects the input instead of closing the workspace.
- Pipe errors and failed protocol replies quarantine only their pane. Its screen
  remains copyable/resizable. Native tokens are removed before teardown; late
  messages cannot reach a removed Zig owner. Natural-exit output is still read.
- Native teardown runs on up to four reapers, not the window thread. Synchronous
  pipe cancellation handles the cancel-before-IO race. A one-second watchdog can
  terminate a stuck client job; callback cancellation completes before handles
  are freed. Launches apply backpressure at a backlog of 16 detached sessions;
  existing panes can always close. Shutdown drains and joins reapers.
- Main clients and WSL probes enter jobs atomically at creation, without a
  suspended-launcher interval. WSL metadata probes are optional, demand-driven,
  startup-delayed, serialized across panes and backed off after failure. Closing
  a pane signals its probe immediately, even before its reaper runs.
- GUI preferences remain in init.lua, with the exact previous source saved
  atomically to init.backup.lua before replacement. This is one rotating backup,
  not history; unchanged saves preserve it. Save failure rolls live preferences
  back; malformed reloads retain the last valid preferences. Modules are untouched.
- Copy feedback captures occupied extents once per copy and reuses the buffers.
  Blank selected rows and each line's trailing padding are excluded. Adjacent
  equal spans merge while rendering; there is no 64-row truncation or per-frame
  text scan/allocation.

## Verification

Pinned Zig 0.15.2; Windows 10 x64 build 19045; Windows binaries copied to a local
Windows filesystem and executed through WSL interoperability:

- Formatting, Debug and ReleaseSafe application/test builds.
- **225 Windows tests passed in each configuration**, with actual Ghostty/ConPTY
  DLLs and the fixture. Includes oversized/max-size paste and reserved replies,
  pane isolation, failed reply/theme notifications, late output/events, malformed
  reload/save rollback, exact Lua backups and real-DLL blank-line selection spans.
- Portable regression tests and **10 Python packaging tests** passed.
- ReleaseSafe ZIP generation and payload/checksum verification passed.
- `tools/test-recovery.ps1`, ReleaseSafe, normal GPU selection:
  - WSL/NixOS: **60 cycles × 7 tabs** (420 measured open/close pairs), handles
    **464 → 469** after warmup; no normal forced closes or visible WSL error dialogs.
  - cmd: **30 × 7**, handles **435 → 441**; no normal forced closes.
- ReleaseSafe WARP: WSL/NixOS **30 × 5**, handles **333 → 336**; no normal forced closes.
- Desktop runs also verify the error toast, duplicate/stale transport errors,
  renderer recovery and a deliberately delayed close. The delayed close keeps
  the UI responsive and triggers exactly one watchdog cleanup.
- Debug WARP cmd recovery smoke passed; `git diff --check` passed.

## Scope and test corrections

The first desktop detector was too broad: normal hidden console windows can
contain wsl.exe in their title. The final test checks visible standard error
dialogs and captures their owner command line on failure; it never dismisses
such dialogs. It respects the eight-tab limit and warms full rendered scenes,
all swap-chain buffers, toast geometry and teardown workers before comparing
handle counts. Both snapshots explicitly settle renderer work with cursor blink
disabled; otherwise deferred GPU/driver cleanup could cause a false leak warning. A small post-warmup handle allowance accounts for bounded driver
and worker caches; counts are not a proof of every native allocation's lifetime.

Final review added a reproducing late-output regression: a failed protocol reply
after the exit watcher had already marked a retained pane as exited could leave
its callback error sticky. The new test failed before the fix; transport cleanup
now also detaches exited panes while preserving their actual process exit code.

A stronger early stress run exposed a real UI stall in synchronous ConPTY close;
that drove the asynchronous teardown/watchdog change. No process-wide Windows
error-dialog suppression was introduced. The user's intermittent 0xc0000142 is
not claimed to have a uniquely proven root cause or to be impossible on every
Windows/WSL version. The final local stress runs did not reproduce it. Mixed-DPI,
shell/profile/version matrices, exhaustive GPU-driver failures and pixel-level
copy-animation appearance remain separate desktop validation.
