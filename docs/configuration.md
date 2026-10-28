# Configuration

Vex reads `%LOCALAPPDATA%\Vex\init.lua`, or the absolute file path in
`VEX_CONFIG`. A missing file uses defaults without creating anything.

```lua
vex.config.theme = "Vex Obsidian"
vex.config.appearance = "theme" -- infer from theme; or "dark" / "light"
vex.config.ui_density = "comfortable" -- "compact", "comfortable", "spacious"
vex.config.font_family = "Cascadia Mono"
vex.config.font_size = 14 -- integer, 9–32
vex.config.cursor_blink = true
vex.config.copy_on_select = true
vex.config.confirm_close = true
vex.config.default_shell = "startup"
```

Use exact theme names from Theme Studio. Shell IDs are `startup`, `cmd`,
`powershell`, `pwsh`, `nu`, `gitbash`, `wsl`, or a saved `custom:<id>`.
`startup` respects the CLI launch choice; changes affect new tabs/splits only.
Explicit appearance overrides an incompatible theme with that mode's default.
Unknown keys and invalid values are errors.

## Built-in themes

The twelve Vex palettes pair a tinted terminal background with coordinated window
chrome, selection, cursor and ANSI colors. Existing theme names remain valid.

| Dark | Character | Light | Character |
| --- | --- | --- | --- |
| Vex Obsidian (default) | Smoked quartz / iris | Vex Alabaster (default) | Warm porcelain / ink blue |
| Vex Tidal | Deep petrol / sea glass | Vex Linen | Natural linen / toasted amber |
| Vex Grove | Evergreen / soft jade | Vex Mist | Pearl grey / coastal blue |
| Vex Dusk | Aubergine / rose quartz | Vex Petal | Blush porcelain / wild rose |
| Vex Ember | Warm charcoal / burnished copper | Vex Apricot | Warm clay / terracotta ink |
| Vex Glacier | Blue graphite / glacial silver | Vex Sage | Celadon / eucalyptus ink |

Pane headers and floating controls use a restrained glass-like finish: a soft tonal
gradient, a fine rim and a light-catching upper edge. The material is drawn by
Vex's GPU renderer on both Windows 10 and 11, without depending on Mica or Acrylic.
Terminal backgrounds stay opaque so text and full-screen applications remain
readable. Theme Studio (`Ctrl+Shift+M`) previews changes; `Tab` switches between
the dark and light collections.

## Interface density

Interface density is available in **Settings → Appearance** and through the
**Interface Density** commands in the command palette. Compact reduces tab and
pane spacing and joins split edges with thin separators; Comfortable preserves the original layout; Spacious adds room
around the workspace and larger pane headers. Changes apply immediately and are
saved with other preferences. They do not change terminal font size or Windows
caption-button sizes. Existing terminal sessions and scrollback remain open.

## Save and reload

- **Configuration: Reload init.lua** in the command palette reloads preferences
  and profiles. Failed reloads retain the last valid values; startup errors use
  defaults and report diagnostics.
- Settings replaces `init.lua` atomically after saving its exact previous source
  to `init.backup.lua`. This single backup rotates on changed saves; identical
  saves preserve it. Keep a separate copy of hand-written Lua for longer history.
- Failed backup/save rolls live preferences back; running sessions remain open.
- Modules are untouched. There is no file polling or background Lua execution.

Custom profiles live in `init.shells.lua` beside preferences. Add them through
**Settings → General → Add custom shell…**; adding a profile never rewrites
`init.lua`. Settings regenerates the managed profile file, so its comments and
expressions are not preserved. Custom configuration paths use
`<name>.backup.lua` and `<name>.shells.lua` companions.

## Lua limits

Lua 5.4.8 supports expressions, functions and local `require` modules.
Evaluation is limited to 64 KiB per source file, 8 MiB VM memory and roughly
one million instructions. Process/IO/debug/coroutine, native-module,
dynamic-loader and protected-call APIs are unavailable. This is local user
configuration, not an untrusted-code sandbox.

See the [website guides](../web/README.md) for detailed settings and recovery
steps. Legacy JSON settings are not read; see [migration notes](releases/2.0.0.md).
