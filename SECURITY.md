# Security policy

Only the latest stable release receives security fixes. Preview builds are for
testing, not a separate supported release line.

## Report privately

Use [GitHub private vulnerability reporting](https://github.com/Rayzerrek/Vex/security/advisories/new).
Do not open a public issue. If unavailable, use the maintainer's email on their
GitHub profile.

Include the Vex version, Windows build, impact and reproduction steps or a proof
of concept. Expect an acknowledgement within a few days; allow time for a fix
before public disclosure.

## Trust boundaries

- **Terminal output is untrusted.** Memory-safety and parser failures in Vex or
  its bundled libghostty-vt are in scope. Report upstream Ghostty bugs to
  [ghostty-org/ghostty](https://github.com/ghostty-org/ghostty) as well.
- **Clipboard writes are supported.** OSC 52 and OSC 1337 can write plain text
  without a separate confirmation. Clipboard read/query access is not enabled.
  Treat output from remote commands as capable of replacing clipboard text.
- **Links require a click.** Detected URLs open through the Windows shell on
  Ctrl+Click; terminal output must not launch programs by itself.
- **Process metadata is advisory.** Windows/WSL probes identify pane activity;
  exposing unrelated process data or crossing session ownership is in scope.
- **Lua configuration is trusted local code.** `init.lua`, its modules and the
  companion shell catalog have evaluation limits, but are not an untrusted-code
  sandbox. Do not load configuration from an untrusted source.

## Out of scope

- Attacks requiring existing code execution as your user.
- Deliberately running a malicious executable.
- Denial of service from a program you chose to run in a pane.
- Scanner output without a demonstrated impact.
