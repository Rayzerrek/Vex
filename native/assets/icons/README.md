# Supplemental brand paths

SVG snapshots from [Simple Icons](https://simpleicons.org/), retrieved via
`https://cdn.simpleicons.org/{slug}` for `zig`, `dotnet`, `lua`, `uv`, and `ollama`.
Simple Icons publishes its collection under CC0; brand names and marks remain
subject to their respective owners' trademark and usage guidelines.

The other bundled brand paths come from the existing curated snapshot in
`windows/Vex.App/Model/AppIconCatalog.generated.cs`. The native application does
not load WPF or access the network at runtime.

Regenerate the native geometry after changing a source path or Zig icon IDs:

```powershell
powershell -NoProfile -File tools/Generate-AppIconGeometry.ps1
powershell -NoProfile -File tools/Generate-AppIconGeometry.ps1 -Check
```

The generator matches paths by slug and orders them by the Zig `GlyphId` enum;
there is no separately maintained numeric C++ drawing switch. Paths are flattened
at generation time (0.01 source-unit tolerance), rasterized into the shared glyph
atlas on cache misses, and reused at the current DPI. Nine entries without a
bundled path have explicit monograms rather than unrelated brand logos.

This remains the existing **73-entry subset**, not a universal Simple Icons
catalog or an executable-icon extractor. Expanding that scope requires replacing
the 8-bit glyph identity in the native ABI and the curated resolver, not merely
adding more drawing cases.
