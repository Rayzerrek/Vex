# Vex

Native Windows terminal workspace: Zig, Win32, ConPTY, libghostty-vt and D3D12.
The active application lives in [`native/`](native/); `windows/` contains the
older WPF implementation, retained separately.

## Install

Download native artifacts from [Releases](https://github.com/Rayzerrek/Vex/releases):

- `Vex-Native-<version>-win-x64.zip` — extract all files and run `Vex.exe`.
- `Vex-Native-<version>-win-x64.msi` — Windows installer with Start Menu and desktop shortcuts.

Windows 10 1809+ / Windows 11, x64. No .NET runtime installation is needed.
Older `Vex-<version>` artifacts and `Vex.App.exe` belong to the WPF application.
Keep `ghostty-vt.dll` and the `ShellIntegration` directory beside the native exe.

## Development in Nix / WSL

```sh
nix develop
cd native
zig build
zig build test-build
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 tools/package.py
```

The Nix shell pins the same Zig version as CI. Windows binaries and test executables
are cross-compiled in WSL; Windows only runs the resulting files. ZIP packaging is
cross-platform. MSI packaging uses WiX/.NET on a Windows CI runner, not your workstation.

See [`native/README.md`](native/README.md) for shell options, shortcuts, Windows
build commands and packaging details. Zig runtime safety remains enabled in release
builds. Commits use conventional prefixes such as `feat:`, `fix:` and `ci:`.

## Branch preview and stable releases

```sh
# Test the branch without modifying main, creating a tag or publishing a GitHub Release:
gh workflow run release.yml --ref feat/zig-native -f mode=preview
```

The **Native packages** run provides ZIP, MSI, SHA-256 files and source provenance
as Actions artifacts. Preview filenames include the commit; artifacts expire after
14 days. Test MSI installation on an isolated Windows machine because it uses the
stable application's install location.

For stable releases, update `native/VERSION` and push a matching `vX.Y.Z` tag.
The pipeline tests Debug and ReleaseSafe, builds native packages, verifies the ZIP
and extracted MSI payload, then publishes both packages and checksums. It does not
build or publish WPF. Read-only checks and preview jobs cannot publish releases.

**Native Zig** separately runs on native pull requests and main/feature-branch pushes.
Linux cross-builds both configurations; Windows runs their compiled unit tests.
Interactive renderer tests and benchmarks are local tools, not hosted CI coverage.
Legacy WPF and web checks are path-filtered and independent.

## Structure

```text
native/              active Zig/Win32 application, tests and native packaging
windows/             legacy WPF/.NET implementation
Vendor/libghostty/   bundled terminal emulator DLL
web/                 landing page
flake.nix            Linux/WSL native development shell
```

## Licensing

[GPL-3.0](LICENSE). Third-party attribution, including Ghostty, is retained in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
