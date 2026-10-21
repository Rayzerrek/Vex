# Third-party notices

Vex is distributed under [GPL-3.0](LICENSE). Included components retain their
own licenses.

## Application

| Component | Files | License / source |
| --- | --- | --- |
| libghostty-vt | `app/vendor/libghostty/ghostty-vt.dll`, `app/vendor/include/ghostty/` | MIT — [Ghostty](https://github.com/ghostty-org/ghostty) |
| Microsoft Windows Console ConPTY 1.24.261001001 | `app/vendor/conpty/conpty.dll`, `OpenConsole.exe` | MIT — [Microsoft Terminal](https://github.com/microsoft/terminal) |
| Lua 5.4.8 | `app/vendor/lua/src/`, statically linked | MIT — [Lua](https://www.lua.org/ftp/lua-5.4.8.tar.gz) |

Copyright notices:

- Ghostty: Copyright (c) 2024 Mitchell Hashimoto, Ghostty contributors.
- ConPTY: Copyright (c) Microsoft Corporation. All rights reserved.
- Lua: Copyright (c) 1994-2025 Lua.org, PUC-Rio.

The Ghostty DLL and matched ConPTY DLL/host pair are redistributed unmodified.
See [ConPTY provenance and checksums](app/vendor/conpty/README.md), its
[license](app/vendor/conpty/LICENSE), and the [Lua license](app/vendor/lua/LICENSE).
Application icon sources and attribution are listed in
[app/assets/icons/README.md](app/assets/icons/README.md).

## Website

| Component | License | Use |
| --- | --- | --- |
| react, react-dom | MIT | Website runtime |
| tailwindcss, @tailwindcss/vite | MIT | Generated CSS and build tooling |
| vite, vitest, eslint, prettier | MIT | Development/build tooling |
| IBM Plex Sans, IBM Plex Mono (Fontsource) | SIL OFL-1.1 | Locally bundled web fonts |

Font copyright notices and full license texts ship with the website:
[IBM Plex Sans](web/public/licenses/ibm-plex-sans.txt) and
[IBM Plex Mono](web/public/licenses/ibm-plex-mono.txt).
WiX/.NET are installer build tools, not dependencies of the installed application.

## MIT License

Applies to the components marked MIT above.

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
