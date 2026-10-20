const std = @import("std");
const binding = @import("../terminal/api.zig");
const core = @import("../terminal/terminal.zig");
const platform = @import("../windows/windows_ffi.zig");
const panes = @import("../workspace/pane_tree.zig");
const directory = @import("../shell/directory.zig");
const shell = @import("../shell/shell_launch.zig");
const app_icon = @import("../terminal/app_icon.zig");
const close_confirmation = @import("../workspace/close_confirmation.zig");

const command_line = @import("../terminal/command_line.zig");
const terminal_title = @import("../terminal/terminal_title.zig");
const status_presentation = @import("../terminal/status_presentation.zig");

const SessionState = union(enum) { running, exited: u32 };

pub const CopyFeedback = struct {
    spans: [64]platform.c.VexCopySpan = undefined,
    count: u8 = 0,
    scroll_offset: u64 = 0,
    started_ms: ?u64 = null,

    pub const duration_ms: u64 = 440;

    pub fn clear(self: *CopyFeedback) void {
        self.started_ms = null;
        self.count = 0;
    }

    pub fn progress(self: *CopyFeedback, now_ms: u64, scroll_offset: u64, animations_enabled: bool) ?f32 {
        const started = self.started_ms orelse return null;
        if (!animations_enabled or self.count == 0 or scroll_offset != self.scroll_offset) {
            self.clear();
            return null;
        }
        const elapsed = now_ms -| started;
        if (elapsed >= duration_ms) {
            self.clear();
            return null;
        }
        return @as(f32, @floatFromInt(elapsed)) / @as(f32, @floatFromInt(duration_ms));
    }
};

