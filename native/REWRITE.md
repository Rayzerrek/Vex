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
| Process lifetime / ConPTY | `Vex.Terminal/TerminalSession.cs`, `ProcessTree.cs` | Per-pane ownership, bounded queues, process-exit observer and job-object teardown; auto-close/close confirmations and further cancellation/Win32 failure injection pending |
| Terminal core / feed filtering | `Vex.Libghostty/*`, `FeedFilterTests` | Same DLL, typed public API; UTF-8, color, wide/grapheme, modes, responses, snapshot cache, ConPTY-stripped DCS filter (`+q` XTGETTCAP / `$qm` DECRQSS), disabled resize scrollback pull, DEC 2048 in-band resize reports (`\x1b[48;...t`), DEC 2026 synchronized output frame batching (100 ms starvation cap), default bar cursor and ConPTY-compatible key encoder options implemented |
| Renderer / glyphs / DPI / caret | `Terminal/Native/*Rendering*`, `*Glyphs*`, `*Layout*`, `*Scrollbar*` | DirectWrite baseline, themed chrome, and interactive per-pane scrollback scrollbar overlay (proportional thumb, hover/drag expansion, thumb drag and track page jump) implemented; pixel fixtures, custom box/Powerline glyphs, color emoji and blink pending |
| Input / mouse / selection / clipboard | `*Input*`, `*Selection*`, `*Clipboard*`, `*Mouse*` | Basic keys, `Ctrl+Backspace`, primary-screen viewport scroll shortcuts (`Shift+PageUp/PageDown`, `Ctrl+Shift+Up/Down`, `Ctrl+Shift+Home/End`), mouse selection (cell, word double-click, line triple-click, captured outside-pane drag clamping, copy-on-select), keyboard selection (`Shift+Left/Right/Home/End`, `Ctrl+Shift+Left/Right/Home/End`), selection highlight rendering, `Ctrl+C` (smart copy when selected) / `Ctrl+Shift+C` copy, `Ctrl+Shift+X` cut and `Backspace` cursor-end selection deletion, `Ctrl+Shift+V` / `Shift+Insert` / right-click paste with newline normalization and DEC 2004 bracketed paste, wheel scroll, TUI mouse reporting (`DEC 1000/1002/1003` + SGR/SGR-pixels/UTF-8/urxvt encoding with `Shift` override), and write-only OSC 52 / OSC 1337 clipboard sequences implemented; IME and click-to-move-cursor pending |
| Shell profiles and integration | `Model/Shell*`, `ShellIntegration/*` | Startup selection/resolution for cmd/system, Windows PowerShell, pwsh, nu, Git Bash, absolute custom executables and project/default/explicit-distribution WSL/Linux programs; cmd/PS/Nu integration implemented; profile menu, per-pane overrides and persistence pending |
| Windows/WSL projects and directories | `Model/Workspace.cs`, `Model/Wsl*`, `MainWindow.Projects.cs` | Local/selected-distribution Linux OSC 7 ownership/inheritance and WSL UNC project startup implemented; cross-distribution/Windows-shell mismatches reject; project UI and Linux process metadata pending |
| Settings and session persistence | `Model/AppSettings.cs`, `Session*`, `VexJsonContext.cs` | Pending; native currently does not read/write the production profile |
| Saved layouts | `SavedTerminalLayout.cs`, `MainWindow.SavedLayouts.cs` | Pending; opening explicitly executes commands once; restore must never replay commands |
| Themes/appearance/font editor | `ChromePalette.cs`, `Model/TerminalTheme.cs`, settings overlay | Vex Dark/default fonts first; all remaining palettes/settings pending |
| Palette, tab peek, overlays/popups | `CommandPalette*`, `TabPeek*`, `MainWindow.Overlays.cs` | Pending |
| Status, attention, icons, titles | `ProgramStatusTracker.cs`, `ProgramStatusPresentation.cs`, `AppIcon*` | OSC 7501 records/capability replies, pane/tab priority status pills, progress, BEL (`\x07`) / background attention indicators, acknowledgement, sanitized OSC 0/2 terminal title formatting (`terminal_title.zig`), 73-entry curated Lucide + SimpleIcons vector icon catalog (`app_icon.zig`) with zero-allocation cached-PID foreground process detection (`vex_pty_foreground_process`), per-DPI Direct2D GPU `ID2D1Bitmap` icon cache, overlapping `TabIconStack` (with `+N` overflow badge), pane header favicons, and custom Windows DWM titlebar chrome (project pill with git branch, new-tab `+` button, caption buttons, and `app.ico`) implemented; tooltips/origin routing and multi-project switcher menu pending; raw terminal titles must not become trusted UI controls |
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
