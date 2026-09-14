# Security Policy

## Supported versions

Only the latest release receives security fixes.

| Version | Supported |
|---|---|
| 1.3.x | Yes |
| < 1.3 | No |

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Report it through GitHub's [private vulnerability reporting][advisory] on the
`Rayzerrek/Vex` repository. If you cannot use that, email the maintainer at the
address on the GitHub profile.

Include:

- The version shown in Settings → About, and your Windows build.
- A description of the issue and its impact.
- Steps to reproduce, or a proof of concept.
- Any suggested mitigation.

You can expect an acknowledgement within a few days. Please allow time for a
fix to be released before disclosing the issue publicly.

[advisory]: https://github.com/Rayzerrek/Vex/security/advisories/new

## Scope

Vex starts processes and renders untrusted output, so the areas most worth
testing are:

- **Escape sequence handling.** `Vendor/libghostty/ghostty-vt.dll` parses
  arbitrary bytes from any program running in a pane. A memory-safety bug there
  is in scope; report it upstream to
  [ghostty-org/ghostty](https://github.com/ghostty-org/ghostty) as well.
- **Clipboard access.** A terminal escape sequence must not be able to read or
  overwrite your clipboard without a visible action by you.
- **URL handling.** Detected links open through the shell on `Ctrl+Click`;
  a sequence must not be able to make Vex launch something you did not click.
- **Process handling.** `Vex.Terminal` reads another process's command line
  through `ReadProcessMemory` to identify node-shimmed tools. That path must
  never expose memory from unrelated processes.
- **Parsing of untrusted files.** `settings.json` and `session.json` are read
  from disk on startup.

## Out of scope

- Anything requiring an attacker to already have code execution as your user.
- Issues that require the user to run a malicious binary deliberately.
- Denial of service from a program you chose to run in a terminal pane.
- Reports produced solely by a scanner, without a demonstrated impact.
