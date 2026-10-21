# Configuration

The native application reads `%LOCALAPPDATA%\Vex\init.lua`. Set `VEX_CONFIG` to
an absolute path to use a different file. A missing file is normal; Vex runs
with defaults until settings are saved.

```lua
vex.config.theme = "Vex Obsidian"
vex.config.appearance = "dark" -- "light" or "theme" are also supported
vex.config.font_family = "Cascadia Mono"
vex.config.font_size = 14
vex.config.cursor_blink = true
vex.config.copy_on_select = true
vex.config.confirm_close = true
vex.config.default_shell = "startup"
```

`appearance` is optional and defaults to `"theme"`, inferring the mode from the
named theme. Explicit `"dark"` / `"light"` overrides a theme from the opposite
collection with the default theme for that mode. UI saves record both the theme
and its current appearance.

Use **Configuration: Reload init.lua** in the command palette to reload without
restarting. Failed reloads retain the previous valid settings and report an
error in a nonmodal toast. Saving preferences through the UI replaces the
configuration file with the current values, first atomically saving its previous
contents to `init.backup.lua` (or `<custom-name>.backup.lua` for `VEX_CONFIG`).
Identical saves leave the backup untouched. This is a single previous-version
backup, not a history: copy hand-written Lua elsewhere if you want to retain it
across multiple changed saves. Modules are not modified. If backup/save fails,
the GUI returns to the last committed preferences and running sessions remain open.

Custom shell profiles are managed separately in the adjacent shells Lua file;
adding a shell does not rewrite hand-written `init.lua` configuration.

See the application guide for the full references:

- [Lua settings, limits and reload behavior](../app/README.md#lua-configuration)
- [Custom shells and Windows/WSL launch behavior](../app/README.md#custom-shells)
- [Shell selection and keyboard controls](../app/README.md#shells-and-controls)

The previous WPF JSON configuration and session formats are not the current
application's configuration API. Their documentation remains in Git history.
