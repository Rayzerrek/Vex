# Native rewrite contract

Reference implementation: `../windows/`, especially `docs/architecture.md`,
`docs/configuration.md`, `docs/shortcuts.md`, and its tests. Work takes place on
`feat/zig-native`. The WPF application stays runnable and unchanged until native
functional and visual parity is demonstrated. This is a staged rewrite, not a
claim that a reduced-feature terminal replaces Vex already.
Current platform scope is Windows + WSL; native Linux/macOS ports are not planned in this stage.

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
Inspected examples:

- [`src/lib/allocator.zig`](https://github.com/ghostty-org/ghostty/blob/9e080c5a/src/lib/allocator.zig): explicit allocator interfaces, pointer-stable C adapters and leak-checking test allocators.
- [`src/tripwire.zig`](https://github.com/ghostty-org/ghostty/blob/2ac3c1f1/src/tripwire.zig): exercise failure paths to verify `errdefer`, not merely happy-path behavior. Native currently injects Zig allocation failures; Win32 failure points remain follow-up work.

- [`src/main.zig`](https://github.com/ghostty-org/ghostty/blob/main/src/main.zig): thin entrypoint, explicit module imports and a root `test` block referencing the implementation. This current-main structural example targets a newer Zig; copy the organization principle, not its version-specific APIs.

Keep ownership contracts local, tests beside the module and abstractions tied to
an actual requirement. Reuse standard-library helpers before adding a framework.
Name our modules/types by their role (`terminal.zig`, `api.zig`, `Terminal`, `Api`),
not the dependency brand. Preserve upstream names only for real C ABI bindings,
vendored sources and DLL filenames; do not add aliases just to hide C type names.

## Parity ledger

| Area | Reference | Native milestone / remaining evidence |
| --- | --- | --- |
| Project -> tabs -> split tree, active leaf | `Model/Project.cs`, `WorkspaceTab.cs`, `PaneNode.cs` | One project, up to 8 tabs, recursive splits, focus, per-tab pane focus/zoom toggle, interactive pane header buttons (Focus/Zoom, Split Right, Split Down, Close), close and captured tab reorder; multiple projects, pane moves and tab ghost/push/scroll preview still pending |
| Split orientation/ratio | `SplitPaneView.xaml.cs` | Right/down at 0.5; ratio clamping 0.1..0.9; live captured divider dragging, 100-DIP interactive minimum and rollback implemented; final divider styling/physical multi-monitor DPI evidence pending; split directories use the last valid local OSC 7 report, otherwise the pane's launch directory |
| Workspace shortcuts | `MainWindow.Keyboard.cs`, `docs/shortcuts.md` | T/R/D/W, Ctrl+Tab, Ctrl+Shift+Tab first; all others pending |
| Process lifetime / ConPTY | `Vex.Terminal/TerminalSession.cs`, `ProcessTree.cs` | Per-pane ownership, bounded queues, process-exit observer and job-object teardown; automatic pane/tab exit close, final-tab retention, captured-gesture cancellation and stable-ID close confirmations implemented; Win32 failure injection pending |
| Terminal core / feed filtering | `Vex.Libghostty/*`, `FeedFilterTests` | Same DLL, typed public API; UTF-8, color, wide/grapheme, modes, responses, snapshot cache, ConPTY-stripped DCS filter (`+q` XTGETTCAP / `$qm` DECRQSS), disabled resize scrollback pull, DEC 2048 in-band resize reports (`\x1b[48;...t`), DEC 2026 synchronized output frame batching (100 ms starvation cap), default bar cursor and ConPTY-compatible key encoder options implemented |
| Renderer / glyphs / DPI / caret | `Terminal/Native/*Rendering*`, `*Glyphs*`, `*Layout*`, `*Scrollbar*` | D3D12 queue/fences + DXGI flip-discard presentation, native instanced cell drawing with a shared bounded R8 glyph atlas, Direct2D/DirectWrite UI via D3D11On12 (see `RENDERER.md`), themed chrome, and interactive per-pane scrollback scrollbar overlay (proportional thumb, hover/drag expansion, thumb drag and track page jump) implemented, plus VT-aware cursor blinking with conditional one-shot wakes; full pixel/caret comparisons, custom box/Powerline glyphs and color emoji pending |
| Input / mouse / selection / clipboard | `*Input*`, `*Selection*`, `*Clipboard*`, `*Mouse*` | Basic keys, `Ctrl+Backspace`, primary-screen viewport scroll shortcuts (`Shift+PageUp/PageDown`, `Ctrl+Shift+Up/Down`, `Ctrl+Shift+Home/End`), mouse selection (cell, word double-click, line triple-click, captured outside-pane drag clamping, copy-on-select), keyboard selection (`Shift+Left/Right/Home/End`, `Ctrl+Shift+Left/Right/Home/End`), selection highlight rendering, `Ctrl+C` (smart copy when selected) / `Ctrl+Shift+C` copy, `Ctrl+Shift+X` cut and `Backspace` cursor-end selection deletion, `Ctrl+Shift+V` / `Shift+Insert` / right-click paste with newline normalization and DEC 2004 bracketed paste, wheel scroll, TUI mouse reporting (`DEC 1000/1002/1003` + SGR/SGR-pixels/UTF-8/urxvt encoding with `Shift` override), and write-only OSC 52 / OSC 1337 clipboard sequences implemented; IME and click-to-move-cursor pending |
| Shell profiles and integration | `Model/Shell*`, `ShellIntegration/*` | Startup selection/resolution for cmd/system, Windows PowerShell, pwsh, nu, Git Bash, absolute custom executables and project/default/explicit-distribution WSL/Linux programs; cmd/PS/Nu integration, runtime Shell Picker overlay (`New Tab`, `Split Right`, `Split Down`, `Set Default`), top-bar `[ + | ▾ ]` split button, Command Palette shell actions, and per-pane shell overrides implemented; profile persistence pending |
| Windows/WSL projects and directories | `Model/Workspace.cs`, `Model/Wsl*`, `MainWindow.Projects.cs` | Local/selected-distribution Linux OSC 7 ownership/inheritance and WSL UNC project startup implemented; cross-distribution/Windows-shell mismatches reject; project UI and Linux process metadata pending |
| Settings and session persistence | `Model/AppSettings.cs`, `Session*`, `VexJsonContext.cs` | Pending; native currently does not read/write the production profile |
| Saved layouts | `SavedTerminalLayout.cs`, `MainWindow.SavedLayouts.cs` | Pending; opening explicitly executes commands once; restore must never replay commands |
| Themes/appearance/font editor | `ChromePalette.cs`, `Model/TerminalTheme.cs`, settings overlay | Vex Dark/default fonts and runtime 12-theme switcher first; persisted theme/font settings pending |
| Palette, tab peek, overlays/popups | `CommandPalette*`, `TabPeek*`, `MainWindow.Overlays.cs` | Command Palette (`Ctrl+Shift+P`), Tab Peek (`Ctrl+Shift+O` with multi-pane status origin details and `• Pane N` badge), Shortcuts (`Ctrl+/`), Theme Picker (`Ctrl+Shift+M`), Shell Picker (`[ + | ▾ ]` / palette), inline Tab Rename (`F2`), and running-program Close Confirmation modal implemented |
| Status, attention, icons, titles | `ProgramStatusTracker.cs`, `ProgramStatusPresentation.cs`, `AppIcon*` | OSC 7501 records/capability replies, pane/tab priority status pills, progress, BEL (`\x07`) / background attention indicators, acknowledgement, multi-pane status origin formatting (`Pane N - ...`) and attention-origin focus routing (`focusTabAttentionOrigin`), sanitized OSC 0/2 terminal title formatting (`terminal_title.zig`), 73-entry curated Lucide + SimpleIcons vector icon catalog (`app_icon.zig`) with per-session shell brand icons and zero-allocation cached-PID foreground process detection (`vex_pty_foreground_process`), per-DPI Direct2D GPU `ID2D1Bitmap` icon cache, overlapping `TabIconStack` (with `+N` overflow badge), pane header favicons, and custom Windows DWM titlebar chrome (project pill with git branch, `[ + | ▾ ]` new-tab/shell-picker split button, caption buttons, and `app.ico`) implemented; status hover tooltips and multi-project switcher menu pending; raw terminal titles must not become trusted UI controls |
| Links/path completion/search | `*Links*`, `*PathCompletion*` | URL detection/Ctrl+Click and inline path picker implemented; best-fifty ranking uses bounded scratch space and deterministic score/name ties, with a real-shell insertion fixture. Scanning is synchronous and capped at 16,384 entries; background indexing and terminal search remain pending |
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
- cmd does not emit OSC 7 by default (including its later prompt integration). Do not
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
- OSC 7501-only checkpoint: 41 tests in Debug and ReleaseSafe, ReleaseSafe application
  build and desktop smoke pass. The captured status screenshot in this session
  is black; these checks do not establish visual parity.
- WPF files are unchanged. Release solution build has zero warnings/errors and
  all 139 libghostty tests pass. The application suite passes 533/536 tests;
  the two clipboard tests still fail in isolation (clipboard lock/notification
  unavailable), while the animation test passes on isolated rerun. The suite
  therefore cannot be reported fully passing in this desktop session.
- OSC 9;4 progress fallback and OSC 133 lifecycle are covered by the later shell
  checkpoint below. Full tooltip/message presentation, project aggregation and
  attention-origin navigation remain pending.

## Shell integration checkpoint verification

- Startup-only `--shell` IDs or an absolute `--program` plus raw `--arguments`;
  one resolved, owned launch applies to every new tab/split. No settings UI or
  production-profile read/write is implied. WSL requests rejected at this historical checkpoint;
  explicit-distribution launch is implemented in the later checkpoint below.
- Windows argv quoting, UTF-16 command-line bounds, canonical/PATH resolution,
  missing-profile fallback, missing bootstrap errors and environment ownership
  have allocator-failure fixtures. Custom programs validate before filesystem IO.
- ConPTY receives fresh, borrowed UTF-16 launch buffers and a child-owned
  environment; no worker borrows them after CreateProcessW. TERM/COLORTERM
  defaults preserve explicit overrides and do not mutate parent app globals.
- cmd integration emits prompt markers only. PowerShell preserves the user's
  prompt/history handler and reports command boundaries/directories; unavailable
  PSReadLine explicitly reports degraded start tracking. Nushell enables its own
  OSC 7/133; Git Bash/custom launch retains existing integration without injection.
- OSC 133 routine/long/failed commands, prompt cleanup and result persistence
  are tested with deterministic monotonic timestamps. OSC 9;4 is suppressed by
  valid OSC 7501 (including clear) until full reset. Marker stripping preserves
  the real-DLL prompt cursor, including every read boundary.
- 52 tests pass in Debug and ReleaseSafe; ReleaseSafe app build and both desktop
  scripts pass. On this machine all five built-ins are present and exercised,
  including PowerShell/Nushell automatic Unicode OSC 7 split inheritance,
  completion/failure status and a Unicode/spaced cmd installation path.
- At that checkpoint, profile menus/per-pane shell overrides, Linux projects
  and settings were pending. The WSL checkpoint below adds Linux launch, not
  the remaining UI/settings. cmd bootstrap paths with `%` or quotes reject
  explicitly; this is not full path/profile compatibility parity.

## WSL checkpoint verification

- Windows + WSL is the explicit platform target, without native Linux/macOS ports.
- `--shell wsl --distribution NAME` starts the Linux login shell;
  `--shell wsl:/absolute/program` executes arbitrary Linux programs, with optional
  raw argv arguments. No shell-name whitelist, guessed flags or cmd fallback.
- `wsl_launch.zig` owns distribution/argv metadata; `wsl_paths.zig` parses Linux
  directories/distribution names and maps the two WSL UNC host forms. Shared
  Windows argv quoting lives in `windows_arguments.zig`, without import cycles.
- Windows drive paths go unchanged to WSL `--cd` for its mount configuration;
  WSL UNC paths retain their Linux spelling and selected distribution. The
  Windows launcher starts in its own directory rather than probing a Linux UNC cwd.
- Pane directory context borrows application-owned distribution metadata until
  teardown. Linux OSC 7 maps into that context; invalid/remote/wrong-domain reports
  preserve the previous path. No POSIX symlink/parent path normalization is guessed.
- Parser, per-pane argv, ownership and transactional path/cache allocation failures
  have unit fixtures; real-DLL directory callbacks cover every UTF-8/OSC chunk boundary.
- Live Ubuntu smoke covers default login, a Linux executable under a Unicode/spaced/
  apostrophe path, TERM/COLORTERM defaults and explicit overrides, Linux OSC 7/split
  inheritance, new-tab project root, UNC project startup and busy shell/child shutdown.
- 59 tests pass in Debug and ReleaseSafe; ReleaseSafe app build, workspace smoke,
  all five installed Windows built-in shells and Ubuntu WSL smoke pass. WPF files
  remain unchanged; no fresh WPF test result is implied by these native checks.
- Explicit distribution was required at this checkpoint; default/project-aware
  selection is implemented below. Custom Linux hostname recognition, prompt adapters, Linux process/icon tracking
  and deliberately detached Linux process cleanup are still pending. Existing shell
  integration remains optional; no user startup file or production profile is rewritten.

## Default WSL distribution checkpoint

- CLI `--distribution` is optional. Automatic Windows-project WSL launches read
  the current user's Lxss `DefaultDistribution` GUID and `DistributionName` once,
  without an enumeration/helper process or booting a distribution to discover it.
- WSL UNC projects choose their own distribution without consulting the registry;
  the `system` profile/no CLI flags selects Linux there. Explicit non-system
  Windows/custom profiles and cross-distribution mismatches fail rather than remap.
- The application resolves automatic selection before constructing its immutable
  launch owner. New tabs/splits keep that owned distribution; registry changes
  cannot retarget an already-open workspace.
- `windows/wsl_registry.zig` confines native registry handles to the IO seam.
  Only REG_SZ with bounded, terminated UTF-16 is decoded. GUIDs validate before
  opening subkeys, and distribution names parse into the existing domain type.
  Missing/inaccessible/corrupt metadata produces an error; no first-distro/cmd fallback.
- Registry fixtures create/delete unique volatile test keys outside Lxss. They
  cover missing values/keys, invalid GUIDs, value types, lengths, NULs, surrogate
  errors, unsafe distribution names and allocator failures, without altering WSL
  registrations/defaults or production profiles.
- The Lxss format is a compatibility dependency, not a guaranteed public default
  discovery API. [Windows Terminal's distribution generator](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalSettingsModel/WslDistroGenerator.cpp)
  likewise reads Lxss distribution metadata. [RegQueryValueExW](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regqueryvalueexw)
  explicitly does not guarantee string termination; native checks the raw contract.
- Live WSL smoke adds registry-default round trip, split distribution retention,
  and no-flag UNC project selection. 63 tests pass in Debug/ReleaseSafe, the
  ReleaseSafe build and all three desktop suites pass, including all five installed
  Windows built-ins and Ubuntu. Native handle counts remain unchanged across
  registry allocation-failure reads. WPF files remain unchanged; its suites were
  not rerun for this isolated native checkpoint. Profile menus/persistence remain pending.

## Interactive divider checkpoint

- `workspace/pane_tree.zig` shares the DIP partition between leaf placement and
  divider hit testing. Divider IDs derive from the application's non-reused
  fresh pane tokens; no drag retains pointers to mutable/promoted tree nodes.
- `workspace/divider_drag.zig` owns allocation-free begin/move/commit/cancel
  transitions. The initial grab offset prevents a jump; each move resolves the
  target's current container. Removed targets clear rather than retarget the drag.
- Interactive ratios satisfy both 0.1..0.9 and 100-DIP neighboring subtree
  minimums. A container that cannot fit two minimums disables dragging. This
  preserves existing tiny layouts rather than introducing WPF's grid overflow;
  recursive descendant minimum aggregation and final 1-DIP splitter styling
  remain separate parity work. The current 6-DIP gap is unchanged.
- Win32 owns capture/cursors and sends checked event tags/finite DIP coordinates
  through the adapter. ReleaseCapture failures propagate; actual capture is
  probed on the window thread, not inferred from the Zig state or a test thread.
- Release commits live emulator/ConPTY sizes without changing pane focus. Escape
  is consumed, not translated into terminal input. Capture/focus loss, cancel
  mode, window/DPI resize and tab/tree changes restore the initial ratio before
  applying new geometry or freeing nodes. Cancelled/captured releases cannot
  activate window caption buttons.
- Four co-located unit fixtures cover geometry boundaries, both axes, nested
  containers, grab offset, clamps, commit/rollback, removed/promoted IDs, invalid
  floats and undersized handles. Existing tree OOM tests cover the updated nodes.
- `tests/Test-NativeDividers.ps1` exercises real window-thread capture and live
  sessions, child console/emulator dimension agreement, focus, outside-window
  limits, cancellation, tab/close mutations, resize and shutdown while dragging
  under flooding output. Messages are synthetic and DPI-scaled; physical mouse,
  cross-monitor DPI and matched responsiveness/visual evidence remain pending.
- 67 tests pass in Debug/ReleaseSafe; ReleaseSafe build and all four desktop
  scripts pass, including all five installed Windows built-ins and Ubuntu WSL.
  WPF files/profile remain untouched; its suites were not rerun for this checkpoint.

## Tab reorder checkpoint

- `workspace_model.zig` assigns stable tab IDs from fresh first-pane tokens;
  closing that first pane does not change tab identity. `moveTab` transfers tree
  owners within the existing array without allocation or session recreation.
  Selection follows the same tab; close fallback follows the reordered neighbors.
- `tab_strip.zig` owns bounded DIP geometry and filtered insertion slots. The
  window copies parsed count/width before input/paint rather than using a stale
  count from the last frame. Gap/close hit regions do not arm tab drags.
- `tab_drag.zig` models none/armed/dragging. Press selects; Manhattan movement
  of 6 DIPs starts capture, ±8 DIPs bounds vertical drop tolerance, and home or
  outside drops do not move. No preview mutates the collection. Removed IDs clear
  instead of targeting another owner. Reorder does not acknowledge unread results.
- The application models divider/tab gestures as an exclusive union. Win32
  owns armed/captured/cancelled input and consumes Escape; source close, workspace
  actions, resize/DPI, focus/capture loss and cancel mode clear state before any
  node/array mutation. Repaint follows clearing state, including synchronous frames.
- An insertion marker is implemented. Animated ghost/push, strip scrolling,
  rename, physical mouse/cross-monitor DPI and full visual evidence remain pending.
- Five additional co-located fixtures cover strip boundaries, threshold/drop
  lifecycle, malformed input/stale IDs, every selected/source/destination
  combination for a three-tab model, owner retention and close fallback.
- `Test-NativeTabs.ps1` uses a bounded `VEX_NATIVE_SELFTEST`-only atomic Win32
  replay through production handlers, not mocks or a separate domain shortcut.
  Real capture/preview ownership is sampled before release. It verifies live
  PTY identity, split focus/tree and parsed viewport retention, unread status,
  both move directions, cancellation, source close, eight tabs and captured
  shutdown after flooding output has reached the viewport.
- Synthetic multi-message replay can race OS-generated moves or real desktop
  focus/capture changes. This was observed in the divider fixture; atomic tab
  replay avoids interleaving without cursor manipulation or production exceptions.
  Divider reruns pass but its quiet-desktop requirement remains explicit. One
  WSL default-distribution marker attempt also timed out; its full rerun passed,
  without claiming a proven cause or universally stable desktop automation.
- 72 tests pass in Debug/ReleaseSafe; ReleaseSafe build and all five desktop
  scripts pass. Atomic tab smoke additionally passes three successive runs.
  Windows built-ins and Ubuntu WSL remain covered. WPF files/profile are unchanged;
  its suites were not rerun. These checks do not establish visual/performance parity.

## Process-exit close checkpoint

- Exited shells close their own split or nonfinal tab without selecting hidden
  tabs. Active split close uses first-leaf fallback; tab close uses the existing
  neighboring-tab rule. Unknown/duplicate pane tokens leave ownership unchanged.
  The final workspace leaf stays readable with a real exit-code banner, and a
  fresh tab can still start. Banner/feed failures now propagate rather than being
  silently discarded.
- Captured gestures cancel before model mutation/session teardown. Close-popup
  targets use stable TabId values so an unrelated background exit cannot retarget
  a pending confirmation to the tab now occupying its old array index.
- Two pure model fixtures cover every selected/closing tab combination, nested
  hidden panes, focus/zoom fallback, stale IDs and the final-tab rule. A live
  ConPTY fixture covers exit codes, visible/background pane/tab closure, real
  pointer capture, confirmation index shifts, session release and retained final
  output. All 105 Zig tests pass in Debug/ReleaseSafe. The eight desktop fixtures
  pass on their latest runs. One earlier workspace attempt observed two paints
  during its zero-hidden-output-paint sample; its isolated rerun passed, without
  establishing the cause. Synthetic gestures still require a quiet desktop;
  native allocation-failure injection and physical teardown-race testing remain
  pending.

## Cursor blink checkpoint

- The real Ghostty cursor-blink flag follows DECSCUSR/DEC 12 independently of
  DEC 25 visibility. A value-only pane/cursor key and monotonic 530 ms scheduler
  preserve phase across paints, reveal on input/position/style/focus change,
  and skip missed periods without repaint catch-up loops or snapshot ownership.
- Only the selected, visible, running pane can arm the one-shot Win32 timer.
  Steady/hidden/off-viewport cursors, inactive windows, minimization, overlays,
  tab rename and process exit stop it. Stale timer messages cannot resurrect it.
  The palette exposes an app-lifetime blink toggle; profile persistence remains
  pending and the WPF profile is untouched.
- The desktop harness explicitly selects steady cursors through that production
  action for quiescent rendering/benchmark fixtures. This is not a selftest-only
  shortcut in production blink behavior. Cursor tests opt into the real default.
- Live ConPTY checks cover steady/reveal, bar/block cursor-cell GPU differences,
  cached snapshot generation, input reveal, the actual palette command,
  focus/minimize/restore, inactive-pane/hidden-tab isolation and exited terminals.
  Focus messages are synthetic, not physical keyboard/multi-monitor evidence.
  Each eligible blink still repaints the scene; no CPU/memory/latency improvement
  is claimed. Existing upload trimming requires two seconds without presentation,
  in that historical checkpoint. The upload reclamation checkpoint below removes
  that dependency.

## Path completion ranking checkpoint

- The result cap now applies the same score/name ordering as final presentation,
  rather than retaining arbitrary equal-score entries from enumeration order.
  Case-insensitive name ties use exact bytes, then entry index for stability.
- A standard-library priority queue retains fifty candidates in bounded stack
  scratch space: O(entries × log(50)) selection instead of scanning all fifty
  retained results for every candidate. Warm output storage is reused; failed
  output allocation preserves the previous visible match list.
- A regression first fails on the previous ranking, then compares retained
  matches against a full sort over 16,384 reverse-ordered entries and seven
  queries. Fixtures cover case ties, allocation failure and warm queries under
  an allocator that rejects every allocation. All 113 tests pass in Debug and
  ReleaseSafe; the ReleaseSafe application builds.
- The live cmd fixture checks actual inserted/filtered file contents, fifty-result
  bounds, no-match/backspace recovery and Unicode/spaced path quoting. Its GPU
  popup capture was inspected. This does not make directory scanning asynchronous,
  establish physical keyboard/IME parity or measure end-to-end query latency.
- Local ReleaseSafe microbenchmark against `14c2011`: three alternating warm
  samples, 280 refreshes each (40 repetitions of the seven fixture queries over
  16,384 reverse-ordered `file-{d:0>5}` names, every seventh entry a directory).
  A monotonic timer excludes setup, filesystem enumeration and output IO;
  output storage is warmed first. Baseline: 663.355/671.581/641.730 ms; bounded
  heap: 625.001/615.621/613.723 ms. Median falls about 7.2%, including unchanged
  scoring and final sorting. Baseline results are incorrect for tied candidates;
  this is not a startup, filesystem or interactive-latency benchmark. Raw local
  samples: `zig-out/verification/path-ranking-benchmark.txt`.

## Upload reclamation checkpoint

- Each upload page records its last allocation-use time once per frame, without
  a clock call per quad. Maintenance releases only tail pages unused for two
  seconds, retaining two warm pages per slot and all recently used pages.
- Completed slot fences gate resource release. Maintenance neither drains nor
  waits on the queue, and ongoing cursor presentation cannot pin stale uploads.
- The live hardware/WARP regression first failed on the previous implementation
  (16.5 MiB retained instead of the 1 MiB reserve), then passed with real cursor
  animation continuing through reclamation. ReleaseSafe build, all 110 tests in
  Debug/ReleaseSafe and the cursor desktop fixture pass. No startup/latency or
  whole-process memory improvement is inferred from the upload-capacity check.

## Native D3D12 terminal draw checkpoint

- `windows/terminal_renderer.{h,cpp}` adds a native instanced terminal pass with
  a shared 1 MiB R8 atlas, bounded collision-checked UTF-8 keys and miss-only
  software DirectWrite/procedural glyph rasterization. Text, cell backgrounds,
  decorations and cursors batch through precompiled shaders; chrome/overlays
  still use On12. Grayscale antialiasing/quarter-pixel phases are not a claim of
  old ClearType pixel parity, color emoji or a completed rendering-in-Zig port.
- Persistently mapped 256 KiB upload pages belong to one of two fence-protected
  slots. Warm frames reuse them; idle trimming drains GPU work before releasing
  excess pages. Cache exhaustion records earlier draws before texture coordinates
  are reused. Neither uploads nor glyph storage grow without explicit limits.
- The extended desktop renderer fixture verifies warm cache hits/no new misses
  or upload allocations, warm batching, styles/Unicode/wide/combining/custom
  glyphs, and more visible unique CJK graphemes than cache entries. Hardware and
  WARP fixtures pass, including in graphics-debug mode with no validation
  errors/corruption. Existing performance warnings remain logged.
- All 103 Zig tests pass in Debug/ReleaseSafe; ReleaseSafe build and all five
  existing desktop suites pass (including Windows shell presets and Ubuntu WSL).
  Native allocation-failure injection, actual GPU loss, physical cross-monitor
  DPI, Windows 10 execution and input-to-present latency remain unverified.
- Three alternating warm CPU/memory samples per workload compare with the saved
  preceding On12-only executable: styled 28x96 text falls from median 52.257 to
  1.476 process-CPU ms/frame and ~357 to ~182–188 MiB process working set. Plain
  text timing ranges overlap and memory increases ~11 MiB. These are not GPU
  frame times, end-to-end latency or a whole-app 35x speedup. The old ~53.5 MB
  HWND baseline has not been recovered; removing remaining On12 UI overhead is
  separate work. Full method, resource budgets and limitations: `RENDERER.md`.
- All files remain under `native/`; the sibling WPF implementation/profile are
  untouched. The earlier presentation-only checkpoint below is historical.

## D3D12 presentation checkpoint

- `windows/windows_renderer.{h,cpp}` replaces the HWND render target with a scoped
  D3D12 device/direct queue/fence owner, two BGRA flip-discard buffers and
  Direct2D/DirectWrite drawing via official D3D11On12 interop. No native glyph-atlas
  pass, renderer-in-Zig migration or performance improvement is claimed.
- Acquire/release/Flush and per-buffer fences govern GPU ownership; resize drops
  every bitmap/wrapper reference and flushes deferred destruction before releasing
  native buffers. DPI-only changes rebuild targets. Graphics reset/recreation
  preserves live sessions, viewport contents and layout updates during resize.
- The desktop fixture covers hardware and forced WARP, GPU-backed capture with
  byte-identical pre/post healthy-device recreation, an injected resize reset,
  minimize/restore, idle present suppression, three panes, overlays/themes/zoom
  and selftest-only diagnostics. Screenshots were inspected. Actual GPU loss,
  physical multi-monitor DPI and Windows 10 execution remain unverified.
- All 103 Zig tests pass in Debug/ReleaseSafe; ReleaseSafe build and the five
  existing Windows desktop suites pass, including all installed Windows shell
  presets and Ubuntu WSL. The renderer fixture also passes with D3D12 debugging:
  zero validation errors/corruption, but logged translation performance warnings
  820/1328 remain. This is not a warning-free result.
- A same-machine, one-off ReleaseSafe workspace smoke shows increased one-pane
  working set (~53.5 MB to ~179.0 MB) and first-frame log (~97.5 ms to ~123.3 ms)
  versus native `b9f099e`. These are not benchmark distributions or a WPF comparison;
  translation overhead is a regression to address with native D3D12 draw passes,
  not evidence that choosing a newer API made Vex faster.
- Build commands, resource lifecycle, artifacts, limitations and the incremental
  native draw-pass direction are documented in [RENDERER.md](RENDERER.md).
  WPF source/profile remain untouched. Repository-wide `zig fmt --check src`
  still reports six pre-existing unformatted Zig files; unrelated formatting was
  not changed in this checkpoint. The changed `build.zig` passes formatting.

## Source structure checkpoint

- `src/main.zig` is the allocator/error entrypoint and explicit test-module root.
- `src/app/` separates workspace orchestration from the stable session owner.
- `src/workspace/` is pure tab/tree state; `src/terminal/` owns emulator/ABI/feed
  parsing; `src/shell/` owns launch/directory state plus integration scripts.
- `src/windows/` contains the Windows event/render/process seams. ABI symbol
  resolution no longer depends on the entire rendering/window adapter.
- Live tests are in `tests/` with one shared desktop harness. Unit tests stay
  beside the implementation, and the full 52-test suite is retained after moves.
- Built-in shell IDs are presets, not an execution whitelist. Custom launch
  accepts any available absolute ConPTY-compatible executable and raw arguments;
  no universal cross-platform backend was claimed at that structural checkpoint.
  Linux program launch through Windows WSL is covered by the WSL checkpoint above.

No WPF profile is read/overwritten. This milestone does not replace production,
certify native memory safety, or establish complete parity/performance.