/// Stable-address owner of one emulator, ConPTY process and directory; borrows the application API/hostname.
pub const TerminalSession = struct {
    id: panes.PaneId,
    terminal: core.Terminal,
    pty: platform.PtyOwner,
    directory: directory.PaneDirectory,
    machine_name: []const u8,
    size: ?platform.TerminalSize = null,
    state: SessionState = .running,
    needs_attention: bool = false,
    scrollbar_visibility: @import("../workspace/scrollbar_visibility.zig").ScrollbarVisibility = .{},
    copy_feedback: CopyFeedback = .{},
    wheel_scroll: @import("../workspace/wheel_scroll.zig").WheelScroll = .{},
    sync_defer_since_ms: ?i64 = null,
    icon: app_icon.AppIcon = app_icon.AppIcon.terminal_shell,
    process_icon: app_icon.AppIcon = app_icon.AppIcon.terminal_shell,
    last_icon_probe_ms: i64 = 0,
    active_process_title: [64]u8 = undefined,
    active_process_title_len: usize = 0,
    has_foreground_process: bool = false,
    foreground_shell_command: bool = false,
    foreground_is_shell: bool = false,
    foreground_is_root: bool = false,
    shell_kind: shell.ShellKind = .cmd,
    shell_name: []const u8 = "Command Prompt",
    shell_program: []const u8 = "cmd.exe",

    /// Allocates at the final address before registering callbacks; startup failure releases every partial owner.
    pub fn create(allocator: std.mem.Allocator, api: *const binding.Api, id: panes.PaneId, path: directory.DirectoryPath, machine_name: []const u8, launch: *const shell.ShellLaunch) !*TerminalSession {
        const session = try allocator.create(TerminalSession);
        errdefer allocator.destroy(session);
        var pane_directory = try directory.PaneDirectory.initForContext(allocator, path, launch.directoryContext());
        errdefer pane_directory.deinit();
        var terminal = try core.Terminal.init(allocator, api);
        errdefer terminal.deinit();
        var pty = try platform.PtyOwner.init(allocator, id, pane_directory.current(), launch);
        errdefer pty.deinit();
        session.* = .{
            .id = id,
            .terminal = terminal,
            .pty = pty,
            .directory = pane_directory,
            .machine_name = machine_name,
            .shell_kind = launch.kind,
            .shell_name = launch.shellName(),
            .shell_program = launch.program,
        };
        try session.terminal.attachResponseSink(session.pty.responseSink());
        try session.terminal.attachDirectorySink(session);
        try session.terminal.attachClipboardSink(session);
        return session;
    }
    /// Synchronously parses each borrowed PWD report before a later report can replace it.
    pub fn onDirectoryReport(self: *TerminalSession, bytes: []const u8) !void {
        try self.directory.applyReport(bytes, self.machine_name);
    }
    /// Publishes decoded write-only OSC 52 / OSC 1337 clipboard payloads to the Windows clipboard.
    pub fn onClipboardWrite(_: *TerminalSession, text: []const u8) !void {
        try platform.writeClipboardText(text);
    }
    /// Joins native workers before releasing the emulator, directory and stable session address.
    pub fn destroy(self: *TerminalSession, allocator: std.mem.Allocator) void {
        self.pty.deinit();
        self.terminal.deinit();
        self.directory.deinit();
        allocator.destroy(self);
    }
    /// User input acknowledges completed results; emulator replies deliberately bypass this path.
    pub fn writeUserInput(self: *TerminalSession, bytes: []const u8) !void {
        self.last_icon_probe_ms = 0;
        const previous = status_presentation.statusPresentationKey(self.terminal.program_status.programStatus());
        self.terminal.program_status.acknowledgeProgramStatus();
        if (previous != status_presentation.statusPresentationKey(self.terminal.program_status.programStatus())) platform.invalidateWindow();
        try self.pty.write(bytes);
    }
    /// Normalizes clipboard newlines and applies bracketed paste when the active shell/TUI requested DEC 2004.
    pub fn paste(self: *TerminalSession, allocator: std.mem.Allocator, text: []const u8) !void {
        if (text.len == 0) return;
        const encoded = try self.terminal.encodePasteAlloc(allocator, text);
        defer allocator.free(encoded);
        try self.writeUserInput(encoded);
    }
    /// Copies the active terminal selection to the system clipboard when a selection range is present.
    pub fn copySelectionToClipboard(self: *TerminalSession, allocator: std.mem.Allocator) !bool {
        const selected = try self.terminal.selectedTextAlloc(allocator, true) orelse return false;
        defer allocator.free(selected);
        if (selected.len > 0) try platform.writeClipboardText(selected);
        return true;
    }
    /// Snapshots visible selected cell runs so the copy shimmer can animate even after selection clears.
    pub fn recordCopyFeedback(self: *TerminalSession, now_ms: u64) void {
        if (!platform.animationsEnabled()) return;
        const frame = self.terminal.readFrame() catch return;
        const sb = self.terminal.scrollbar() catch return;
        var count: u8 = 0;
        for (0..frame.rows) |r| {
            const row_cells = frame.cells.items[r * frame.columns ..][0..frame.columns];
            var in_span = false;
            var start_col: u16 = 0;
            var end_col: u16 = 0;
            for (row_cells, 0..) |cell, c_idx| {
                const col: u16 = @intCast(c_idx);
                if (cell.selected) {
                    if (!in_span) {
                        in_span = true;
                        start_col = col;
                    }
                    end_col = col + 1;
                } else if (in_span) {
                    if (count < self.copy_feedback.spans.len) {
                        self.copy_feedback.spans[count] = .{ .row = @intCast(r), .start_col = start_col, .end_col = end_col };
                        count += 1;
                    }
                    in_span = false;
                }
            }
            if (in_span and count < self.copy_feedback.spans.len) {
                self.copy_feedback.spans[count] = .{ .row = @intCast(r), .start_col = start_col, .end_col = end_col };
                count += 1;
            }
        }
        if (count > 0) {
            self.copy_feedback.count = count;
            self.copy_feedback.scroll_offset = sb.offset;
            self.copy_feedback.started_ms = now_ms;
        }
    }
    /// Deletes a cursor-anchored keyboard selection by sending one DEL (0x7f) per selected character.
    pub fn deleteKeyboardSelection(self: *TerminalSession) !bool {
        if (self.state != .running) return false;
        const count = try self.terminal.consumeKeyboardSelectionBackspaces();
        if (count == 0) return false;
        const chunk = [_]u8{0x7f} ** 64;
        var remaining = count;
        while (remaining > 0) {
            const step = @min(remaining, chunk.len);
            try self.writeUserInput(chunk[0..step]);
            remaining -= step;
        }
        return true;
    }
    /// Avoids redundant ConPTY/emulator resizes; exited sessions retain a resizable terminal viewport.
    pub fn resize(self: *TerminalSession, size: platform.TerminalSize) !void {
        if (self.size) |previous| if (std.meta.eql(previous, size)) return;
        if (self.state == .running) try self.pty.resize(size);
        try self.terminal.resize(size.columns, size.rows, size.cell_width_px, size.cell_height_px);
        self.size = size;
        if (self.state == .running and try self.terminal.inBandResize()) {
            var buffer: [64]u8 = undefined;
            const height_px: u64 = @as(u64, size.rows) * @as(u64, size.cell_height_px);
            const width_px: u64 = @as(u64, size.columns) * @as(u64, size.cell_width_px);
            const report = try std.fmt.bufPrint(&buffer, "\x1b[48;{d};{d};{d};{d}t", .{ size.rows, size.columns, height_px, width_px });
            try self.pty.write(report);
        }
    }
    /// Returns the active title for tab and pane headers, falling back to the detected foreground process name or the session's shell name when the shell has not set an app title.
    pub fn displayTitle(self: *const TerminalSession) []const u8 {
        const title = self.terminal.title.slice();
        if (title.len > 0) return title;
        if (self.active_process_title_len > 0) return self.active_process_title[0..self.active_process_title_len];
        return self.shell_name;
    }
    /// Resolves the active foreground program icon and process title using cached PID inspection or OSC title/status fallback.
    pub fn refreshAppIcon(self: *TerminalSession, query_process: bool) bool {
        const previous_icon = self.icon;
        const previous_title_len = self.active_process_title_len;
        var previous_title_buf: [64]u8 = undefined;
        if (previous_title_len > 0) @memcpy(previous_title_buf[0..previous_title_len], self.active_process_title[0..previous_title_len]);

        if (self.state != .running) {
            self.icon = app_icon.AppIcon.terminal_shell;
            self.process_icon = app_icon.AppIcon.terminal_shell;
            self.active_process_title_len = 0;
            self.has_foreground_process = false;
            self.foreground_shell_command = false;
            return !previous_icon.eql(self.icon) or previous_title_len != 0;
        }
        if (query_process) {
            var exe_buf: [256]u8 = undefined;
            var cmd_buf: [512]u8 = undefined;
            const fg = self.pty.foregroundProcess(&exe_buf, &cmd_buf);
            const proc_name: ?[]const u8 = if (fg.exe.len > 0) fg.exe else null;
            const proc_cmd: ?[]const u8 = if (fg.cmd.len > 0) fg.cmd else null;
            self.foreground_is_shell = fg.is_shell;
            self.foreground_is_root = fg.is_root;
            self.foreground_shell_command = self.foreground_is_shell and shellRunsCommand(fg.is_root, fg.cmd);
            const resolved_proc_title = resolveForegroundProcessTitle(proc_name, proc_cmd);
            if (resolved_proc_title) |title_slice| {
                const display = terminal_title.sanitizeDisplayText(title_slice, &self.active_process_title);
                self.active_process_title_len = if (display) |text| text.len else 0;
                const working = !self.foreground_is_shell or self.foreground_shell_command;
                if (self.has_foreground_process and !working and self.terminal.title.appSlice().len > 0) self.terminal.title.reset();
                self.has_foreground_process = working;
            } else {
                if (self.has_foreground_process and self.terminal.title.appSlice().len > 0) {
                    self.terminal.title.reset();
                }
                self.active_process_title_len = 0;
                self.has_foreground_process = false;
            }
            const title_app = self.terminal.title.appSlice();
            const title_raw = self.terminal.title.slice();
            const osc_title: ?[]const u8 = if (title_app.len > 0) title_app else if (title_raw.len > 0) title_raw else null;
            self.process_icon = app_icon.resolve(proc_name, proc_cmd, osc_title, null);
            self.icon = if (self.terminal.program_status.programStatus().app) |app| app_icon.fromProcess(app) else self.process_icon;
        } else {
            const title_app = self.terminal.title.appSlice();
            const title_raw = self.terminal.title.slice();
            const osc_title: ?[]const u8 = if (title_app.len > 0) title_app else if (title_raw.len > 0) title_raw else null;
            const status_app = self.terminal.program_status.programStatus().app;
            self.icon = if (status_app) |app| app_icon.fromProcess(app) else if (self.has_foreground_process) self.process_icon else app_icon.resolve(null, null, osc_title, null);
        }
        const title_changed = self.active_process_title_len != previous_title_len or
            !std.mem.eql(u8, previous_title_buf[0..previous_title_len], self.active_process_title[0..self.active_process_title_len]);
        return !previous_icon.eql(self.icon) or title_changed;
    }
    /// Captures a borrowed close-evaluation snapshot of the session's running/title/process state.
    pub fn closeSnapshot(self: *TerminalSession, query_process: bool) close_confirmation.PaneCloseSnapshot {
        if (query_process and self.state == .running) {
            // A destructive action must not reuse an idle-shell result from the UI polling interval.
            self.last_icon_probe_ms = std.time.milliTimestamp();
            _ = self.refreshAppIcon(true);
        }
        const prog_status = self.terminal.program_status.programStatus();
        const status_app = prog_status.app orelse "";
        const status_running = prog_status.state == .working or prog_status.state == .blocked;
        const shell_activity = self.terminal.program_status.shellActivityState();
        return .{
            .is_running = self.state == .running,
            .title = self.terminal.title.slice(),
            .status_app = if (status_running) status_app else "",
            .status_active = status_running or shell_activity == .working or shell_activity == .blocked,
            .has_foreground_process = self.has_foreground_process,
            .foreground_shell_command = self.foreground_shell_command,
            .foreground_is_shell = self.foreground_is_shell,
            .foreground_is_root = self.foreground_is_root,
            .active_process_title = if (self.active_process_title_len > 0)
                self.active_process_title[0..self.active_process_title_len]
            else if (status_running)
                status_app
            else
                "",
            .icon = self.icon,
        };
    }
};

