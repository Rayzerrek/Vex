# Lua 5.4.8

Unmodified upstream `src/` from https://www.lua.org/ftp/lua-5.4.8.tar.gz
(released 2025-05-21).

Archive SHA-256: `4f18ddae154e793e46eeab727c59ef1c0c0c2b744e7b94219710d76f530629ae`.
License: MIT; see `LICENSE` (copied from upstream `doc/readme.html`).

`native/build.zig` compiles the VM and the configuration libraries statically;
there is no Lua installation, extra DLL, download or subprocess at runtime.
The application bridge and execution limits live in `src/app/lua_settings.c`.
The CLI interpreter, compiler and unused standard libraries are not built.
