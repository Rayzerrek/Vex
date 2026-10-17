const std = @import("std");
const Application = @import("terminal_application.zig").TerminalApplication;
const native = @import("../windows/windows_dialog.zig");
const platform = @import("../windows/windows_ffi.zig");
const themes = @import("../workspace/terminal_theme.zig");
const commands = @import("terminal_commands.zig");
const shell = @import("../shell/shell_launch.zig");

/// Builds a synchronous, copied presentation; terminal/session storage never escapes into native controls.
pub fn presentTerminalModal(self: *Application) !void {
    var items: [64]native.DialogItem = undefined;
    var shells: [8]native.DialogItem = undefined;
    var details: [8][96]u8 = undefined;
    var count: usize = 0;
    var request: native.DialogPresentation = .{
        .kind = .appearance,
        .title = "Theme Studio",
        .description = if (self.currentTheme().is_dark) "Dark collection · Select to preview. Enter to apply, Esc to restore." else "Light collection · Select to preview. Enter to apply, Esc to restore.",
        .palette = self.currentTheme().chromePalette(),
        .settings = self.currentPreferences(),
    };
    switch (self.overlay) {
        .theme_picker, .settings => {
            var appearance_indices: [themes.themes.len]usize = undefined;
            for (themes.indicesForAppearance(self.currentTheme().is_dark, &appearance_indices)) |i| {
                const theme = themes.themes[i];
                items[count] = .{ .value = @intCast(i), .label = theme.name, .detail = theme.subtitle, .background = theme.background, .foreground = theme.foreground, .accent = theme.accent, .swatches = .{ theme.ansi[1], theme.ansi[2], theme.ansi[3], theme.ansi[4], theme.ansi[5] } };
                count += 1;
            }
            request.selected = @intCast(self.theme_index);
            if (self.overlay == .settings) {
                request.kind = .settings;
                request.title = "Settings";
                request.description = "Changes are applied and saved automatically.";
                self.discoverAvailableShells();
                shells[0] = .{ .value = 0, .label = "Launch shell", .detail = "Keep the shell selected at startup" };
                for (self.available_shells[0..self.available_shell_count], 1..) |kind, i| {
                    shells[i] = .{ .value = @intFromEnum(kind), .label = kind.displayName(), .detail = kind.subtitle() };
                }
                request.shells = shells[0 .. self.available_shell_count + 1];
            }
        },
        .command_palette => |*cp| {
            request.kind = .commands;
            request.title = "Command Palette";
            request.description = "Search actions, use the arrow keys to choose, then press Enter.";
            request.accept_label = "Run";
            var matched: [commands.command_defs.len]commands.CommandDef = undefined;
            const total = commands.filterCommands(cp.query_buf[0..cp.query_len], &matched);
            for (matched[0..total]) |command| {
                items[count] = .{ .value = @intFromEnum(command.id), .label = command.title, .detail = command.shortcut };
                count += 1;
            }
            request.selected = if (count > 0) items[@min(cp.selected_index, count - 1)].value else 0;
        },
        .tab_peek => |peek| {
            request.kind = .tabs;
            request.title = "Tab Overview";
            request.description = "Choose a tab without interrupting running programs.";
            request.accept_label = "Switch";
            const model = if (self.model) |*model| model else return;
            for (model.tabs.items, 0..) |*tab, i| {
                const session = self.findSession(tab.active_pane) orelse return error.MissingPaneSession;
                const status = try self.tabProgramStatus(tab.tree.root);
                items[count] = .{
                    .value = @intFromEnum(tab.id),
                    .label = tab.customTitle() orelse session.displayTitle(),
                    .detail = try std.fmt.bufPrint(&details[i], "{d} panes · {s}", .{ tab.tree.paneCount(), @tagName(status.summary.state) }),
                };
                count += 1;
            }
            request.selected = if (count > 0) items[@min(peek.selected_index, count - 1)].value else 0;
        },
        .shell_picker => |picker| {
            request.kind = .shell;
            request.title = "Choose Shell";
            request.description = "Existing terminal sessions keep their current shell.";
            request.accept_label = "Choose";
            request.mode = @intFromEnum(picker.mode);
            self.discoverAvailableShells();
            for (self.available_shells[0..self.available_shell_count]) |kind| {
                items[count] = .{ .value = @intFromEnum(kind), .label = kind.displayName(), .detail = kind.subtitle() };
                count += 1;
            }
            request.selected = if (count > 0) items[@min(picker.selected_index, count - 1)].value else 0;
        },
        .close_confirm => |*confirmation| {
            request.kind = .close;
            request.title = confirmation.confirmation.titleSlice();
            request.description = confirmation.confirmation.messageSlice();
            request.accept_label = confirmation.confirmation.confirmSlice();
        },
        .none, .path_completion => return,
    }
    request.items = items[0..count];
    try native.showDialog(request);
}

