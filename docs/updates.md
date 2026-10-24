# Native application updates

Vex checks the latest **published stable GitHub Release** ten seconds after startup,
then every six hours while running. Checks and downloads run on one native worker;
the terminal/window thread never performs HTTP or hashes a package. Preview artifacts
and unpublished commits are not an update channel.

## Using updates

- Open the command palette (`Ctrl+Shift+P`) and choose **Updates: Check for Updates**
  to check immediately or bring back a dismissed, downloaded update.
- A compact, theme-aware notification in the bottom-right corner shows download
  progress, verification and **Update & restart**. Its background is 90% opaque;
  text/buttons remain readable. Alpha composition uses the existing GPU renderer,
  not Windows 11-only Mica, a blur pass or an additional overlay window.
- **X** or `Ctrl+Shift+.` hides the notification without interrupting terminals.
- **Update & restart** or `Ctrl+Shift+U` runs the existing close review for active
  programs, respecting the user's close-confirmation preference. Cancelling the
  review cancels the installation intent. Restarting closes terminal sessions;
  it does **not** migrate running processes or restore their contents.
- An ordinary application exit does not install an update. Its temporary download
  is removed; a subsequent launch checks again.

The first version containing the updater must still be installed normally: run its
MSI **over the existing installation**, without uninstalling first. Older binaries
cannot discover a feature they do not contain. The installed updater also recognizes
older native MSI products without `InstallLocation`, using their cached MSI file
component paths rather than guessing an installation directory.

Portable ZIP builds offer **Open downloads** instead of silently installing an MSI
or modifying an arbitrary portable directory. Local build output is portable too.

## Shared notifications

Updates, errors, configuration warnings and final-session completion use the same
native `NotificationSurface`: content-sized geometry, themed severity icon, X with a
28-DIP hit target, hand cursor on controls and short 160-ms entrance/exit/hover motion.
Motion follows the existing animation preference. Text measurement and glyph masks
are reused rather than rebuilt on animation frames. Hover/cursor targets are updated
when content or stacking moves a card under a stationary pointer.

Transient notices stack above the update card, expire after 30 seconds and can be
closed with X or `Ctrl+Shift+.`; the update stays available until separately dismissed.
Clicking a notice's body neither dismisses it nor sends the click to the terminal.
Messages are bounded to six visible lines with ellipsis. UTF-8 is decoded atomically:
malformed input cannot partially overwrite the previous notice.

Final-session completion no longer inserts blank rows or draws a second exit card
inside terminal output. The completed session keeps its output and a persistent
**Session finished** / **Process exited** header after its notice expires. Confirmation
dialogs for closing active programs remain separate modal decisions, not toasts.

## Installation and recovery

The app copies its standalone `vex-update.exe` helper into a protected, random
per-user staging directory **on a worker**, not the window thread. A two-phase
handoff passes only the parent process handle and three explicit readiness/approval/
cancellation events. The helper checks the product, package and other Vex windows
before acknowledging readiness. Only then does the app review currently running
programs and ask permission to close. Preflight failure leaves every terminal open.

After approval, the helper waits for the **whole application process** (including
PTY teardown), not just window destruction or a potentially recycled PID. Approval
wins when approval and parent exit become signalled together. Cancellation cannot
revoke an already committed transaction during application teardown.

A global mutex keyed by canonical installation directory serializes helpers across
desktop sessions. Startup briefly checks the same guard before loading terminal DLLs;
new windows are declined while that installation is being replaced. The helper checks
for other Vex windows before readiness, and for remaining Vex/ConPTY processes after
parent exit. A read-only MSI lease prevents package modification/deletion from
acknowledgement through completion of Windows Installer.

The helper rechecks the package, then invokes the system `msiexec.exe` with passive
progress, no forced reboot and Restart Manager shutdown disabled. It preserves both
the registered installation directory and per-user/per-machine scope. Windows may
request elevation for a machine-wide installation. Successful installation relaunches
Vex as the original user, not as the elevated Windows Installer service.

MSI major upgrades install the new components before removing the old product,
**inside** the install transaction (`afterInstallExecute`). Component GUIDs must remain
stable across versions. This avoids tearing down the old product's registration
before new-file installation has succeeded; failure rolls back the new installation. Errors keep an
installer log in staging and relaunch the existing application. If Windows returns
3010, Vex asks for a Windows restart instead of launching a potentially mixed version.
Other early helper failures are reported explicitly. Settings remain outside the
installation payload and are not reset by an update.

The exact successfully installed MSI and its feed are **retained as recovery data**.
Windows Installer can require the embedded cabinet for repair, uninstall or rollback;
deleting its source immediately can break those operations. A future check removes
inactive staging directories with the exact generated random-name format after seven
days, but never evicts a directory registered
as the installed product's source. Registry-query uncertainty also prevents eviction.

Ordinary shutdown cancels HTTP, hashing and uncommitted preflight, joins the worker,
and removes uninstalled staging files. Cancelling preflight closes only that helper;
a committed Windows Installer process is never terminated. Retrying a transient busy/
permissions/sharing preflight reuses the verified download instead of fetching it again.