fn shellRunsCommand(is_root: bool, cmd: []const u8) bool {
    // A shell profile owns its startup arguments. OSC 133 and children describe its live work.
    if (is_root) return false;
    return command_line.shellHasCommand(cmd);
}

test "profile shells are name-independent and startup options do not imply live work" {
    for ([_][]const u8{
        "pwsh -NoExit -EncodedCommand AAAA",
        "never-seen-shell --config 'my config'",
        "my-shell --initialize 'prompt setup'",
    }) |cmd| try std.testing.expect(!shellRunsCommand(true, cmd));
    try std.testing.expect(shellRunsCommand(false, "pwsh -File work.ps1"));
    try std.testing.expect(shellRunsCommand(false, "bash -c 'sleep 100'"));
    try std.testing.expect(!shellRunsCommand(false, "never-seen-shell"));
    try std.testing.expect(!shellRunsCommand(false, "bash -il"));
}

fn resolveForegroundProcessTitle(proc_name: ?[]const u8, proc_cmd: ?[]const u8) ?[]const u8 {
    const raw_exe = proc_name orelse return null;
    if (proc_cmd) |cmd| {
        if (command_line.parseCommandLineApp(cmd)) |app| return app;
    }
    const name = command_line.executableStem(raw_exe);
    return if (name.len > 0) name else null;
}

test "copy feedback progress advances and clears on duration or scroll change" {
    var fb: CopyFeedback = .{};
    try std.testing.expectEqual(@as(?f32, null), fb.progress(1000, 0, true));

    fb.spans[0] = .{ .row = 2, .start_col = 4, .end_col = 18 };
    fb.count = 1;
    fb.scroll_offset = 5;
    fb.started_ms = 1000;

    try std.testing.expectApproxEqAbs(@as(f32, 0.0), fb.progress(1000, 5, true).?, 0.001);
    try std.testing.expectApproxEqAbs(@as(f32, 0.5), fb.progress(1220, 5, true).?, 0.001);
    try std.testing.expectEqual(@as(?f32, null), fb.progress(1440, 5, true));
    try std.testing.expectEqual(@as(u8, 0), fb.count);

    fb.spans[0] = .{ .row = 0, .start_col = 0, .end_col = 10 };
    fb.count = 1;
    fb.scroll_offset = 5;
    fb.started_ms = 2000;
    try std.testing.expectEqual(@as(?f32, null), fb.progress(2050, 6, true));
    try std.testing.expectEqual(@as(?u64, null), fb.started_ms);
}
