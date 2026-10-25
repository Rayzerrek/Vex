# Application development

Windows x64; Zig **0.15.2** (see `.zig-version`) and Python **3.11+**.
Builds default to ReleaseSafe; Debug is supported. ReleaseFast/ReleaseSmall
are rejected because runtime safety is required.

## Build and run

From the repository root on Linux/WSL:

```sh
nix develop
cd app
zig build
zig build test-build
```

Copy `zig-out/bin/` to a local Windows directory. Run `vex-native.exe`, or
`vex-native-tests.exe` with the adjacent DLLs and `vex-conpty-fixture.exe`.
A Linux cross-build is not a Windows test pass.

On Windows, from `app/` with the pinned Zig installed:

```powershell
zig build
zig build test
zig build run
```

Launch options go after `--`:

```powershell
zig build run -- --shell pwsh
zig build run -- --shell wsl --distribution Ubuntu
zig build run -- --program 'C:\Windows\System32\cmd.exe' --arguments /d
```

See [testing](../docs/testing.md) for the complete checks and desktop validation.

## Terminal images

Kitty Graphics direct RGB/RGBA/PNG images use generation-cached D3D12 textures.
File/shared-memory transfers and Unicode placeholders are not supported.
Interactive graphics checks and capture tooling are maintained separately.
See [Kitty Graphics](../docs/kitty-graphics.md) for limits and verification.

## Source map

- `src/zig/`: application, shell, terminal and workspace models; Windows adapters.
- `src/windows/`: Win32 window/dialogs, renderer, ConPTY and process probes.
- `src/lua/`: configuration bridge to vendored Lua.
- `tests/`: Python packaging tests and C++ fixtures; Zig tests live beside code.
- `tools/`, `packages/windows/`: ZIP/MSI packaging and maintenance tools.
- `vendor/`, `assets/`: bundled dependencies and licensed resources.

Build output lives in `zig-out/` and `.zig-cache/`; packages in `publish/`.
Generated shaders are checked in. Regenerate on Windows with
`python3 tools/compile_terminal_shaders.py`.
See [architecture](../docs/architecture.md), [configuration](../docs/configuration.md)
and [native updates](../docs/updates.md).

## Package

From `app/`, using only Python:

```sh
python3 tools/package.py
python3 tools/package.py --verify
```

This stages `publish/app/` (including the standalone updater) and creates a ZIP
plus SHA-256 checksum. Test executables and debug symbols are excluded. Shell
integration is embedded.

For ZIP + MSI, from the repository root on Windows with Python, the pinned Zig
and .NET SDK 10 (WiX is restored by the project):

```powershell
./app/tools/build-installer.ps1
# Reuse binaries whose Windows tests have already passed:
./app/tools/build-installer.ps1 -SkipBuild -SkipTests
```

The script verifies checksums and compares the real MSI cabinet against staging
using administrative extraction; it does not install the application.

## CI and releases

[`app.yml`](../.github/workflows/app.yml) runs portable checks, cross-builds
Debug/ReleaseSafe and executes both Windows test configurations.
[`release.yml`](../.github/workflows/release.yml) reuses those checks, packages
tested ReleaseSafe binaries and publishes matching `vX.Y.Z` tags from `VERSION`.
Release notes live in `docs/releases/<version>.md` when needed.

Create branch-only preview artifacts:

```sh
gh workflow run release.yml --ref <branch> -f mode=preview
```

ZIP/MSI, checksums and `build-info.json` remain in Actions artifacts for 14 days;
no tag or GitHub Release is created. Preview MSI shares the stable install
location: use an isolated machine.

Retry an existing release tag with a workflow revision containing this pipeline:

```sh
gh workflow run release.yml --ref main -f mode=release -f tag=vX.Y.Z
```
