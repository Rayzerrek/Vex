# Program status and saved layouts review

Review target: working tree against `d8ce04d38159172510784a13afd8e0ea9b77fbe0`
on 2026-10-08, including added files. Command: `git diff HEAD`; no feature
commits preceded this review.

Standards loaded: `AGENTS.md`, `CONTRIBUTING.md`, existing WPF/ConPTY conventions,
and the code-review-and-quality smell baseline.

Spec: [Rex Program Status Protocol v0.3](https://www.superlogical.com/rex/docs/build/program-status),
plus the requested minimal Vex UI and saved pane layouts with commands. CLI,
persistent background sessions and additional keybindings are outside scope.

## Standards

| Severity | Location | Evidence and consequence | Resolution |
| --- | --- | --- | --- |
| Blocker | `Vex.App.Tests.csproj`, transport fixture | Without a runtime identifier, the test loads conpty.dll under runtimes/ and misses packaged OpenConsole. Query followed by DA1 fails on system conhost, while production uses another host. This violates representative integration verification. | Match the app's win-x64 native layout; direct and prewarmed query tests exercise the real ordering. Remove the obsolete fragmented reply workaround. |
| Should Fix | `ProgramStatusTransportTests.SavedLayoutCommand_StartsOnceAndShellReportsPrompt` | cmd emits the command in OSC 0 metadata. Counting a raw marker overcounts execution; a marker in a Nushell error can satisfy readiness before execution. | Require the output line and prompt, and assert an appended file contains exactly one execution. This exposed the Nushell launch failure below. |
| Simplification | Status priority in tracker and app model | Possible Duplicated Code / Repeated Switches: two rankings could choose different statuses in a pane and its aggregate. | One `ProgramStatusSummary.DisplayPriority` map serves records, panes, tabs and projects. |

No open Standards findings after correction. Checked ownership, native seams,
bounded storage, dispatcher work, lazy UI construction, persistence, dependencies,
discoverable names and the remaining smell baseline. No runtime dependency added.

## Spec

All findings below were reproduced and corrected before committing.

| Severity | Location | Requirement and reproduction | Resolution |
| --- | --- | --- | --- |
| Blocker | `ProgramStatusTracker.TryParseProgramStatus` | Rex: "last value wins". `id=bad//id:id=correct` was rejected before reaching the final address. | Validate final ID grammar after scanning all pairs. |
| Blocker | `ProgramStatusTracker.IsStatusFieldWithinLimits` | Rex: "Check every pair". An oversized malformed field or an oversized earlier duplicate could evade atomic rejection. | Apply each occurrence's caps before grammar filtering; regression covers all bounded fields and ID depth/segments. |
| Blocker | `ShellLaunchBuilder.BuildNushellArguments` | Saved commands must execute and report failure. Apostrophe doubling changes a Nu path; `(char escape)` aborts startup on Nu 0.114.1. Startup failures also bypass Nu's REPL markers and appear idle at the next prompt. | Quote Nu strings and Windows argv separately, emit numeric ESC/BEL, and report caught startup errors before the prompt. Real ConPTY tests verify path quoting, one execution, and exit code 7. |
| Blocker | `Vex.App.csproj`, `build-installer.ps1`, payload packaging | Published shell scripts were bundled instead of available beside the app. Existing file-list ZIP compression also flattened directories. Both lose the runtime paths used by the launch builder. | Publish shell scripts as sidecar content and create archive entries with publication-relative paths, retaining nested assets and excluding debug symbols. |
| Should Fix | `GhosttyTerminal`, OSC escape state | Rex terminator: "ST". ESC before BEL or repeated ESC before ST could consume following printable text. | Recognize both terminators in the pending-escape state; retain subsequent grid text. |
| Should Fix | `ProgramStatusTracker.TryDecodeStatusText` | Rex: "invisible formatting characters". UTF-16 char filtering missed supplementary U+E0001. | Filter Unicode runes, with no extra string allocation for ordinary labels. |
| Should Fix | Aggregate status badges | Rex: "which terminal it came from". A tab/project tooltip omitted the source pane. | Bind trusted tab/pane numbers in tooltips and accessibility names, including reorder and equal-status changes. |
| Should Fix | `SavedLayoutOverlay` | Minimal editor must preserve pending work during Alt-Tab. Deactivation hid it and reopening discarded edits. | Retain editor visibility; real WPF test pumps deferred deactivation and verifies bindings. |
| Should Fix | `SavedTerminalLayouts.ValidateLayoutPane` | Advertised 32-pane bound rejected valid unbalanced layouts with fewer panes because depth was capped at eight. | Allow depth up to 31 while retaining the 32-leaf cap; verify 32 accepted and 33 rejected. |
| Should Fix | `ProgramStatusTracker.TrimAsciiWhitespace` | Rex: "Whitespace around keys and values". Form feed and vertical tab were not trimmed. | Trim the complete ASCII whitespace range; both cases have regression coverage. |

No open mandatory protocol findings in the reviewed paths. Separately verified
fragmentation, fixed capability reply before DA1, replacement, subtree clearing,
ancestor app resolution, record eviction, prompt/process lifetime, RIS/DECSTR,
alternate-screen independence, strict UTF-8/base64, controls and OSC 9;4 fallback.
WPF exercises report coalescing, transient attention, editor bindings and menus.

Platform note: Vex is a Windows WPF application and ships no terminfo entry.
Rex's recommended `Pst` capability is therefore not supplied; runtime detection
is implemented. Saved layouts are a Vex adaptation, not a Rex wire protocol.

## Validation

Final results and measured performance are recorded in
[ProgramStatusComparison.md](../../windows/Vex.Performance/ProgramStatusComparison.md).
Verification includes Windows Debug/Release suites, real published WPF startup,
native payload checks, and the web formatting/lint/type check.

Standards: 3 resolved findings, worst Blocker; Spec: 10 resolved findings, worst
Blocker. Zero open findings on either axis within the reviewed scope.