/// Routes native control events to the same live session owner as keyboard and terminal IO.
pub fn onDialogEvent(self: *Application, event: native.DialogEvent) void {
    if (self.health == .failed) return;
    handleDialogEvent(self, event) catch |err| self.fail(err);
}
fn handleDialogEvent(self: *Application, event: native.DialogEvent) !void {
    switch (event) {
        .cancel => {
            if (self.overlay == .theme_picker) try self.applyTheme(self.overlay.theme_picker.initial_theme_index);
            self.overlay = .none;
            platform.invalidateWindow();
        },
        .query => |query| {
            if (self.overlay != .command_palette) return;
            self.overlay.command_palette.eraseQuery(true);
            _ = self.overlay.command_palette.appendQuery(query);
            try presentTerminalModal(self);
        },
        .preview => |value| switch (self.overlay) {
            .theme_picker => {
                if (value >= themes.themes.len or themes.themes[value].is_dark != self.currentTheme().is_dark) return error.InvalidThemeSelection;
                try self.applyTheme(value);
                try presentTerminalModal(self);
            },
            .command_palette => |*cp| {
                var matched: [commands.command_defs.len]commands.CommandDef = undefined;
                const count = commands.filterCommands(cp.query_buf[0..cp.query_len], &matched);
                for (matched[0..count], 0..) |command, i| {
                    if (@intFromEnum(command.id) == value) cp.selected_index = i;
                }
            },
            .tab_peek => |*peek| {
                const model = if (self.model) |*model| model else return;
                if (model.tabIndex(@enumFromInt(value))) |index| peek.selected_index = index;
            },
            else => {},
        },
        .shell_mode => |mode| {
            if (self.overlay == .shell_picker) self.overlay.shell_picker.mode = @enumFromInt(@as(u8, @intCast(mode)));
        },
        .settings => |settings| {
            if (self.overlay != .settings) return;
            try self.commitPreferences(settings);
            try presentTerminalModal(self);
        },
        .choose => |value| switch (self.overlay) {
            .theme_picker => {
                if (value >= themes.themes.len or themes.themes[value].is_dark != self.currentTheme().is_dark) return error.InvalidThemeSelection;
                try self.applyTheme(value);
                self.overlay = .none;
                try self.commitPreferences(self.currentPreferences());
            },
            .command_palette => |*cp| {
                var matched: [commands.command_defs.len]commands.CommandDef = undefined;
                const count = commands.filterCommands(cp.query_buf[0..cp.query_len], &matched);
                for (matched[0..count]) |command| if (@intFromEnum(command.id) == value) {
                    try self.executeCommand(command.id);
                    return;
                };
                self.overlay = .none;
            },
            .tab_peek => {
                self.overlay = .none;
                const model = if (self.model) |*model| model else return;
                if (model.tabIndex(@enumFromInt(value))) |index| try self.applyAction(.select_tab, @intCast(index));
            },
            .shell_picker => |picker| {
                const kind = std.meta.intToEnum(shell.ShellKind, value) catch return error.InvalidShellSelection;
                var found = false;
                for (self.available_shells[0..self.available_shell_count]) |available| if (available == kind) {
                    found = true;
                };
                if (!found) return error.UnavailableShellSelection;
                try self.executeShellPickerChoice(picker.mode, kind);
            },
            .close_confirm => try self.confirmCloseOverlay(),
            else => {},
        },
    }
}
