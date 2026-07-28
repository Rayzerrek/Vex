# Vendored XtermSharp

Source: https://github.com/migueldeicaza/XtermSharp (MIT, see LICENSE).

Vendored as plain files (same convention as `Vendor/STTextView`) because the
only build published to NuGet is `1.0.0-alpha.10` from 2020, while upstream
master carries several years of emulator fixes. Vendoring also lets us patch
the core for Windows if the renderer needs it.

## Differences from upstream

- `Pty.cs` is excluded: it is a POSIX `forkpty` wrapper, and the Windows port
  uses ConPTY via `windows/Kero.Terminal` instead.
- `GlobalSuppressions.cs` and the test projects are excluded.
- `XtermSharp.csproj` retargets `netstandard2.0` → `net10.0` and references
  `NStack.Core` 1.1.1 (upstream pins 0.12.0).
- `using Rune = System.Rune;` alias added in `SelectionService.cs` and
  `InputHandlers/InputHandler.cs`: NStack's `System.Rune` is ambiguous with
  `System.Text.Rune` on modern .NET (upstream targets netstandard2.0 where
  the latter does not exist).

Import: `master` as of 2025 (check `git log` upstream before refreshing).
