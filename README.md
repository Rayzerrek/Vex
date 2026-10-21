# Vex

A native Windows terminal with tabs, split panes and Windows/WSL shells.
Written in Zig and C/C++, using Win32, ConPTY, libghostty-vt and D3D12.

## Install

Download from [Releases](https://github.com/Rayzerrek/Vex/releases):

- `Vex-Native-<version>-win-x64.msi` — Windows installer.
- `Vex-Native-<version>-win-x64.zip` — extract the entire archive and run
  `Vex.exe`. Keep the included DLLs and `OpenConsole.exe` beside it.

Open the command palette with **Ctrl+Shift+P**, or preferences with **Ctrl+,**.
Detailed user guides live on the [website](web/README.md).

Upgrading from WPF? Read the [native migration notes](docs/releases/2.0.0.md).
Old `Vex.App.exe` packages and JSON settings are not the current application.

## Development

- [`app/`](app/README.md) — build, run and package the Windows application.
- [`web/`](web/README.md) — website and detailed user documentation.
- [`docs/`](docs/README.md) — architecture, configuration and verification.
- [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md) · [Changelog](CHANGELOG.md)

## License

[GPL-3.0](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md).
