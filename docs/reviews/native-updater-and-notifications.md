# Native updater and shared notifications — working-tree review

Scope: native updater, MSI/release packaging, restart handoff and the shared status,
error and final-session notification surface. Baseline: `55807d9`.

## Standards axis

No outstanding blocking findings in the reviewed scope.

- HTTP, hashing, MSI identity checks and helper preparation stay on the native worker.
  Zig owns workspace actions and the existing active-program close review.
- The helper uses explicit readiness/approval/cancellation events, a retained parent
  process handle, an MSI read lease and a canonical-install global mutex. Approval
  precedes parent exit in wait arrays, including simultaneous signalling.
- Feed parsing is bounded and strict; integrity checks are repeated at the helper
  boundary. Registered source MSIs remain available for maintenance and rollback.
- Error/status/update presentation shares `NotificationSurface`, geometry and hit
  testing. UTF-8 decoding owns temporary buffers before committing a new notice.
  The obsolete exit-card renderer and terminal-padding injection were removed.
- Text measurement is cached by content/font/width. Hover and reveal reuse glyph
  masks; settled surfaces do not schedule continuous paints. A reproduced replaced-
  card hover bug now has a GPU-pixel regression assertion and geometry-driven cursor
  refresh rather than stale semantic hover state.
- Test input/diagnostics require the existing self-test opt-in. Mock update windows
  cannot download or install an MSI. Tests do not close unrelated user windows.

## Spec axis

The implemented scope matches the requested updater and notification behaviour.

- Updates follow published stable releases, not commits. Portable builds offer the
  downloads page. The first updater-capable version still requires a normal MSI install.
- Preflight failure/cancellation leaves terminal sessions running. Approved restarts
  use the normal live-process review; no process migration is claimed.
- Notifications are compact, bottom-right, theme-aware and 90%-opaque behind readable
  content. X/action controls use a hand cursor, hover feedback and bounded motion.
  Transient errors/session status and the update card have distinct stacking slots.
- Final output is retained without blank padding; exit state remains in the header
  after the transient notice closes. Active-program confirmation remains modal.

## Verification evidence

- Native Windows suite: 237 tests in ReleaseSafe and Debug.
- Python packaging suite: 12 tests.
- Real desktop checks: dark/light GPU captures, every update/notice state, X/body hit
  behaviour, window-thread hand/arrow cursor, hover without new glyph-cache misses,
  replaced-card hover, idle paint budget, Unicode, 4096-byte body, malformed UTF-8,
  fade completion and an actual shell exit with preserved output.
- Isolated MSI fixture: another live window, cancellation, read lease, corruption,
  immediate parent exit, Unicode/spaced paths, real upgrade and deferred failure
  after file copies. Upgraded and rolled-back executable/helper hashes, product
  registration, restart and eventual uninstall were checked. Latest fixture evidence:
  `app/publish/update-installer-fixture-b001de7b79bf419d83d8b2d6e97d08fa/`.
- Transport fixtures cover blocked headers/body cancellation, errors, truncation,
  oversized/chunked responses and write failure. A published GitHub MSI was also
  downloaded and verified separately; that test does not install it.

Local captures/logs under `app/publish/` are generated evidence, not repository or
hosted-CI desktop coverage. No startup-speed or memory-reduction claim is made from
incomparable executable paths/GPU initialization. The desktop performance assertions
measure idle repainting and animation text-cache reuse, not a universal FPS guarantee.

## Remaining boundaries

- Windows 10 runtime and direct UAC-denial exercises remain unverified. Supported
  Win32/renderer APIs do not substitute for a Windows 10 desktop pass.
- Integrity relies on authenticated GitHub HTTPS plus the release hash; this is not
  Authenticode publisher verification or protection from a compromised release account.
- Windows Installer owns the transaction/reboot boundary. A 3010 result requests a
  Windows restart rather than automatically launching potentially mixed files. Power
  loss, OS corruption or refused elevation cannot be promised away by application tests.
- This review records pre-publication verification; stable publication uses the release
  pipeline and its additional clean-build checks.
