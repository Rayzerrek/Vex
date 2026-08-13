# libghostty-vt spike

Throwaway smoke test of the `libghostty-vt` C API as a replacement for
XtermSharp: P/Invoke bindings, VT feed, render-state dump, live ConPTY.

## Build the DLL (one-time)

Pin: `ghostty-org/ghostty@f64f4aca2c29b554d111b36c3d946a9bddd159ff`
Toolchain: Zig 0.16.0 (`https://ziglang.org/download/0.16.0/zig-x86_64-windows-0.16.0.zip`)

```powershell
git clone --filter=blob:none --no-checkout https://github.com/ghostty-org/ghostty.git ghostty-src
git -C ghostty-src checkout f64f4aca2c29b554d111b36c3d946a9bddd159ff
cd ghostty-src
zig build -Demit-lib-vt -Doptimize=ReleaseFast -Dtarget=x86_64-windows-gnu
Copy-Item zig-out\bin\ghostty-vt.dll ..\spike-libghostty\
```

No MSVC needed: the `x86_64-windows-gnu` target uses Zig's bundled
mingw-w64 libc and libc++. The DLL is not committed (see `.gitignore`);
the consuming side only needs `ghostty-vt.dll` next to the csproj.

## Run

```powershell
dotnet run --project windows\spike-libghostty
```

## Status

Phase 1 (in-memory VT): effects, write_pty, colors, cursor, row/cell
iteration all verified. Known open item: `ghostty_render_state_row_cells_get`
crashes on the first cell query — see `Program.cs` for the current state.