## Release contract and trust

`app/tools/build-installer.ps1` creates `vex-update.txt` only after stable ZIP/MSI
payload, checksum and administrative-extraction verification. The release workflow
uploads versioned artifacts before the feed and publishes new releases only after
all assets are present. Preview builds do not create update feeds.

The bounded ASCII wire format is:

```text
VEX-UPDATE-1
X.Y.Z
<MSI byte count>
<64 lowercase SHA-256 hex digits>
```

Every line, including the last, ends in LF. The parser rejects extra bytes, malformed
versions, leading zeroes, prerelease/path/argument strings, invalid hashes and sizes
outside 1–512 MiB. Versions use Windows Installer's numeric limits (255.255.65535)
and are compared numerically; equal/older releases are never installed.

Downloads are derived from a fixed repository and version, not URLs or commands in
the feed. WinHTTP validates TLS certificates and forbids HTTPS-to-HTTP redirects.
Transfers have per-operation timeouts, a ten-minute streaming deadline and hard byte
limits. Known oversized Content-Length headers are rejected before reading the body;
truncated responses and declared-size mismatches are rejected as well. Downloads stream to `update.part`; readiness is published only after exact
size, SHA-256, MSI UpgradeCode, x64 platform and ProductVersion verification. Failed
partials are removed. The helper repeats verification immediately before installation.

**Trust boundary:** GitHub's release assets over authenticated HTTPS are the source
of trust. SHA-256 detects corruption/mismatched artifacts; it is not an independent
publisher signature. This implementation does not claim Authenticode signing or
protect against a compromised GitHub release account. No API token is required.
Offline/background check errors remain unobtrusive; explicit checks and failed
downloads expose their errors with a retry action. A stable release without a feed
reports “Update feed unavailable” on manual checks.

## Verification

From `app/` on Windows:

```powershell
zig fmt --check build.zig src
zig build
zig build test
python3 -m unittest discover -s tests -p 'test_*.py' -v
./tools/build-installer.ps1 -SkipBuild -SkipTests
./zig-out/bin/vex-conpty-fixture.exe update-package ./publish/Vex-Native-X.Y.Z-win-x64.msi ./publish/vex-update.txt
```

The native suite includes strict feed/version parsing, protected staging ACLs,
size/hash/non-MSI rejection, portable installation detection, cancellation without
network access and cancellation of the restart confirmation without losing sessions.
Python packaging tests verify helper inclusion and feed/artifact consistency.

Local interactive desktop checks (not hosted CI coverage):

```powershell
./tools/test-updates.ps1
./tools/test-updates.ps1 -Light
```

These isolate settings, disable real downloads/installations, exercise all banner
states, capture the actual GPU-rendered scene, dismiss by mouse and check that an
idle banner does not continuously repaint. They also check all notice severities,
stacking, X hit targets, the window-thread OS hand/arrow cursor, hover without new text
rasterization, replaced-card hover, animation completion, Unicode/maximum-length text,
atomic rejection of malformed UTF-8 and a real final shell exit. They do not move the
user's desktop cursor. Captures go to `app/publish/update-ui/`.

To leave a safe mock open in the installed application:

```powershell
./tools/show-update-preview.ps1 -WithNotice
```

It uses isolated settings and disables real downloads/installations; the GPU capture
is saved to `app/publish/update-preview/preview.png`.

For an optional real-network check, supply a feed matching an **already published**
MSI (version, size and hash from that release):

```powershell
./zig-out/bin/vex-conpty-fixture.exe update-download <published-release-feed.txt>
```

This downloads/verifies the real package, rejects a deliberately too-small byte
limit and cancels an active transfer. It never installs a product. A locally rebuilt
MSI's feed normally will not match the bytes of the published MSI.

Repeatable local installation checks require WiX/.NET build tools and opt into
installing **test-only products**, with a fresh UpgradeCode and no real Vex shortcuts:

```powershell
./tools/test-update-installer.ps1 -WhatIf
./tools/test-update-installer.ps1
```

They use the production handoff/helper with isolated identity, exercise another open
window, read-lease protection, cancellation, corruption, immediate parent exit,
Unicode/spaced paths, an actual MSI upgrade and a deliberately failed transaction.
Each package contains different executable/helper bytes. A deferred failing custom
action runs after file copies have actually executed, not merely after the script was
scheduled. Rollback checks both binary hashes and Windows Installer product registration;
cleanup verifies that the resulting installation can be uninstalled. Diagnostics
remain under `app/publish/update-installer-fixture-*/`. Never repurpose the real Vex
UpgradeCode for an isolated test.

The native suite also runs a fixture-only loopback HTTP transport (not compiled into
either shipping executable) for 404/503, chunked/truncated/oversized bodies, failing
writes and cross-thread cancellation while headers/body are blocked. Real GitHub
HTTPS redirects and verification are checked separately by `update-download`.
Windows 10 compatibility is based on the existing renderer and supported Win32 APIs;
a Windows 11 desktop pass alone is not a Windows 10 runtime pass.
