const std = @import("std");
const platform = @import("windows_ffi.zig");
const preferences = @import("../app/user_settings.zig");
const themes = @import("../workspace/terminal_theme.zig");
const c = platform.c;

/// Owned dialog kind; path completion deliberately remains an in-terminal popup.
pub const DialogKind = enum(u32) { appearance = 1, commands = 2, settings = 4, shell = 5, close = 6, custom_shell = 7 };
/// Borrowed presentation strings are copied by the native dialog before showDialog returns.
pub const DialogItem = struct {
    value: u32,
    label: []const u8,
    detail: []const u8 = "",
    background: u32 = 0,
    foreground: u32 = 0,
    accent: u32 = 0,
    swatches: [5]u32 = .{ 0, 0, 0, 0, 0 },
};
/// Native control notifications are decoded before application code receives them.
pub const DialogEvent = union(enum) { cancel, choose: u32, preview: u32, query: []const u8, settings: preferences.UserSettings, add_shell, save_shell: struct { name: []const u8, program: []const u8, arguments: []const u8 } };
/// One window-thread-owned dialog, with copied presentation and no retained application pointers.
pub const DialogPresentation = struct {
    kind: DialogKind,
    title: []const u8,
    description: []const u8,
    accept_label: []const u8 = "Apply",
    items: []const DialogItem = &.{},
    shells: []const DialogItem = &.{},
    selected: u32 = 0,
    settings: preferences.UserSettings = .{},
    palette: themes.ChromePalette,
};

fn nativeText(text: []const u8) c.VexDialogText {
    return .{ .bytes = if (text.len == 0) null else text.ptr, .length = text.len };
}
fn nativeItem(item: DialogItem) c.VexDialogItem {
    return .{ .value = item.value, .label = nativeText(item.label), .detail = nativeText(item.detail), .background = item.background, .foreground = item.foreground, .accent = item.accent, .swatches = item.swatches };
}
/// Opens/updates copied native controls; Win32 disables only their owning terminal window.
pub fn showDialog(presentation: DialogPresentation) !void {
    if (presentation.items.len > 64 or presentation.shells.len > 64) return error.DialogItemLimit;
    var items: [64]c.VexDialogItem = undefined;
    var shells: [64]c.VexDialogItem = undefined;
    for (presentation.items, 0..) |item, i| items[i] = nativeItem(item);
    for (presentation.shells, 0..) |item, i| shells[i] = nativeItem(item);
    const result = c.vex_dialog_open(.{
        .kind = @intFromEnum(presentation.kind),
        .selected = presentation.selected,
        .title = nativeText(presentation.title),
        .description = nativeText(presentation.description),
        .accept_label = nativeText(presentation.accept_label),
        .items = if (presentation.items.len == 0) null else &items,
        .count = presentation.items.len,
        .shells = if (presentation.shells.len == 0) null else &shells,
        .shell_count = presentation.shells.len,
        .settings = .{ .theme = presentation.settings.theme_index, .font_size = presentation.settings.font_size, .shell = @intFromEnum(presentation.settings.default_shell), .cursor_blink = presentation.settings.cursor_blink, .copy_on_select = presentation.settings.copy_on_select, .font_family = presentation.settings.font_family },
        .palette = platform.nativeChromePalette(presentation.palette),
    });
    if (result != 0) return error.NativeDialogFailure;
}
pub fn showFormError(message: []const u8) void {
    c.vex_dialog_form_error(nativeText(message));
}
/// Reads native list scrolling on the window thread; there is no shadow Zig viewport.
pub fn firstVisibleRow() usize {
    return c.vex_dialog_first_visible();
}
/// Closes without a result notification; caller owns the application state transition.
pub fn closeDialog() void {
    c.vex_dialog_close();
}
/// Reports expected preference IO failures without terminating the terminal window.
pub fn showSettingsFailure(err: anyerror) void {
    var buffer: [256]u8 = undefined;
    const message = std.fmt.bufPrint(&buffer, "Vex settings: preferences were not saved ({s}). Existing sessions are unchanged.", .{@errorName(err)}) catch "Vex settings: preferences were not saved. Existing sessions are unchanged.";
    c.vex_dialog_warning(nativeText(message));
}
pub fn showShellLaunchFailure(err: anyerror) void {
    var buffer: [256]u8 = undefined;
    const message = std.fmt.bufPrint(&buffer, "The selected shell could not be started ({s}). Existing sessions are unchanged.", .{@errorName(err)}) catch "The selected shell could not be started. Existing sessions are unchanged.";
    c.vex_dialog_warning(nativeText(message));
}
/// Reports configuration errors without blocking input or modifying the user's Lua source.
pub fn showSettingsLoadFailure(err: anyerror, diagnostic: *const preferences.SettingsDiagnostic, context: enum { startup, reload }) void {
    var buffer: [1024]u8 = undefined;
    const reason = if (diagnostic.message().len > 0 and std.unicode.utf8ValidateSlice(diagnostic.message())) diagnostic.message() else @errorName(err);
    const message = std.fmt.bufPrint(&buffer, "{s}\n{s} Your Lua files have not been changed.", .{ reason, if (context == .reload) "Previous settings are still active." else "Using default settings." }) catch "Vex configuration could not be loaded. Your Lua files have not been changed.";
    c.vex_dialog_warning(nativeText(message));
}
/// Parses bounded native control data, including settings enums, before entering the application.
pub fn decodeDialogEvent(event: u32, value: u32, text: []const u8, raw: c.VexUserSettings) !DialogEvent {
    return switch (event) {
        0 => .cancel,
        1 => .{ .choose = value },
        2 => .{ .preview = value },
        3 => blk: {
            if (text.len > 128 or !std.unicode.utf8ValidateSlice(text)) return error.InvalidDialogQuery;
            for (text) |byte| if (byte < 32 or byte == 127) return error.InvalidDialogQuery;
            break :blk .{ .query = text };
        },
        4 => blk: {
            if (raw.theme >= themes.themes.len or raw.font_size < 9 or raw.font_size > 32) return error.InvalidUserSettings;
            const settings: preferences.UserSettings = .{
                .theme_index = @intCast(raw.theme),
                .font_size = @intCast(raw.font_size),
                .cursor_blink = raw.cursor_blink,
                .copy_on_select = raw.copy_on_select,
                .font_family = raw.font_family,
                .default_shell = std.meta.intToEnum(preferences.DefaultShell, raw.shell) catch return error.InvalidDefaultShell,
            };
            try settings.validate();
            break :blk .{ .settings = settings };
        },
        6 => .add_shell,
        7 => blk: {
            if (text.len > 256 * 1024 or !std.unicode.utf8ValidateSlice(text)) return error.InvalidShellForm;
            var fields = std.mem.splitScalar(u8, text, 0);
            const name = fields.next() orelse return error.InvalidShellForm;
            const program = fields.next() orelse return error.InvalidShellForm;
            const arguments = fields.next() orelse return error.InvalidShellForm;
            if (fields.next() != null) return error.InvalidShellForm;
            break :blk .{ .save_shell = .{ .name = name, .program = program, .arguments = arguments } };
        },
        else => error.InvalidDialogEvent,
    };
}

