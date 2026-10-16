# Program-independent native rewrite

Scope: the Windows/WSL rewrite under `native/`, not the WPF implementation.
The product is a terminal with tabs/splits, not project management or named
workspaces. The user's removal of project/Git chrome is intentional.
Review sources: `AGENTS.md`, `REWRITE.md`, `RENDERER.md`, the current working tree,
and the user's requirement that a new program must not need a Vex-specific patch.

## Contract for subsequent milestones

- Application execution, terminal input/rendering, status, titles, close protection
  and path insertion must work without registering a program name.
- Protocols supply facts. OSC 7501 is program status; OSC 7 is the directory;
  OSC 0/2 is an untrusted display label. Do not infer status from titles, spinners,
  screen contents, agent names or icon availability.
- Shell/runtime adapters describe command grammars, not supported applications.
  Unknown grammar retains the executable identity; unknown input syntax gets
  literal path insertion instead of guessed shell quoting.
- A brand catalog is optional presentation data. Missing artwork gets a stable
  monogram, never another application's logo. It must not affect execution,
  process classification, warnings or protocol support.
- Keep disk IO/process inspection out of painting. Start optional workers lazily,
  coalesce obsolete requests and cap storage/work. Measure before claiming speedups.
- New features should include an unfamiliar/random-name program fixture, not
  merely tests for installed agents/editors.

## Audit and corrections

| Area | Finding / correction |
| --- | --- |
| Launch / terminal / input | Existing custom absolute executable and WSL program launch, ConPTY lifetime, VT emulation, mouse and paste are protocol-based. Shell presets are not an execution whitelist. Production WPF profiles remain untouched. |
| Titles and command identity | Removed the application allowlist, brand canonicalization and `pi-*` guessing from the parser. Plain labels remain intact. Launcher adapters decode unknown targets, scoped npm package ownership and Python modules without heap allocation. Ambiguous flags/inline source retain the host. |
| Process inspection | Removed the known-editor/agent/tool stop list and tool-specific helper exclusions. The first substantive process owns its workers regardless of name. Custom roots come from launch metadata and do not require a system snapshot. PID/creation-time caching includes failed command-line reads; requesting VM-read access is limited to cache misses. This is an ownership heuristic, not proof of the foreground process inside every shell/remote environment. |
| Close protection | Removed known-agent/editor registries and title-only claims of a live agent. Any observed process or explicit active status receives protection, including unnamed active reports. Tab closure now folds over the entire pane tree instead of silently checking only sixteen panes. Modified-title warnings say **possible** unsaved changes rather than claiming knowledge of an editor's buffer. |
| Icons | Replaced the incompatible numeric C++ drawing switch with generated brand geometry ordered from Zig IDs. Explicit app identity works for unknown runtimes too; retained done/error reports keep their reported artwork until status is cleared or replaced, rather than losing the completed task's identity. This is report identity, not proof of the current foreground process. Process-derived icons are cached independently so metadata clearing can restore them without another process query. |
| Command palette | Removed the nine-result navigation cap. Full-list keyboard/wheel navigation, Home/End/Page keys, stable scrolling/hit testing, fuzzy token ranking, UTF-8-safe input and small-window geometry share a pure Zig module. Search/ranking use bounded stack/inline storage. |
| Path insertion | Replaced UI-thread scanning with one lazy worker and recursive indexing. Main-thread navigation creates owned requests; the newest revision wins. Superseded scans stop between filesystem operations. Query edits during indexing survive publication. Rendering never waits for an index. |
| Path safety | Directory authority is parsed OSC 7 or the launch directory, not scraped prompt text. Insertion pastes a path, never Enter/a command. Foreground apps receive literal text, shell prompts use a known quoting adapter, and unknown shells are not assumed to be cmd. Enter/Tab with no result keeps the picker open. Fixed converted-path ownership on allocation failure, including WSL UNC error propagation. |

## Responsibility-based extraction

- Replaced the 2,838-line application file with one concrete terminal owner
  (about 1,150 lines) and keyboard, pointer, overlay, command and diagnostic modules.
  Feature functions borrow the owner; callback aliases introduce no forwarding
  objects, copied session owners, new allocation or dynamic dispatch.
- Split the 2,226-line platform implementation into the window/font owner
  (about 1,120 lines), chrome, terminal drawing and OS utilities. Paint interfaces
  borrow explicit frame values. Procedural glyph paths use the existing COM owner;
  shared color conversion has one definition. Generated artwork remains generated.
- Removed the old application/platform files rather than leaving compatibility
  wrappers. `src/workspace/` contains actively used tab/split state, not projects.
- A real eighteen-pane fixture reproduces the old lost close warning beyond pane
  sixteen. Streaming highest-risk review fixes it without allocating snapshots.
  Cancel retains all nineteen sessions across two tabs; confirmation releases the
  eighteen-pane tab and preserves its neighbor.
- Desktop fixtures now await asynchronous indexing and gate hidden output after
  real process metadata settles, instead of assuming synchronous scans or racing
  a fixed child sleep against metadata polling.

## Reference inspection

