# Native rewrite contract

Reference implementation: `../windows/`, especially `docs/architecture.md`,
`docs/configuration.md`, `docs/shortcuts.md`, and its tests. Work takes place on
`feat/zig-native`. The WPF application stays runnable and unchanged until native
functional and visual parity is demonstrated. This is a staged rewrite, not a
claim that a reduced-feature terminal replaces Vex already.

## Safety rules

- Zig 0.15.2; Debug tests and ReleaseSafe application builds. No ReleaseFast,
  ReleaseSmall, `@setRuntimeSafety(false)`, `unreachable` for external input,
  unchecked optional unwraps, or packed Ghostty cell-layout decoding.
- Explicit allocators; each terminal owns its handles and snapshot buffers.
  Constructors roll back with `errdefer`; owners destroy resources once.
  Constructors and domain-tree/tab edits either complete or leave the prior
  workspace intact. A terminal snapshot/native IO fault fails closed at the
  window boundary; it never returns a partial snapshot as successful output.
- Only FFI modules handle C pointers. A checked symbol from the pinned DLL and
  trusted callback userdata still require pointer conversion: Zig is **not** a
  memory-safe language and Win32/libghostty cannot be made pointer-free.
- Main-thread workspace and emulator ownership; pipe workers never access Zig
  objects. Queued messages identify sessions by non-reused tokens, never a
  pointer into a closed/freed pane. Stop workers before freeing their buffers.
- Bounded input queues, validated dimensions/UTF-8, explicit errors at IO seams.
  Never silently discard an overflowing input queue or a broken VT snapshot.
- Closing a session must drain ConPTY and terminate its child process tree.
  Tests include active-output teardown and switching/closing background panes.
- Retain cached terminal snapshots on clean frames; hidden tabs still parse
  output but do not walk/draw their viewports. Optimize only measured paths.

## Zig quality reference