test "native dialog events validate settings and query data before entering the terminal owner" {
    const raw: c.VexUserSettings = .{ .theme = 2, .font_size = 19, .shell = 3, .cursor_blink = true, .copy_on_select = false, .font_family = preferences.default_font };
    const result = try decodeDialogEvent(4, 0, "", raw);
    try std.testing.expectEqual(@as(u8, 19), result.settings.font_size);
    try std.testing.expectEqual(preferences.DefaultShell.pwsh, result.settings.default_shell);
    try std.testing.expect(!result.settings.copy_on_select);
    var invalid = raw;
    invalid.font_size = 0;
    try std.testing.expectError(error.InvalidUserSettings, decodeDialogEvent(4, 0, "", invalid));
    invalid = raw;
    invalid.shell = 6;
    try std.testing.expectError(error.InvalidDefaultShell, decodeDialogEvent(4, 0, "", invalid));
    try std.testing.expectError(error.InvalidDialogEvent, decodeDialogEvent(99, 0, "", raw));
    // Mode controls no longer exist; retired event IDs must not mutate shell-picker intent.
    for (0..4) |mode| try std.testing.expectError(error.InvalidDialogEvent, decodeDialogEvent(5, @intCast(mode), "", raw));
    try std.testing.expectError(error.InvalidDialogQuery, decodeDialogEvent(3, 0, "bad\nquery", raw));
    try std.testing.expectError(error.InvalidDialogQuery, decodeDialogEvent(3, 0, "\xff", raw));
    try std.testing.expectEqualStrings("future tool", (try decodeDialogEvent(3, 0, "future tool", raw)).query);
}

test "custom shell form separates arguments without interpreting quotes or command syntax" {
    const raw: c.VexUserSettings = .{ .theme = 0, .font_size = 14, .shell = 1, .cursor_blink = true, .copy_on_select = true, .font_family = preferences.default_font };
    const form = (try decodeDialogEvent(7, 0, "Future 音\x00C:\\My Shell\\shell.exe\x00--interactive \"hello world\"", raw)).save_shell;
    try std.testing.expectEqualStrings("Future 音", form.name);
    try std.testing.expectEqualStrings("C:\\My Shell\\shell.exe", form.program);
    try std.testing.expectEqualStrings("--interactive \"hello world\"", form.arguments);
    try std.testing.expectEqualStrings("", (try decodeDialogEvent(7, 0, "Name\x00shell.exe\x00", raw)).save_shell.arguments);
    try std.testing.expectError(error.InvalidShellForm, decodeDialogEvent(7, 0, "missing separator", raw));
    try std.testing.expectError(error.InvalidShellForm, decodeDialogEvent(7, 0, "a\x00b\x00c\x00d", raw));
    try std.testing.expectError(error.InvalidShellForm, decodeDialogEvent(7, 0, "a\x00b\x00\xff", raw));
    const custom = try decodeDialogEvent(4, 0, "", .{ .theme = 0, .font_size = 14, .shell = 256, .cursor_blink = true, .copy_on_select = true, .font_family = preferences.default_font });
    try std.testing.expectEqual(@as(u32, 256), @intFromEnum(custom.settings.default_shell));
}

test "dialog item budgets reject oversized presentations before native window access" {
    const items = [_]DialogItem{.{ .value = 0, .label = "future" }} ** 65;
    try std.testing.expectError(error.DialogItemLimit, showDialog(.{ .kind = .commands, .title = "Commands", .description = "", .items = &items, .palette = themes.themes[0].chromePalette() }));
}
