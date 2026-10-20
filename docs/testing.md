# Testing

## Application checks

Use the pinned Zig/Python toolchain on Linux/WSL:

```sh
nix develop
cd app
zig fmt --check build.zig src
zig build
zig build test-build
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 tools/package.py
python3 tools/package.py --verify
```

`zig build test-build` compiles the Zig tests and C++ ConPTY/motion fixtures.
Most behavior tests are embedded beside the Zig implementation and collected
through `src/zig/main.zig`; terminal regressions also live in
`src/zig/terminal/terminal_tests.zig`. Python tests in `app/tests/` exercise
portable payloads, checksums, versions, preview labels and tampering detection.

### Run Windows tests

On a Windows development machine, use `zig build test` from `app/`.
Alternatively, copy the cross-built `app/zig-out/bin/` directory to Windows and
run `vex-native-tests.exe`. Keep `ghostty-vt.dll`, `conpty.dll`,
`OpenConsole.exe` and `vex-conpty-fixture.exe` adjacent to it.

Linux cross-compilation does **not** mean that Windows runtime tests passed.

### Desktop checks

GPU rendering, mixed DPI, Unicode/wide glyphs, clipboard interactions, tabs,
split panes, shell selection and overlays need a real Windows desktop. Check
both themes, process exit, cancellation and disabled system animations.
For tab overview, confirm that opening/closing previews does not resize PTYs.
Desktop harnesses and benchmarks are local tools, not repository CI coverage.
See [app/README.md](../app/README.md) for feature-specific smoke tests.

## Packaging

Portable ZIP staging and verification require only Python. MSI packaging runs
on Windows with .NET/WiX:

```powershell
./app/tools/build-installer.ps1
# Reuse already tested cross-build output:
./app/tools/build-installer.ps1 -SkipBuild -SkipTests
```

The script verifies checksums and compares real administratively extracted MSI
files against staging. Artifacts land in `app/publish/`. Packaging does not
replace Windows unit tests or desktop smoke tests.

## Website

```sh
pnpm --dir web install --frozen-lockfile
pnpm --dir web run check
pnpm --dir web run build
```

Check screenshot paths/alt text, navigation and the advertised license by eye.

## CI

- `.github/workflows/native.yml`: formatting and packaging tests, Linux
  Debug/ReleaseSafe cross-builds, then compiled Zig test execution on Windows.
- `.github/workflows/web.yml`: website checks and production build.
- `.github/workflows/release.yml`: reuses application CI and packages tested
  ReleaseSafe output. Stable tags must match `app/VERSION`; preview runs create
  branch artifacts without publishing a release.

The legacy WPF test suite and workflow were removed with the old implementation.
Historical test descriptions are available in Git history.
