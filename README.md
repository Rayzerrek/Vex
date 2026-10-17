# Vex

Vex is a native Windows terminal for working with several shells in one place.
Keep projects in separate tabs, split a tab into panes, and move between your
editor, development server and command line without juggling terminal windows.

Use PowerShell, Command Prompt, Nushell, Git Bash or WSL — whichever is installed
on your machine. Each terminal session runs independently, so you can work in
Windows and Linux shells side by side.

## A workspace for your command line

- **Tabs and split panes.** Give each task its own tab, or keep related sessions
  visible next to each other with horizontal and vertical splits.
- **Your choice of shell.** Open a tab from the shell picker and choose a default
  shell for new sessions in Settings.
- **Commands within reach.** Open the command palette to find actions and their
  keyboard shortcuts. Use the tab overview to switch between running tasks.
- **Make it comfortable.** Choose a light or dark theme, set your terminal font
  and text size, and turn cursor blinking on or off. Preferences save automatically.

Vex uses Windows' ConPTY for shell sessions, Ghostty's terminal emulation library
and a GPU-accelerated D3D12 renderer. It is a native desktop application, without
Electron or a required .NET runtime.

## Get Vex

Download a Windows x64 package from [Releases](https://github.com/Rayzerrek/Vex/releases):

- **Installer** (`Vex-Native-<version>-win-x64.msi`) — installs Vex with Start Menu
  and desktop shortcuts.
- **Portable ZIP** (`Vex-Native-<version>-win-x64.zip`) — extract the whole archive
  into a folder and run `Vex.exe`. Keep the included DLLs, `OpenConsole.exe` and
  `ShellIntegration` folder beside the executable.

Requires **Windows 10 version 1809 or later, or Windows 11**, on x64.
Shells and WSL distributions need to be installed separately.

Older packages named `Vex-<version>` and the `Vex.App.exe` executable are from
Vex's previous WPF implementation, not the current native app.

## Find your way around

Start with a shell, open another tab when you need a separate task, or split the
current tab to keep two sessions in view. The command palette is a good place to
find an action without memorizing every shortcut.

| Action | Shortcut |
| --- | --- |
| Open command palette | Ctrl+Shift+P |
| New tab | Ctrl+Shift+T |
| Close tab | Ctrl+Shift+W |
| Next / previous tab | Ctrl+Tab / Ctrl+Shift+Tab |
| Split right / down | Ctrl+Shift+R / Ctrl+Shift+D |
| Choose a theme | Ctrl+Shift+M |
| Open tab overview | Ctrl+Shift+Space |
| Complete a path | Ctrl+Shift+F |

## Development

The current application lives in [`native/`](native/). See the
[native development guide](native/README.md) for building, running tests,
command-line shell options and packaging releases. The pinned toolchain supports
cross-compiling from Linux or WSL; running the application requires Windows.

The repository also contains the previous WPF implementation in `windows/` and
the website in `web/`.

## License

Vex is licensed under [GPL-3.0](LICENSE). See
[third-party notices](THIRD-PARTY-NOTICES.md) for Ghostty and other included components.