Use [Ghostty](https://github.com/ghostty-org/ghostty) as the primary reference for
idiomatic Zig, not as a reason to copy optimizations or weaken runtime checks.
Two inspected examples:

- [`src/lib/allocator.zig`](https://github.com/ghostty-org/ghostty/blob/9e080c5a/src/lib/allocator.zig): explicit allocator interfaces, pointer-stable C adapters and leak-checking test allocators.
- [`src/tripwire.zig`](https://github.com/ghostty-org/ghostty/blob/2ac3c1f1/src/tripwire.zig): exercise failure paths to verify `errdefer`, not merely happy-path behavior. Native currently injects Zig allocation failures; Win32 failure points remain follow-up work.

Keep ownership contracts local, tests beside the module and abstractions tied to
an actual requirement. Reuse standard-library helpers before adding a framework.
Name our modules/types by their role (`terminal.zig`, `api.zig`, `Terminal`, `Api`),
not the dependency brand. Preserve upstream names only for real C ABI bindings,
vendored sources and DLL filenames; do not add aliases just to hide C type names.

## Parity ledger

| Area | Reference | Native milestone / remaining evidence |
| --- | --- | --- |
| Project -> tabs -> split tree, active leaf | `Model/Project.cs`, `WorkspaceTab.cs`, `PaneNode.cs` | First milestone: one project, up to 8 tabs, recursive splits, focus, close; multiple projects and drag/reorder still pending |
| Split orientation/ratio | `SplitPaneView.xaml.cs` | Right/down at 0.5; ratio clamping 0.1..0.9 in model; interactive dividers pending; split directories use the last valid local OSC 7 report, otherwise the pane's launch directory |
| Workspace shortcuts | `MainWindow.Keyboard.cs`, `docs/shortcuts.md` | T/R/D/W, Ctrl+Tab, Ctrl+Shift+Tab first; all others pending |
| Process lifetime / ConPTY | `Vex.Terminal/TerminalSession.cs`, `ProcessTree.cs` | Per-pane ownership, bounded queues, process-exit observer and job-object teardown; auto-close/close confirmations and further cancellation/Win32 failure injection pending |
| Terminal core / feed filtering | `Vex.Libghostty/*`, `FeedFilterTests` | Same DLL, typed public API; UTF-8, color, wide/grapheme, modes, responses, snapshot cache; ConPTY DCS filter parity pending |
| Renderer / glyphs / DPI / caret | `Terminal/Native/*Rendering*`, `*Glyphs*`, `*Layout*` | DirectWrite baseline and themed chrome; pixel fixtures, custom box/Powerline glyphs, color emoji and blink pending |
| Input / mouse / selection / clipboard | `*Input*`, `*Selection*`, `*Clipboard*`, `*Mouse*` | Basic keys/plain paste/wheel; full protocols, IME, click positioning, selection/copy/cut, bracketed paste pending |
| Shell profiles and integration | `Model/Shell*`, `ShellIntegration/*` | cmd.exe only; detected/custom shells and integration pending |
| Windows/WSL projects and directories | `Model/Workspace.cs`, `Model/Wsl*`, `MainWindow.Projects.cs` | Local directory ownership/OSC 7/inheritance implemented; project UI/WSL pending; WSL directories explicitly reject Windows-shell launch |
| Settings and session persistence | `Model/AppSettings.cs`, `Session*`, `VexJsonContext.cs` | Pending; native currently does not read/write the production profile |
| Saved layouts | `SavedTerminalLayout.cs`, `MainWindow.SavedLayouts.cs` | Pending; opening explicitly executes commands once; restore must never replay commands |
| Themes/appearance/font editor | `ChromePalette.cs`, `Model/TerminalTheme.cs`, settings overlay | Vex Dark/default fonts first; all remaining palettes/settings pending |
| Palette, tab peek, overlays/popups | `CommandPalette*`, `TabPeek*`, `MainWindow.Overlays.cs` | Pending |
| Status, attention, icons, titles | `ProgramStatusTracker.cs`, `ProgramStatusPresentation.cs`, `AppIcon*` | OSC 7501 records/capability replies, pane/tab priority badges, progress and acknowledgement implemented; OSC 9;4/133 fallback, tooltips/origin routing, project aggregation and shell icons pending; raw terminal titles must not become trusted UI controls |
| Links/path completion/search | `*Links*`, `*PathCompletion*` | Pending |
| UI Automation / accessibility / installer / crash log | Windows UI and packaging | Pending |

The reference keeps the last tab and caps each project at 8 tabs. Splitting
selects the new pane; closing the active split selects the first remaining
leaf; closing an active tab selects its left neighbor, or the right one for
the first tab. Plain Ctrl chords belong to the terminal, except Ctrl+Tab.
These are behavioral contracts, not opportunities to redesign the app.

## Completion gates

1. Native behavior fixtures cover the corresponding Windows tests (not mocks
   that merely assert implementation calls). Allocation-failure tests cover
   transactional workspace mutations and all owned Zig buffers.
2. Windows desktop tests exercise distinct live shells in tabs and panes,
   background output, focus, resize, close races, active-output shutdown and
   malformed/oversized input. Screenshots are inspected, not equated to visual
   parity simply because a PNG exists.
3. Production-profile compatibility and crash-safe persistence are validated
   using isolated copies, never by overwriting the user's WPF session.
4. Matched cold/warm startup, frame-time/input-to-present, process/child/GPU
   memory benchmarks show the improvement. A smoke startup number is not this
   benchmark, and low idle CPU with blink missing is not a parity result.
5. Before replacing `windows/`: complete every ledger row, renderer pixel
   comparisons, physical keyboard/IME/accessibility checks, and packaging.

## Milestone 1 verification

- Build: Zig 0.15.2, default ReleaseSafe; Debug tests; C++ warnings are errors.
- Real-DLL tests: ABI, Unicode/wide/long combining cells, palette changes,
  alternate screen, scrollback, keyboard modes, replies and clean-frame reuse.
- Domain tests: nested geometry, transactional allocation failures,
  duplicate/stale IDs, bounded depth, eight tabs and WPF close/focus fallbacks.
- Windows desktop smoke: three live panes, independent tabs, background output,
  stale output tokens, process-tree teardown and flooding resize/close.
- Artifacts: `zig-out/verification/{result.json,native-window.png,native-workspace.png}`.

## Directory checkpoint verification

- Each session owns a parsed launch path and the last valid local OSC 7 path.
- PWD callbacks parse each report synchronously, including several reports in
  one read. Invalid/remote/empty reports do not erase the previous valid path.
- Splits clone the focused pane's directory; new tabs retain the project root.
- URI percent/Unicode decoding, local-host matching, control/relative/WSL
  rejection and allocation/callback rollback have pure/real-DLL fixtures.
- Windows smoke verifies actual `%CD%` in a split under a Unicode/spaced folder,
  and verifies a new tab still launches in the project root.
- cmd does not emit OSC 7 by default; shell integration remains pending. Do not
  infer a split directory from prompt text (the WPF split path does not do so).

## OSC 7501 checkpoint verification

- Separate from OSC 7 directory reporting; status owns no directory state.
- Bounded per-terminal hierarchy: 256 records, 4,096-byte framed reports,
  128-byte/eight-level IDs, 32-byte app IDs, 192-byte titles and 2,048-byte messages.
- WPF fixtures port replacement, duplicate-field rules, inherited app names,
  subtree clearing, priority/recency, result acknowledgement, eviction and
  strict Base64/UTF-8/control/format-character handling.
- The streaming filter preserves unrelated VT and shields opaque string payloads.
  Replies are ordered against real-DLL cursor queries; each chunk boundary,
  BEL/ST framing limit and oversized/repeated-ESC recovery is tested.
- Allocation failure injection covers replacement/insert and failures at the
  record cap, with previous records retained. No native allocator failure
  injection or native memory-safety certification is implied.
- Real ConPTY smoke covers isolated panes, tab priority, blocked/result input
  semantics, hidden-status repaint and unchanged zero-paint hidden text output.
- Latest checkpoint: 41 tests in Debug and ReleaseSafe, ReleaseSafe application
  build and desktop smoke pass. The captured status screenshot in this session
  is black; these checks do not establish visual parity.
- WPF files are unchanged. Release solution build has zero warnings/errors and
  all 139 libghostty tests pass. The application suite passes 533/536 tests;
  the two clipboard tests still fail in isolation (clipboard lock/notification
  unavailable), while the animation test passes on isolated rerun. The suite
  therefore cannot be reported fully passing in this desktop session.
- OSC 9;4 progress fallback and OSC 133 shell lifecycle markers deliberately
  remain for the shell-integration checkpoint. Full tooltip/message presentation,
  project aggregation and attention-origin navigation remain pending.

No WPF profile is read/overwritten. This milestone does not replace production,
certify native memory safety, or establish complete parity/performance.
