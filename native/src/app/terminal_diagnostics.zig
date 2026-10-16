//! Selftest-only observations of the real terminal owner; no shadow state or simulated sessions.
const Application = @import("terminal_application.zig").TerminalApplication;
const std = @import("std");
const platform = @import("../windows/windows_ffi.zig");
const status = @import("../terminal/program_status.zig");
const tab_strip = @import("../workspace/tab_strip.zig");
const commands = @import("terminal_commands.zig");
const dialogs = @import("../windows/windows_dialog.zig");

pub fn onProbe(self: *Application, query: u32) u64 {
    if (self.health == .failed) return 0;
    const model = if (self.model) |*model| model else return 0;
    switch (query) {
        10 => return model.tabs.items.len,
        11 => return model.activeTab().tree.paneCount(),
        12 => return @intFromEnum(model.activeTab().active_pane),
        13 => return self.sessions.items.len,
        14 => return if (self.activeSession()) |session| session.pty.processId() else 0,
        20 => return platform.paintCount(),
        34 => return @intFromEnum(self.default_shell_kind),
        38 => return if (self.activeSession()) |session| @intFromEnum(session.icon.glyph) else 0,
        100 => return if (self.overlay == .command_palette) self.overlay.command_palette.selected_index else 0,
        101 => {
            if (self.overlay != .command_palette) return 0;
            var matched: [commands.command_defs.len]commands.CommandDef = undefined;
            return commands.filterCommands(self.overlay.command_palette.query_buf[0..self.overlay.command_palette.query_len], &matched);
        },
        102 => return if (self.overlay == .command_palette) dialogs.firstVisibleRow() else 0,
        103 => return if (self.overlay == .command_palette) self.overlay.command_palette.query_len else 0,
        104 => return @intFromBool(self.path_completion.index == .deferred and self.path_completion.index.deferred.loading),
        105 => return self.path_completion.entries.items.len,
        107 => return if (self.activeSession()) |session| @intFromEnum(session.terminal.program_status.shellActivityState()) else 0,
        108 => return if (model.tabs.items.len > 1) if (self.findSession(model.tabs.items[1].active_pane)) |session| session.terminal.title.generation else 0 else 0,
        109 => return if (self.activeSession()) |session| session.terminal.title.generation else 0,
        72 => return @intFromBool(self.cursor_blink_enabled),
        73 => return @intFromBool(self.cursor_blink.isShown()),
        74 => return @intFromBool(self.cursor_blink.state == .blinking),
        75 => return @intFromBool(platform.cursorWakeArmed()),
        76 => return @intFromBool(self.cursor_window_active),
        77 => return if (self.activeSession()) |session| @intFromBool(session.terminal.frame.cursor.blinking) else 0,
        78 => return if (self.activeSession()) |session| @intFromBool(session.terminal.frame.cursor.visible) else 0,
        79 => return if (self.activeSession()) |session| session.terminal.frame.generation else 0,
        80 => return if (self.activeSession()) |session| @intFromEnum(session.terminal.frame.cursor.shape) else 0,
        81 => return if (self.activeSession()) |session| session.terminal.frame.cursor.column else 0,
        82 => return if (self.activeSession()) |session| session.terminal.frame.cursor.row else 0,
        83 => return if (self.metrics) |metrics| @intFromFloat(@round(metrics.cell_width * 1000)) else 0,
        84 => return if (self.metrics) |metrics| @intFromFloat(@round(metrics.cell_height * 1000)) else 0,
        90 => return self.status_pulse.phaseIndex(),
        91 => return @intFromBool(self.status_pulse.state == .pulsing),
        92 => return if (self.activeSession()) |session| @intFromEnum(session.icon.kind) else 0,
        93 => return if (self.activeSession()) |session| if (session.icon.badge_len > 0) session.icon.badge_text[0] else 0 else 0,
        94 => return if (self.activeSession()) |session| session.active_process_title_len else 0,
        95 => return probeBytes(if (self.activeSession()) |session| session.active_process_title[0..session.active_process_title_len] else "", 0),
        96 => return probeBytes(if (self.activeSession()) |session| session.active_process_title[0..session.active_process_title_len] else "", 8),
        85 => return if (self.activeSession()) |session| @intFromEnum(session.shell_kind) else 0,
        86 => return (self.tabProgramStatus(model.activeTab().tree.root) catch return 0).origin_pane_number,
        87 => return if ((self.tabProgramStatus(model.activeTab().tree.root) catch return 0).origin_pane_id) |id| @intFromEnum(id) else 0,
        88 => {
            self.discoverAvailableShells();
            return self.available_shell_count;
        },
        89 => return if (self.overlay == .shell_picker) @intFromEnum(self.overlay.shell_picker.mode) else 0,
        70 => return if (self.activeSession()) |session| @intFromBool(session.state == .exited) else 0,
        71 => return if (self.activeSession()) |session| switch (session.state) {
            .running => 0,
            .exited => |code| code,
        } else 0,
        40 => return switch (model.activeTab().tree.root.*) {
            .leaf => 0,
            .split => |split| @intFromFloat(@round(split.ratio * 1000)),
        },
        41 => return @intFromBool(self.pointer_drag == .divider and self.pointer_drag.divider.active != null),
        42 => return @intFromBool(platform.hasPointerCapture()),
        45 => return if (self.pointer_drag == .tab) switch (self.pointer_drag.tab) {
            .none => 0,
            .armed => 1,
            .dragging => 2,
        } else 0,
        46 => return @intFromEnum(model.activeTab().id),
        47 => return model.selected_tab,
        48 => {
            const metrics = self.metrics orelse return 0;
            const layout = tab_strip.TabStripLayout.init(metrics.width, model.tabs.items.len) catch return 0;
            return @intFromFloat(@round(layout.tab_width * 1000));
        },
        49 => return if (self.pointer_drag == .tab and self.pointer_drag.tab == .dragging) if (self.pointer_drag.tab.dragging.destination) |index| index + 1 else 0 else 0,
        50 => return @intFromBool(model.activeTab().is_focus_mode),
        51 => return self.theme_index,
        52 => return @intFromBool(self.currentTheme().is_dark),
        53 => return switch (self.overlay) {
            .none => 0,
            .command_palette => 1,
            .theme_picker => 2,
            .tab_peek => 3,
            .path_completion => 4,
            .close_confirm => 5,
            .shell_picker => 6,
            .settings => 7,
        },
        54 => return @intFromFloat(@round(platform.fontSize())),
        55 => return self.path_completion.matches.items.len,
        60...67 => return if (query - 60 < model.tabs.items.len) @intFromEnum(model.tabs.items[query - 60].id) else 0,
        43 => return if (self.activeSession()) |session| if (session.size) |size| size.columns else 0 else 0,
        44 => return if (self.activeSession()) |session| if (session.size) |size| size.rows else 0 else 0,
        39 => {
            const session = self.activeSession() orelse return 0;
            const reported = session.directory.reported orelse return 0;
            return @intFromBool(!std.mem.eql(u8, reported, session.directory.launch));
        },
        31 => return if (self.activeSession()) |session| @intFromEnum(session.terminal.program_status.programStatus().state) else 0,
        32 => return @intFromEnum((self.tabProgramStatus(model.activeTab().tree.root) catch return 0).summary.state),
        33 => {
            var selected: status.ProgramStatusState = .none;
            for (model.tabs.items) |tab| {
                const candidate = (self.tabProgramStatus(tab.tree.root) catch return 0).summary.state;
                if (candidate.displayPriority() > selected.displayPriority()) selected = candidate;
            }
            return @intFromEnum(selected);
        },
        else => {},
    }
    const marker = switch (query) {
        0 => "VEX_NATIVE_SMOKE_OK",
        1 => "VEX_NATIVE_FLOOD_OK",
        2 => "VEX_NATIVE_PANE_A",
        3 => "VEX_NATIVE_PANE_B",
        4 => "VEX_NATIVE_TAB_A",
        5 => "VEX_NATIVE_TAB_B",
        6 => "VEX_NATIVE_PANE_C",
        7 => "VEX_NATIVE_BACKGROUND_OK",
        8 => "VEX_NATIVE_DIRECTORY_OK",
        9 => "VEX_NATIVE_ROOT_DIRECTORY_OK",
        30 => "VEX_NATIVE_DIRECTORY_REPORTED",
        35 => "VEX_NATIVE_SHELL_READY",
        36 => "VEX_NATIVE_SHELL_DIRECTORY_OK",
        37 => "VEX_NATIVE_SHELL_ENV_OK",
        else => return 0,
    };
    const session = self.activeSession() orelse return 0;
    const found = session.terminal.containsViewportText(marker) catch |err| {
        self.fail(err);
        return 0;
    };
    return @intFromBool(found);
}

fn probeBytes(text: []const u8, offset: usize) u64 {
    var word: u64 = 0;
    if (offset >= text.len) return 0;
    const slice = text[offset..@min(text.len, offset + 8)];
    for (slice, 0..) |byte, index| {
        word |= @as(u64, byte) << @intCast(index * 8);
    }
    return word;
}
