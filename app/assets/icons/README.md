# Supplemental brand paths

SVG snapshots from [Simple Icons](https://simpleicons.org/), retrieved via
`https://cdn.simpleicons.org/{slug}` for `zig`, `dotnet`, `lua`, `uv`, and `ollama`.
Simple Icons publishes its collection under CC0; brand names and marks remain
subject to their respective owners' trademark and usage guidelines.

Tabs and pane headers resolve these icons automatically from the foreground
process. Local scripts retain their runtime icon (for example, `python3 server.py`
and `node server.js`), while recognized CLI tools and installed npm packages can
supply a more specific identity. Versioned Python binaries such as `python3.13`
and `python3.13t` use the Python icon too. Unknown packages and Python modules
keep deterministic letter badges; explicit OSC 7501 application identity takes
precedence. No shell configuration or executable-icon lookup is required.

The other bundled brand paths were carried over from the previous WPF app's
curated icon snapshot (available in Git history). The application does not load
WPF or access the network at runtime.

Native geometry is checked in at `src/windows/renderer/app_icon_geometry.generated.h`.
Regeneration tooling is maintained locally outside the repository; changing a
source path or Zig icon IDs requires updating that generated snapshot.

The generator matches paths by slug and orders them by the Zig `GlyphId` enum;
there is no separately maintained numeric C++ drawing switch. Paths are flattened
at generation time (0.01 source-unit tolerance), rasterized into the shared glyph
atlas on cache misses, and reused at the current DPI. The same monochrome paths
serve light and dark themes: `icon_contrast.h` adapts the drawing color to the
actual background when needed to maintain at least 3:1 contrast, without swapping
icon sets. Pi uses the three-color pixel mark defined in `pi_icon.h`, shared by
chrome and dialogs; each color follows the same background-contrast rule.
Nine entries without a
bundled path have explicit monograms rather than unrelated brand logos.

This remains the existing **73-entry subset**, not a universal Simple Icons
catalog or an executable-icon extractor. Expanding that scope requires replacing
the 8-bit glyph identity in the native ABI and the curated resolver, not merely
adding more drawing cases.
