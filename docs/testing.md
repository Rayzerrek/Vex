# Testing

## Application

From the repository root on Linux/WSL:

```sh
nix develop
cd app
zig fmt --check build.zig src
zig build test-portable
zig build
zig build test-build
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 tools/package.py
python3 tools/package.py --verify
```

Repeat application/test builds with `-Doptimize=Debug` when changing application
code. Zig tests live beside implementations and are collected by
`src/zig/main.zig`; Python tests cover packaging. `test-portable` runs host-side
snapshot/feed regressions without Windows DLLs.

On Windows, run `zig build test` from `app/`, in both Debug and ReleaseSafe.
Alternatively, copy `app/zig-out/bin/` to a local Windows filesystem and run
`vex-native-tests.exe` with adjacent `ghostty-vt.dll`, `conpty.dll`,
`OpenConsole.exe` and `vex-conpty-fixture.exe`. UNC-backed temporary directories
can fail Windows file-locking tests. A Linux cross-build is **not** a Windows test pass.

## Desktop validation

Use a real Windows desktop for rendering, mixed DPI, clipboard/TUI behavior,
tabs/splits, shell selection, overlays and disabled animations. Check both themes.
Opening tab overview must not resize PTYs. Copy checks should include Unicode,
blank/wrapped lines, repeated copies, cuts/deletes and background applications.

From the repository root on Windows:

```powershell
./app/tools/test-recovery.ps1
./app/tools/test-recovery.ps1 -Shell wsl -Distribution NixOS -Cycles 30 -BatchSize 5
# Use your installed distribution; optionally add -Warp for software rendering.
```

With Git Bash installed, verify idle-shell closure and active-command protection:

```powershell
./app/tools/test-close-confirmation.ps1
```

This local desktop check covers Git's shell trampoline and MSYS commands whose
fork parent has already exited. It uses isolated settings and software rendering.

The recovery smoke uses isolated settings and checks tab churn, pane-local
transport failures, stale events, toasts, renderer recovery and delayed teardown.
It is local interactive coverage, not hosted CI or a guarantee for every driver/WSL version.

### Path picker acceptance cases

Use an isolated Vex window and configuration on Windows, with a real WSL
session. These are local desktop checks, not hosted CI coverage:

- Open from a reported WSL cwd; without OSC 7, the fallback must be the
  distribution root, never the Windows user's profile directory.
- Compare empty/missing/unreadable roots with a readable directory containing
  unreadable children. Children must not erase readable results. Skipped paths
  and index limits must have distinct notices.
- Navigate drive, UNC-share and distribution roots: Parent/empty Backspace are
  no-ops at the root. Test absolute Linux paths, relative paths, typed/pasted UNC
  paths, and pasted directory prefixes longer than the search field.
- Check long paths, Polish characters, emoji, spaces, apostrophes and WSL's
  escaped Windows-reserved filename characters (`:`, `?`, `*`, `"`, `<`, `>`, `|`).
  Labels/search use Linux spelling; insertion must decode and quote that spelling,
  including Nushell/PowerShell in WSL when process metadata identifies the shell.
  In cmd, `%`/`!` paths require an explicit raw override instead of unsafe quoting.
  Control-character names and Linux backslash names are intentionally omitted;
  literal private-use names colliding with WSL's escapes are not representable.
- Include directory symlink/junction cycles: indexing must not recurse through
  links. Explicitly opening a supported directory link may index its target.
- Exercise Home/End, paging, filtering to zero results, Backspace, Enter on an
  empty result, Escape, outside clicks, and rapid navigation while indexing.
  Neither stale results nor old hover positions may replace the current search.

Zig regressions cover roots, escaping, Unicode boundaries, stale revisions,
allocation rollback, symlink cycles and bounded indexing. Limits are 16,384
entries, 8 MiB of indexed names (including display spelling), and 50 matches.
A directory exactly at a limit must not claim that paths were omitted.

## Performance

From `app/`, `zig build bench` runs ReleaseSafe CPU microbenchmarks for allocation,
copy-feedback snapshots and feed filtering. These do not measure ConPTY, VT
parsing, rendering or end-to-end input latency.

Set `VEX_NATIVE_METRICS` to an absolute log path before launching on Windows for
application/font/renderer readiness, first-frame and first-PTY-output timings.
Times begin inside `main`, not at process creation. Compare alternating matching
baseline/candidate runs after warmup; report medians and spread.
See [local performance results](reviews/native-performance.md).

## Packaging

See [app/README.md](../app/README.md#package) for ZIP/MSI commands. MSI validation
compares real cabinet extraction with staging; it does not replace installation,
Windows runtime or desktop checks.

## Website

```sh
pnpm --dir web install --frozen-lockfile
pnpm --dir web run check
pnpm --dir web run test
pnpm --dir web run build
```

Manually check navigation/search, code copying, mobile layout, both themes,
keyboard access, reduced motion and pages without JavaScript.

## CI scope

- [`app.yml`](../.github/workflows/app.yml): formatting, Python/portable Zig tests,
  Debug/ReleaseSafe cross-builds and compiled Windows tests.
- [`web.yml`](../.github/workflows/web.yml): formatting, lint, types, unit tests
  and prerendered production build.
- [`release.yml`](../.github/workflows/release.yml): application checks followed
  by verified ZIP/MSI packaging. Preview runs create artifacts only; stable
  tags must match `app/VERSION`.

Review reports record individual runs, not ongoing CI coverage.
