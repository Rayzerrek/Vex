const std = @import("std");
const palette = @import("../workspace/command_palette.zig");

pub const CommandId = enum {
    open_path_completion,
    open_theme_picker,
    toggle_dark_light,
    open_tab_peek,
    zoom_in,
    zoom_out,
    zoom_reset,
    new_tab,
    new_tab_with_shell,
    split_right,
    split_right_with_shell,
    split_down,
    split_down_with_shell,
    change_default_shell,
    toggle_focus_mode,
    rename_active_tab,
    next_tab,
    previous_tab,
    close_active,
    toggle_cursor_blink,
    open_settings,
    reload_configuration,
};

pub const CommandDef = struct {
    id: CommandId,
    title: []const u8,
    shortcut: []const u8,
};

pub const command_defs = [_]CommandDef{
    .{ .id = .open_path_completion, .title = "Path Completion: Insert File or Folder Path...", .shortcut = "Ctrl+Shift+F" },
    .{ .id = .open_theme_picker, .title = "Color Theme: Open Theme Studio...", .shortcut = "Ctrl+Shift+M" },
    .{ .id = .toggle_dark_light, .title = "Color Theme: Toggle Dark / Light Mode", .shortcut = "Tab in Studio" },
    .{ .id = .open_tab_peek, .title = "Tab Overview: Peek All Tabs", .shortcut = "Ctrl+Shift+Space" },
    .{ .id = .zoom_in, .title = "Font Size: Zoom In", .shortcut = "Ctrl++" },
    .{ .id = .zoom_out, .title = "Font Size: Zoom Out", .shortcut = "Ctrl+-" },
    .{ .id = .zoom_reset, .title = "Font Size: Reset Zoom", .shortcut = "Ctrl+0" },
    .{ .id = .new_tab, .title = "Terminal: New Tab", .shortcut = "Ctrl+Shift+T" },
    .{ .id = .new_tab_with_shell, .title = "Terminal: New Tab with Shell...", .shortcut = "" },
    .{ .id = .split_right, .title = "Pane: Split Right", .shortcut = "Ctrl+Shift+R" },
    .{ .id = .split_right_with_shell, .title = "Pane: Split Right with Shell...", .shortcut = "" },
    .{ .id = .split_down, .title = "Pane: Split Down", .shortcut = "Ctrl+Shift+D" },
    .{ .id = .split_down_with_shell, .title = "Pane: Split Down with Shell...", .shortcut = "" },
    .{ .id = .change_default_shell, .title = "Shell: Change Default Shell...", .shortcut = "" },
    .{ .id = .toggle_focus_mode, .title = "Pane: Toggle Focus / Zoom Mode", .shortcut = "" },
    .{ .id = .rename_active_tab, .title = "Tab: Rename Active Tab", .shortcut = "Double-Click" },
    .{ .id = .next_tab, .title = "Terminal: Next Tab", .shortcut = "Ctrl+Tab" },
    .{ .id = .previous_tab, .title = "Terminal: Previous Tab", .shortcut = "Ctrl+Shift+Tab" },
    .{ .id = .close_active, .title = "Terminal: Close Active Pane or Tab", .shortcut = "Ctrl+Shift+W" },
    .{ .id = .toggle_cursor_blink, .title = "Cursor: Toggle Blinking", .shortcut = "" },
    .{ .id = .open_settings, .title = "Settings: Open Preferences...", .shortcut = "Ctrl+," },
    .{ .id = .reload_configuration, .title = "Configuration: Reload init.lua", .shortcut = "" },
};

pub fn filterCommands(query: []const u8, out: *[command_defs.len]CommandDef) usize {
    var scores: [command_defs.len]i32 = undefined;
    var count: usize = 0;
    for (command_defs) |command| {
        const score = palette.commandMatchScore(command.title, command.shortcut, query) orelse continue;
        var index = count;
        // The action list is small and fixed; stable insertion uses bounded stack storage only.
        while (index > 0 and score > scores[index - 1]) : (index -= 1) {
            scores[index] = scores[index - 1];
            out[index] = out[index - 1];
        }
        scores[index] = score;
        out[index] = command;
        count += 1;
    }
    return count;
}

test "filterCommands matches empty query, case-insensitive substrings, and multi-token queries" {
    var matched: [command_defs.len]CommandDef = undefined;
    const all = filterCommands("", &matched);
    try std.testing.expectEqual(command_defs.len, all);

    const theme = filterCommands("theme", &matched);
    try std.testing.expect(theme >= 2);

    const tab_new = filterCommands("tab new", &matched);
    try std.testing.expect(tab_new >= 2);

    const dark_mode = filterCommands("theme dark", &matched);
    try std.testing.expectEqual(@as(usize, 1), dark_mode);
    try std.testing.expectEqual(CommandId.toggle_dark_light, matched[0].id);

    const ctrl_r = filterCommands("Ctrl+Shift+R", &matched);
    try std.testing.expectEqual(@as(usize, 1), ctrl_r);
    try std.testing.expectEqual(CommandId.split_right, matched[0].id);
}
