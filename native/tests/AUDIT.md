# Native test and legacy-code audit

Scope: `native/` only, against baseline commit `9f08099`. No .NET app changes.
Verification host: Windows x64, Zig 0.15.2, Python 3.13.14.

## Method

- Ran the baseline app build, formatting check, 157 Zig tests and 8 Python tests.
- Mapped `build.zig`, the imports in `src/main.zig`, embedded Zig tests,
  C++ fixture/static assertions, packaging tests and maintenance tools.
- Searched declarations and references across native sources, excluding test
  blocks when identifying production callers. Followed candidate call chains
  and migration history before deletion; reference counts alone were not proof.
- Kept vendored library APIs and generated assets out of dead-code removal.
- Added reproducing tests before fixing the two bugs below; both failed before
  their fixes and passed afterward. No skips or expected-failure markers added.

## Removed code and proof

| Removed | Evidence and preserved coverage |
| --- | --- |
| `PaneTree.setParentRatio` and private `findParent` | Only the pane-tree test called the leaf-ID setter. Production `DividerDrag.move/cancel` uses `setDividerRatio`, introduced in `08b4eb2`. Moved ratio validation/clamp assertions to that active API; added infinity, unchanged-state, repeated-clamp and stale-ID checks. Depth, allocation rollback and divider drag tests remain. |
| `ProgramStatusSummary.badgeLabel`, `detailSlice`, `formatOriginDetail`, and their single formatter test | The old overlay caller was removed in `d0a2c08`. Remaining calls were confined to the formatter chain and its test. Current native presentation calls `status_presentation.statusDetail` through `windows_ffi.nativeProgramStatus`; overview cards render terminal previews. Existing state-label, task/app fallback, UTF-8 boundary, repaint-key and FFI detail tests remain, with additional message/auth/idle/no-status assertions on the active formatter. No assertion for the obsolete `Pane N - ...` string was retained. |
| Native tab-list dialog kind and `.tab_peek` choose/preview event branches | `2c93296` replaced this dialog with inline previews; `presentTerminalModal` returns for `.tab_peek`. Input now goes through `terminal_overlays.handleOverlayKey/handleOverlayPointer`. Removed the unused enum member, reject its old numeric value at the C boundary, and retain the other dialog IDs. Overview layout, close-target and workspace tests remain. |
| `AppIcon.shellBrandIcon` | Its old shell-overlay caller was removed in `d0a2c08`; no native callers remained. Current modal shell items carry labels/details, while terminal icons use the active glyph catalog/resolver. All catalog, alias, shim/title precedence and unknown-badge tests remain. |
| `TerminalTheme.isDark`, `lighten`, `built_in` | Production reads `is_dark` and `themes` directly. `isDark` was used only by two assertions, now made on the actual field. `lighten` and the alias had no native consumers. Appearance partitions and derived chrome palette checks remain. |

## Genuine bugs fixed

### RIS retained mouse motion history

The new real-DLL regression enables SGR any-motion tracking, emits a motion
report, confirms same-cell deduplication, sends RIS and re-enables tracking.
Before the fix, the first post-reset motion at that cell returned an empty
slice instead of `ESC[<35;2;2M`.

`Terminal.onTerminalReset` now calls the existing `resetMouseEncoder` helper.
This is why that initially unreferenced helper was **not** deleted. The test also
confirms normal same-cell deduplication still works after the first new report.

### Invalid package label replaced staging before rejection

The new packaging regression creates a valid archive and a staging marker,
then calls `package` with an invalid label. Before the fix, label validation
raised an error only after staging had been deleted and recreated; the marker
was lost. Artifact-name validation now precedes all staging mutations. The test
checks preservation of the marker, existing archive bytes and checksum.

Also added staged-payload tampering coverage and missing-MSI/bad-MSI-checksum
assertions. These exercise existing behavior rather than loosening validation.

## Deliberately retained

- OSC 133 / OSC 9;4 progress handling: `TerminalSession.closeSnapshot` uses
  `shellActivityState` for close safety. Added explicit lifecycle, duration
  boundary, acknowledgement, progress suppression and exit-state assertions.
- `tests/conpty_fixture.cpp`: actually launched by the real terminal tests for
  screen/history/alternate-screen erase behavior.
- `tests/chrome_motion.cpp`: nine `static_assert` checks are compiled into the
  fixture build; lack of runtime output does not make them unused.
- Allocation-failure helpers, Lua decode/load test conveniences, and badge
  accessors: these still exercise active parsers/owners, not replaced features.
- Selftest diagnostics: the README explicitly identifies local desktop tools
  outside the repository. Missing in-repository callers cannot prove them dead.
- Built-in shell integration scripts, shader sources/compiler and generated
  graphics: embedding/offline generation are legitimate uses, not orphan code.

## Verification

- `zig fmt --check build.zig src`
- `zig build` and `zig build test-build` (ReleaseSafe)
- `zig build test`: **157 passed, 0 skipped**, executed on Windows
- `zig build -Doptimize=Debug`
- `zig build test -Doptimize=Debug`: **157 passed, 0 skipped**, executed on Windows
- `python -m unittest discover -s tests -p 'test_*.py' -v`: **10 passed**
- `python tools/package.py` and `python tools/package.py --verify`: real
  ReleaseSafe ZIP payload/checksums verified
- `git diff --check`

The Zig count is unchanged: one obsolete formatter test removed, one mouse-reset
regression added. C++ compile-time tests and existing DLL/ConPTY/allocation-failure
coverage were retained. Final standards/spec review found no outstanding issue
in the changed paths.

## Remaining verification gaps

No automated build/test failures remain. Interactive GPU rendering, dialog focus,
mixed-DPI/reduced-motion behavior, installed-shell/WSL desktop scenarios, and MSI
build/cabinet extraction/install were not exercised in this audit. Headless tests
and portable ZIP verification are not substitutes for those checks. This audit
is not a claim of exhaustive dead-code proof or desktop coverage.
