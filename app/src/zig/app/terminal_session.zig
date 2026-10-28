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

const copy_effect = @import("copy_feedback.zig");

const SessionState = union(enum) { running, exited: u32 };

pub const CopyFeedback = struct {
    spans: std.ArrayList(platform.c.VexCopySpan) = .empty,
    scroll_offset: u64 = 0,
    started_ms: ?u64 = null,

    pub const duration_ms: u64 = 440;

    pub fn clear(self: *CopyFeedback) void {
        self.started_ms = null;
        self.spans.clearRetainingCapacity();
    }

    pub fn progress(self: *CopyFeedback, now_ms: u64, scroll_offset: u64, animations_enabled: bool) ?f32 {
        const started = self.started_ms orelse return null;
        if (!animations_enabled or self.spans.items.len == 0 or scroll_offset != self.scroll_offset) {
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
    copy_source: copy_effect.Snapshot = .{},
    pending_copy_text: ?[]u8 = null,
    pending_copy_ms: i64 = 0,
    smooth_scroll: @import("../workspace/smooth_scroll.zig").SmoothScroll = .{},
    wheel_scroll: @import("../workspace/wheel_scroll.zig").WheelScroll = .{},
    sync_defer_since_ms: ?i64 = null,
    icon: app_icon.AppIcon = app_icon.AppIcon.terminal_shell,
    process_icon: app_icon.AppIcon = app_icon.AppIcon.terminal_shell,
    process_probe: @import("../terminal/process_probe_schedule.zig").ProcessProbeSchedule = .{},
    active_process_title: [64]u8 = undefined,
    active_process_title_len: usize = 0,
    has_foreground_process: bool = false,
    foreground_shell_command: bool = false,
    foreground_is_shell: bool = false,
    foreground_is_root: bool = false,
    foreground_is_transport: bool = false,
    shell_launch: *const shell.ShellLaunch,
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
            .shell_launch = launch,
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
    pub fn onClipboardWrite(self: *TerminalSession, text: []const u8) !void {
        platform.writeClipboardText(text) catch |err| {
            // Clipboard contention is an operation failure, not a poisoned VT parser.
            platform.reportOperationFailure(err);
            return err;
        };
        self.queueCopyFeedback(text);
    }

    fn discardPendingCopy(self: *TerminalSession) void {
        if (self.pending_copy_text) |text| self.terminal.allocator.free(text);
        self.pending_copy_text = null;
    }

    /// OSC callbacks and attributed Windows clipboard notifications use the same
    /// validation path. Reading the emulator inside its parser callback is unsafe.
    pub fn queueCopyFeedback(self: *TerminalSession, text: []const u8) void {
        const now = std.time.milliTimestamp();
        if (!self.copy_source.recent(now) or !platform.animationsEnabled()) return;
        self.copy_feedback.clear();
        self.discardPendingCopy();
        if (std.mem.trim(u8, text, " \t\r\n").len == 0) return;
        self.pending_copy_text = self.terminal.allocator.dupe(u8, text) catch return;
        self.pending_copy_ms = now;
        platform.scheduleCopyCheck(copy_effect.settle_ms);
    }

    pub fn deferCopyFeedback(self: *TerminalSession) void {
        if (self.pending_copy_text == null) return;
        self.pending_copy_ms = std.time.milliTimestamp();
        platform.scheduleCopyCheck(copy_effect.settle_ms);
    }

    pub fn cancelApplicationCopy(self: *TerminalSession) void {
        self.discardPendingCopy();
        self.copy_feedback.clear();
        self.copy_source.captured_ms = null;
    }

    pub fn finishCopyFeedback(self: *TerminalSession, now_ms: u64, visible: bool) bool {
        const text = self.pending_copy_text orelse return false;
        const now = std.time.milliTimestamp();
        const elapsed = now - self.pending_copy_ms;
        if (elapsed >= 0 and elapsed < copy_effect.settle_ms) {
            platform.scheduleCopyCheck(@intCast(copy_effect.settle_ms - elapsed));
            return false;
        }
        defer self.discardPendingCopy();
        if (!visible or !platform.animationsEnabled() or !self.copy_source.recent(now)) return false;
        const sb = self.terminal.scrollbar() catch return false;
        if (sb.offset != sb.scrollable()) return false;
        const frame = self.terminal.readFrame() catch return false;
        const box = copy_effect.unchanged(self.terminal.allocator, &self.copy_source, frame, text) orelse return false;
        self.copy_source.captured_ms = null;
        self.setCopyFeedback(frame, box, sb.offset, now_ms);
        return true;
    }

    /// Snapshot before any keyboard or mouse operation, independent of bindings.
    pub fn recordApplicationInput(self: *TerminalSession) void {
        self.cancelApplicationCopy();
        if (!platform.animationsEnabled()) return;
        const sb = self.terminal.scrollbar() catch return;
        if (sb.offset != sb.scrollable()) return;
        const frame = self.terminal.readFrame() catch return;
        self.copy_source.captureVersioned(self.terminal.allocator, frame, std.time.milliTimestamp()) catch {};
    }

    fn setCopyFeedback(self: *TerminalSession, frame: anytype, box: ?copy_effect.Box, offset: u64, now_ms: u64) void {
        copy_effect.collectContentSpans(self.terminal.allocator, &self.copy_feedback.spans, frame, box) catch {
            self.copy_feedback.clear();
            return;
        };
        if (self.copy_feedback.spans.items.len == 0) return;
        self.copy_feedback.scroll_offset = offset;
        self.copy_feedback.started_ms = now_ms;
    }
    /// Detaches the native token before releasing Zig owners; workers carry only native storage.
    pub fn destroy(self: *TerminalSession, allocator: std.mem.Allocator) void {
        self.pty.deinit();
        self.discardPendingCopy();
        self.copy_source.deinit(self.terminal.allocator);
        self.copy_feedback.spans.deinit(self.terminal.allocator);
        self.terminal.deinit();
        self.directory.deinit();
        allocator.destroy(self);
    }
    /// User input acknowledges completed results; emulator replies deliberately bypass this path.
    pub fn writeUserInput(self: *TerminalSession, bytes: []const u8) !void {
        if (bytes.len == 0) return;
        self.smooth_scroll.clear();
        self.recordApplicationInput();
        const previous = status_presentation.statusPresentationKey(self.terminal.program_status.programStatus());
        self.terminal.program_status.acknowledgeProgramStatus();
        if (previous != status_presentation.statusPresentationKey(self.terminal.program_status.programStatus())) platform.invalidateWindow();
        try self.writePty(bytes);
        if (try self.terminal.revealInput()) {
            self.copy_feedback.clear();
            platform.invalidateWindow();
        }
        if (self.process_probe.noteInput(std.time.milliTimestamp(), bytes)) {
            self.pty.noteProcessInput();
            platform.scheduleDeferredTick(50);
        }
    }
    /// A dead transport is quarantined; its screen remains available for copying.
    pub fn stopAfterTransportFailure(self: *TerminalSession, code: u32) void {
        if (self.pty.handle == null) return;
        // Late output can fail after the exit watcher ran; keep the real process exit code.
        if (self.state == .running) self.state = .{ .exited = code };
        self.terminal.response_sink = null;
        // Only transport callback failures reach this recovery path.
        if (self.terminal.callback_error) |err| {
            if (err == error.PtyReplyWriteFailed) self.terminal.callback_error = null;
        }
        self.terminal.program_status.onProgramExited();
        self.cancelApplicationCopy();
        self.pty.deinit();
    }
    pub fn writePty(self: *TerminalSession, bytes: []const u8) !void {
        if (self.state != .running) return error.SessionTransportFailed;
        self.pty.write(bytes) catch |err| {
            if (err != error.PtyWriteFailed) return err;
            self.stopAfterTransportFailure(29); // ERROR_WRITE_FAULT
            return error.SessionTransportFailed;
        };
    }
    /// Normalizes clipboard newlines and applies bracketed paste when the active shell/TUI requested DEC 2004.
    pub fn paste(self: *TerminalSession, allocator: std.mem.Allocator, text: []const u8) !void {
        if (text.len == 0) return;
        const encoded = try self.terminal.encodePasteAlloc(allocator, text);
        defer allocator.free(encoded);
        try self.writeUserInput(encoded);
        self.copy_source.captured_ms = null;
    }
    /// Copies the active terminal selection to the system clipboard when a selection range is present.
    pub fn copySelectionToClipboard(self: *TerminalSession, allocator: std.mem.Allocator) !bool {
        const selected = try self.terminal.selectedTextAlloc(allocator, true) orelse return false;
        defer allocator.free(selected);
        if (selected.len > 0) try platform.writeClipboardText(selected);
        return true;
    }
    /// Capture feedback before clearing; pointer copies retain multi-click history.
    pub fn copyAndClearSelection(self: *TerminalSession, allocator: std.mem.Allocator, now_ms: u64, reset_gesture: bool) !bool {
        if (!try self.copySelectionToClipboard(allocator)) return false;
        self.recordCopyFeedback(now_ms);
        try self.terminal.clearSelection(reset_gesture);
        return true;
    }
    /// Snapshots visible selected cell runs so the copy shimmer can animate even after selection clears.
    pub fn recordCopyFeedback(self: *TerminalSession, now_ms: u64) void {
        self.copy_feedback.clear();
        if (!platform.animationsEnabled()) return;
        const selected = (self.terminal.selectedTextAlloc(self.terminal.allocator, true) catch return) orelse return;
        defer self.terminal.allocator.free(selected);
        if (std.mem.trim(u8, selected, " \t\r\n").len == 0) return;
        const frame = self.terminal.readFrame() catch return;
        const sb = self.terminal.scrollbar() catch return;
        self.setCopyFeedback(frame, null, sb.offset, now_ms);
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
        self.cancelApplicationCopy();
        self.smooth_scroll.clear();
        if (self.state == .running) try self.pty.resize(size);
        try self.terminal.resize(size.columns, size.rows, size.cell_width_px, size.cell_height_px);
        self.size = size;
        if (self.state == .running and try self.terminal.inBandResize()) {
            var buffer: [64]u8 = undefined;
            const height_px: u64 = @as(u64, size.rows) * @as(u64, size.cell_height_px);
            const width_px: u64 = @as(u64, size.columns) * @as(u64, size.cell_width_px);
            const report = try std.fmt.bufPrint(&buffer, "\x1b[48;{d};{d};{d};{d}t", .{ size.rows, size.columns, height_px, width_px });
            try self.writePty(report);
        }
    }
    /// Retained exited sessions keep a persistent header label after their transient notification is dismissed.
    pub fn displayTitle(self: *const TerminalSession) []const u8 {
        if (self.state == .exited) return if (self.state.exited == 0) "Session finished" else "Process exited";
        const title = self.terminal.title.slice();
        if (title.len > 0) return title;
        if (self.active_process_title_len > 0) return self.active_process_title[0..self.active_process_title_len];
        return self.shell_name;
    }
    pub fn refreshAppIconIfDue(self: *TerminalSession, now: i64) bool {
        if (self.state != .running or !self.process_probe.due(now)) return false;
        self.process_probe.last_probe_ms = now;
        return self.refreshAppIcon(true);
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
            var cwd_buf: [directory.DirectoryPath.max_bytes]u8 = undefined;
            const fg = self.pty.foregroundProcess(&exe_buf, &cmd_buf, &cwd_buf);
            if (fg.cwd) |cwd| self.directory.applyProbedDirectory(cwd) catch {};
            const proc_name: ?[]const u8 = if (fg.exe.len > 0) fg.exe else null;
            const proc_cmd: ?[]const u8 = if (fg.cmd.len > 0) fg.cmd else null;
            self.foreground_is_shell = fg.is_shell;
            self.foreground_is_root = fg.is_root;
            self.foreground_is_transport = !fg.is_linux and isWindowsTransport(proc_name orelse "");
            self.foreground_shell_command = self.foreground_is_shell and shellRunsCommand(fg.is_root, fg.cmd);
            const resolved_proc_title = if (fg.is_linux) proc_name else resolveForegroundProcessTitle(proc_name, proc_cmd);
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
        self.process_probe.foreground = self.has_foreground_process;
        const title_changed = self.active_process_title_len != previous_title_len or
            !std.mem.eql(u8, previous_title_buf[0..previous_title_len], self.active_process_title[0..self.active_process_title_len]);
        return !previous_icon.eql(self.icon) or title_changed;
    }
    /// Captures a borrowed close-evaluation snapshot of the session's running/title/process state.
    pub fn closeSnapshot(self: *TerminalSession, query_process: bool) close_confirmation.PaneCloseSnapshot {
        if (query_process and self.state == .running) {
            // A destructive action must not reuse an idle-shell result from the UI polling interval.
            self.process_probe.last_probe_ms = std.time.milliTimestamp();
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
            .foreground_is_transport = self.foreground_is_transport,
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

fn isWindowsTransport(executable: []const u8) bool {
    const stem = command_line.executableStem(executable);
    return std.ascii.eqlIgnoreCase(stem, "wsl") or std.ascii.eqlIgnoreCase(stem, "wslhost");
}

test "WSL transport identity is separate from shell and application identity" {
    for ([_][]const u8{ "wsl", "WSL.EXE", "C:\\Windows\\System32\\wsl.exe", "wslhost.exe" }) |exe| {
        try std.testing.expect(isWindowsTransport(exe));
    }
    for ([_][]const u8{ "", "codex.exe", "my-shell.exe", "wsl-editor.exe", "pwsh.exe" }) |exe| {
        try std.testing.expect(!isWindowsTransport(exe));
    }
}

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
        if (command_line.parseCommandLineIdentity(cmd)) |app| {
            return if (app.is_local_script) command_line.commandExecutable(cmd) else app.name;
        }
    }
    const name = command_line.executableStem(raw_exe);
    return if (name.len > 0) name else null;
}

test "foreground captions retain package managers and local script runtimes" {
    const cases = [_][2][]const u8{
        .{ "npm run build", "npm" },
        .{ "pnpm test", "pnpm" },
        .{ "yarn lint", "yarn" },
        .{ "bun run backend.ts", "bun" },
        .{ "python server.py", "python" },
        .{ "uv run server.py", "uv" },
        .{ "uv run uvicorn backend:app", "uvicorn" },
        .{ "npx claude", "claude" },
    };
    for (cases) |case| {
        try std.testing.expectEqualStrings(case[1], resolveForegroundProcessTitle(command_line.commandExecutable(case[0]), case[0]).?);
    }
}

test "copy feedback progress advances and clears on duration or scroll change" {
    var fb: CopyFeedback = .{};
    defer fb.spans.deinit(std.testing.allocator);
    try std.testing.expectEqual(@as(?f32, null), fb.progress(1000, 0, true));

    try fb.spans.append(std.testing.allocator, .{ .row = 2, .start_col = 4, .end_col = 18 });
    fb.scroll_offset = 5;
    fb.started_ms = 1000;

    try std.testing.expectApproxEqAbs(@as(f32, 0.0), fb.progress(1000, 5, true).?, 0.001);
    try std.testing.expectApproxEqAbs(@as(f32, 0.5), fb.progress(1220, 5, true).?, 0.001);
    try std.testing.expectEqual(@as(?f32, null), fb.progress(1440, 5, true));
    try std.testing.expectEqual(@as(usize, 0), fb.spans.items.len);

    try fb.spans.append(std.testing.allocator, .{ .row = 0, .start_col = 0, .end_col = 10 });
    fb.scroll_offset = 5;
    fb.started_ms = 2000;
    try std.testing.expectEqual(@as(?f32, null), fb.progress(2050, 6, true));
    try std.testing.expectEqual(@as(?u64, null), fb.started_ms);
}
