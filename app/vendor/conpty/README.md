# Microsoft Windows Console ConPTY

Unmodified, Microsoft-signed Windows x64 binaries from the MIT-licensed
[`Microsoft.Windows.Console.ConPTY`](https://www.nuget.org/packages/Microsoft.Windows.Console.ConPTY/1.24.261001001)
NuGet package, version **1.24.261001001**. Requires Windows 10 build 17763 or later.
Upstream source: <https://github.com/microsoft/terminal>.

The inbox ConPTY on Windows 10 build 19045 consumes ED3 (`CSI 3 J`) without
forwarding it. That leaves the terminal's scrollback intact even when a child
explicitly requests its removal. This pair forwards ED3, as well as ordinary
screen erases, without guessing intent from redraws.

Both files must stay beside Vex and beside the compiled native tests. Vex loads
this exact adjacent DLL (not a PATH/current-directory match) and requires the
adjacent host, rather than silently reverting to the system host.

## Updating

Download the pinned package from:

```text
https://api.nuget.org/v3-flatcontainer/microsoft.windows.console.conpty/1.24.261001001/microsoft.windows.console.conpty.1.24.261001001.nupkg
```

Extract these entries without modifying or re-signing them:

- `runtimes/win-x64/native/conpty.dll` → `conpty.dll`
- `build/native/runtimes/x64/OpenConsole.exe` → `OpenConsole.exe`

Verify both Authenticode signatures as Microsoft Corporation on Windows, retain
the upstream MIT license, update the version and hashes here and in the root
third-party notices, and run the native tests and package checks. The headless
ConPTY fixture checks full clears, screen-only clears, history-only clears and
alternate-screen isolation through the real child-process transport.

SHA-256:

```text
4d6aaddc1d2385c9f5897df28f33879f699f8f2783315d5204cf3d8c3616ac5f  microsoft.windows.console.conpty.1.24.261001001.nupkg
5afea6c9480e2c7dfd195376dd10996b0ae25cdc6f014e6247fd8aedc8c34852  conpty.dll
6f8e68dec4e8e5e15a54ecc8afa127b0e62139edc004ffbb4892125ff01d5737  OpenConsole.exe
```