- [Rex path insertion demo](https://x.com/almonk/status/2103572918680645870):
  inspected text and extracted video frames. Cursor-anchored fuzzy path selection
  augments arbitrary stdin; it does not replace the shell. Vex now indexes nested
  paths, retains normal navigation and presents keyboard hints/loading/limit states.
- [Rex copy feedback](https://x.com/almonk/status/2101298258706362515) and
  [Activity Mode](https://x.com/almonk/status/2109262000165437943) were inspected;
  neither is evidence of command-palette behavior. Copy animation/activity UI is
  not added by this checkpoint.
- [Rex custom actions](https://www.superlogical.com/rex/docs/customize/actions):
  actions have titles/categories/descriptions/keywords and share key/palette/CLI
  invocation. Vex currently retains its typed built-in action registry; custom
  actions, scripting and richer palette metadata are not implemented here.

## Budgets and remaining limits

- One indexing thread is created on first use, not at startup. At most one pending
  request and one completed result are retained in addition to active scan/UI
  owners. Each index caps at 16,384 entries and 8 MiB of path-name bytes; matches
  remain capped at fifty. The limit is visible, not a claim of whole-disk search.
- Index results are consumed on the existing 250 ms metadata timer. Cancellation
  checks run between filesystem operations; a blocked OS/network call is not
  interrupted. Shutdown joins the worker. Network-stall/cancellation behavior
  needs native failure injection before claiming bounded shutdown latency.
- The icon catalog is still a 73-entry optional subset (64 brand paths and nine
  explicit monograms), not the whole Simple Icons library or executable-icon
  extraction. Unknown/new applications work through the normal fallback. Adding
  new artwork remains a data/catalog update, not automatic brand discovery.
- WSL/Linux process metadata remains unimplemented. Structured status works for
  any remote/Linux program, but no claim is made that title/Windows ancestry can
  identify arbitrary remote foreground work. Windows descendant inspection has
  explicit snapshot/queue/depth caps.
- Windows-drive-to-WSL completion formatting still assumes `/mnt/<drive>`; custom
  mount layouts need a distribution-aware path adapter. WSL UNC paths use their
  parsed Linux spelling. This pre-existing assumption is not universal support.
- cmd does not emit OSC 7 after `cd` with the current integration, so its picker
  uses the launch directory until valid directory metadata arrives. Prompt
  scraping was deliberately removed rather than guessing from arbitrary output.
- This is path autocomplete, not a shell command/flag/history completion engine;
  normal Tab remains owned by the running shell/TUI when the popup is closed.

## Verification and measured results

- After extraction and the tab-close correction: **129 unit tests** pass in Debug
  and ReleaseSafe; **16 desktop fixtures** pass in the production ReleaseSafe build.
  The UI pixel fixture passes on hardware/default and WARP with required D3D12
  validation; status presentation also passes with validation, including four
  distinct stacked favicons and overflow. Results: `zig-out/verification/refactor*`
  and `close-confirmation-{before,after}.txt`.
- Known diagnostic limitation: the full workspace active-output shutdown fixture
  times out with D3D12 validation enabled. It also fails against the reconstructed
  pre-extraction source, at the same close call. Temporary probes locate the stall
  inside renderer shutdown before final ConPTY teardown, not a new owner/callback
  regression. The exact driver/debug-layer cause remains unproven; the production
  build passes this fixture. Do not report the full debug-graphics stress suite as
  passing. Probes were removed from production sources.
- The measurements below predate the latest project-chrome removal/extraction;
  they are checkpoint evidence, not isolated refactor benchmarks.

- New desktop fixtures fail against the saved preceding executable: wrapping Up
  cannot reach the last palette action; a random-name root's helper steals its
  process identity. Both fixtures pass after the changes.
- `Test-NativeUniversalPrograms.ps1` compiles a randomly named test executable
  with its own helper. It exercises real ConPTY input, recursive path insertion
  into arbitrary stdin, unknown-app OSC 7501, close protection and stable PID.
- `Test-NativePalette.ps1` drives the native command-palette dialog by keyboard (wrap,
  Home/End, fuzzy query, UTF-8 limit) and saves dark/light/compact native captures;
  `Test-NativeModals.ps1` covers owned-dialog input isolation, settings apply, atomic
  persistence, restart restoration and pointer shapes. Existing path,
  icons, status presentation, process-exit, shell-picker and hardware/WARP UI
  pixel fixtures pass on their latest runs. These are synthetic desktop events,
  not physical IME/accessibility/multi-monitor evidence.
- Three alternating warm startup trials versus the saved pre-audit executable:
  median Zig-main-to-first-present **142.250 -> 145.122 ms**. Three warm 180-frame
  trials/workload: plain CPU median **1.215 -> 1.302 ms/frame**, styled median
  **1.302 -> 1.042 ms/frame**; ranges overlap. There is no demonstrated universal
  startup/rendering speedup. Memory remains roughly 52 MiB idle / 55–58 MiB in
  these text workloads; each reports the 1 MiB terminal upload reserve.
- One local 8,192-file picker sample: open request **0.755 ms**, index visible
  **101.746 ms**, slowest synchronous UI probe during indexing **54.591 ms**.
  Seven type-and-erase ten-character round trips range **14.260–33.784 ms**.
  This includes message scheduling/presentation/timer effects; it is not physical
  input-to-present latency, a baseline comparison or a network/cold-cache bound.
- Raw local artifacts: `zig-out/verification/{universality-startup,
  universality-renderer,picker-performance,palette,universal-programs}`.
  Ownership/allocation tests cover path conversion, query/index publication,
  stale revisions and bounded warm ranking. Native OS/GPU failure injection and
  exhaustive filesystem/launcher grammars remain outside this evidence.
