const std = @import("std");
const Application = @import("terminal_application.zig").TerminalApplication;
const native = @import("../windows/windows_dialog.zig");
const platform = @import("../windows/windows_ffi.zig");
const themes = @import("../workspace/terminal_theme.zig");
const commands = @import("terminal_commands.zig");
const overlays = @import("terminal_overlays.zig");
const shell = @import("../shell/shell_launch.zig");

/// Builds a synchronous, copied presentation; terminal/session storage never escapes into native controls.
pub fn presentTerminalModal(self: *Application) !void {
    var items: [64]native.DialogItem = undefined;
    var shells: [64]native.DialogItem = undefined;
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
                shells[0] = .{ .value = 0, .label = "Launch shell", .detail = "Keep the shell selected at startup" };
                var shell_count: usize = 1;
                for (self.available_shells[0..self.available_shell_count]) |kind| {
                    if (kind == .custom) continue;
                    shells[shell_count] = .{ .value = @intFromEnum(kind), .label = self.shellLabel(kind), .detail = self.shellDetail(kind) };
                    shell_count += 1;
                }
                request.shells = shells[0..shell_count];
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
        .tab_peek => return,
        .shell_picker => |picker| {
            request.kind = .shell;
            request.title = "Choose a shell";
            request.description = "";
            for (self.available_shells[0..self.available_shell_count]) |kind| {
                items[count] = .{ .value = @intFromEnum(kind), .label = self.shellLabel(kind), .detail = self.shellDetail(kind) };
                count += 1;
            }
            request.selected = if (count > 0) items[@min(picker.selected_index, count - 1)].value else 0;
        },
        .custom_shell => {
            request.kind = .custom_shell;
            request.title = "Add custom shell";
            request.description = "Choose how to start your shell. Arguments are passed directly, not through another shell.";
            request.accept_label = "Add shell";
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
            const return_to_peek = if (self.overlay == .close_confirm) self.overlay.close_confirm.return_to_peek else null;
            if (self.overlay == .theme_picker) try self.applyTheme(self.overlay.theme_picker.initial_theme_index);
            if (self.overlay == .custom_shell) {
                self.overlay = .settings;
                try presentTerminalModal(self);
                return;
            }
            self.overlay = .none;
            if (return_to_peek) |index| overlays.restorePeek(self, index);
            platform.invalidateWindow();
        },
        .query => |query| {
            if (self.overlay != .command_palette) return;
            self.overlay.command_palette.clearQuery();
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
            else => {},
        },
        .add_shell => {
            if (self.overlay != .settings) return;
            self.overlay = .custom_shell;
            try presentTerminalModal(self);
        },
        .save_shell => |form| {
            if (self.overlay != .custom_shell) return;
            self.addCustomShell(std.mem.trim(u8, form.name, " \t"), std.mem.trim(u8, form.program, " \t"), form.arguments) catch |err| {
                native.showFormError(switch (err) {
                    error.InvalidShellName => "Enter a name shorter than 256 UTF-8 bytes.",
                    error.DuplicateShellProfile => "A shell with that name already exists. Choose another name.",
                    error.ShellProgramUnavailable => "Program not found. Choose an executable or check your PATH.",
                    error.InvalidShellProgram, error.InvalidDirectoryPath => "Use an absolute executable path, a PATH command, or wsl:/absolute/program.",
                    error.TooManyShellProfiles => "The custom shell limit has been reached (56 profiles).",
                    error.SettingsFileTooLarge => "Custom profiles exceed the 64 KiB storage limit. Shorten the profile arguments.",
                    error.InvalidLuaShellProfiles => "Fix the .shells.lua configuration before adding another shell.",
                    error.ProfileStoreBusy => "Another Vex window is saving profiles. Try again in a moment.",
                    error.UnavailableShellSelection => "The default profile is missing from .shells.lua. Choose another default first.",
                    error.InvalidLinuxDirectory, error.InvalidWslProgram, error.InvalidWslPathText => "Use wsl:/ followed by the absolute Linux executable path.",
                    error.InvalidShellText, error.ShellCommandLineTooLong => "Check the program and arguments: valid UTF-8, no controls, and fewer than 32767 UTF-16 units.",
                    else => "The profile could not be saved. Check the configuration folder and try again.",
                });
                return;
            };
            self.overlay = .settings;
            try presentTerminalModal(self);
        },
        .settings => |settings| {
            if (self.overlay != .settings) return;
            self.commitPreferences(settings) catch |err| {
                native.showSettingsFailure(err);
                return;
            };
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
            .shell_picker => |picker| {
                const kind = std.meta.intToEnum(shell.ShellKind, value) catch return error.InvalidShellSelection;
                var found = false;
                for (self.available_shells[0..self.available_shell_count]) |available| if (available == kind) {
                    found = true;
                };
                if (!found) return error.UnavailableShellSelection;
                self.executeShellPickerChoice(picker.mode, kind) catch |err| {
                    native.showShellLaunchFailure(err);
                    self.overlay = .{ .shell_picker = picker };
                    try presentTerminalModal(self);
                };
            },
            .close_confirm => try self.confirmCloseOverlay(),
            else => {},
        },
    }
}
