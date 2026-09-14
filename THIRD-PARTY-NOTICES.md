# Third-party notices

Vex is distributed under the GNU General Public License v3.0 (see [LICENSE](LICENSE)).
It bundles or depends on the following third-party components, which remain under
their own licenses. Full license texts are reproduced below where required.

## Bundled binaries

### libghostty (ghostty-vt)

- **Files:** `Vendor/libghostty/ghostty-vt.dll`
- **Source:** https://github.com/ghostty-org/ghostty
- **License:** MIT
- **Copyright:** Copyright (c) 2024 Mitchell Hashimoto, Ghostty contributors

Used as the VT stream emulator behind `Vex.Libghostty`. The DLL is checked into
this repository and redistributed unmodified.

### AvalonEdit syntax highlighting definitions

- **Files:** `windows/Vex.App/Highlighting/*.xshd`
- **Source:** https://github.com/icsharpcode/AvalonEdit
- **License:** MIT
- **Copyright:** Copyright (c) AvalonEdit Contributors

The One Dark and One Light `.xshd` definitions are derived from the samples
shipped with AvalonEdit and adapted to Vex's editor themes.

## NuGet dependencies

| Package | License | Used by |
|---|---|---|
| AvalonEdit | MIT | `Vex.App` |
| Microsoft.NET.Test.Sdk | MIT | `Vex.Libghostty.Tests` |
| xunit, xunit.core, xunit.runner.visualstudio | Apache-2.0 | `Vex.Libghostty.Tests` |

## npm dependencies

The landing page in `web/` bundles the following runtime dependencies, all MIT:

| Package | License |
|---|---|
| react, react-dom | MIT |
| @tanstack/react-router | MIT |
| tailwindcss, @tailwindcss/vite | MIT |
| vite-plus | MIT |

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

## Apache License 2.0

Applies to xunit and its runner packages. Full text:
https://www.apache.org/licenses/LICENSE-2.0

```text
Licensed under the Apache License, Version 2.0 (the "License");
you may not use these files except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```
