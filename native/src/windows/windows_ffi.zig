const std = @import("std");
const panes = @import("../workspace/pane_tree.zig");
const terminal = @import("../terminal/terminal.zig");
const directory = @import("../shell/directory.zig");
const status = @import("../terminal/program_status.zig");
const shell = @import("../shell/shell_launch.zig");
const tab_strip = @import("../workspace/tab_strip.zig");

/// Raw Windows bridge declarations stay confined to native IO modules.
pub const c = @cImport({
    @cInclude("windows_platform.h");
});
/// Workspace controls use explicit actions, not terminal key escape sequences.
pub const WorkspaceAction = enum(u32) { new_tab = 0, split_right = 1, split_down = 2, close_active = 3, next_tab = 4, previous_tab = 5, select_tab = 6, close_tab = 7 };
/// Pointer events are checked at the Win32 seam; their coordinates remain DIPs even during capture outside the window.
pub const PointerEvent = enum(u32) { hover = c.VEX_POINTER_HOVER, begin = c.VEX_POINTER_BEGIN, move = c.VEX_POINTER_MOVE, end = c.VEX_POINTER_END, cancel = c.VEX_POINTER_CANCEL };
/// Win32 owns capture: armed tabs wait for the drag threshold, while divider/tab drags request immediate capture.
pub const PointerResponse = enum(u32) { normal = c.VEX_POINTER_NORMAL, right = c.VEX_POINTER_RIGHT, down = c.VEX_POINTER_DOWN, tab_armed = c.VEX_POINTER_TAB_ARMED, tab_dragging = c.VEX_POINTER_TAB_DRAGGING };
/// Window sizes/font metrics are in DIPs; only cell pixel metrics multiply by scale.
pub const WindowMetrics = struct {
    width: f32,
    height: f32,
    cell_width: f32,
    cell_height: f32,
    scale: f32,
    /// Validate floats before any float-to-integer conversion or allocation.
    pub fn validate(self: WindowMetrics) !void {
        for ([_]f32{ self.width, self.height, self.cell_width, self.cell_height, self.scale }) |value| {
            if (!std.math.isFinite(value) or value <= 0 or value > 65536) return error.InvalidWindowMetrics;
        }
    }
    /// Bounded terminal grid matches the pane's inner surface, including header/padding.
    pub fn terminalSize(self: WindowMetrics, bounds: panes.PaneRect) !TerminalSize {
        try self.validate();
        try bounds.validate();
        return .{
            .columns = @intFromFloat(std.math.clamp(@floor((bounds.width - 12) / self.cell_width), 2, 1000)),
            .rows = @intFromFloat(std.math.clamp(@floor((bounds.height - 36) / self.cell_height), 1, 1000)),
            .cell_width_px = @intFromFloat(std.math.clamp(@round(self.cell_width * self.scale), 1, 65536)),
            .cell_height_px = @intFromFloat(std.math.clamp(@round(self.cell_height * self.scale), 1, 65536)),
        };
    }
};
/// ConPTY-compatible grid dimensions and physical cell metrics.
pub const TerminalSize = struct { columns: u16, rows: u16, cell_width_px: u32, cell_height_px: u32 };

/// Uniquely owns a native PTY session; deinit joins both workers before freeing their buffers.
pub const PtyOwner = struct {
    handle: std.meta.Child(?*c.VexPty),
    /// Tokens must be fresh, nonzero pane IDs; failed startup releases all partial resources.
    pub fn init(allocator: std.mem.Allocator, id: panes.PaneId, path: directory.DirectoryPath, launch: *const shell.ShellLaunch) !PtyOwner {
        const program = try std.unicode.utf8ToUtf16LeAllocZ(allocator, launch.program);
        defer allocator.free(program);
        const command_bytes = try launch.commandForDirectoryAlloc(allocator, path);
        defer allocator.free(command_bytes);
        const command = try std.unicode.utf8ToUtf16LeAllocZ(allocator, command_bytes);
        defer allocator.free(command);
        // WSL receives its directory through --cd; CreateProcessW must not probe the Linux filesystem view.
        const windows_directory = if (launch.backend == .wsl) std.fs.path.dirname(launch.program) orelse return error.InvalidShellProgram else path.bytes;
        const working_directory = try std.unicode.utf8ToUtf16LeAllocZ(allocator, windows_directory);
        defer allocator.free(working_directory);
        var pointer: ?*c.VexPty = null;
        const native_launch: c.VexPtyLaunch = .{ .program = program.ptr, .program_length = program.len, .command = command.ptr, .command_length = command.len, .directory = working_directory.ptr, .directory_length = working_directory.len, .environment = launch.environment.ptr, .environment_length = launch.environment.len };
        if (c.vex_pty_open(@intFromEnum(id), 80, 24, native_launch, &pointer) != 0) return error.PtyLaunchFailed;
        return .{ .handle = pointer orelse return error.PtyNullHandle };
    }
    /// Consumes this owner. No registered callback or queued event retains its pointer.
    pub fn deinit(self: *PtyOwner) void {
        c.vex_pty_close(self.handle);
    }
    /// Queue input without blocking the window thread; overflow is never discarded silently.
    pub fn write(self: *const PtyOwner, bytes: []const u8) !void {
        if (bytes.len > 0 and !c.vex_pty_write(self.handle, bytes.ptr, bytes.len)) return error.PtyWriteFailed;
    }
    /// Resize the process before publishing new emulator geometry.
    pub fn resize(self: *const PtyOwner, size: TerminalSize) !void {
        if (!c.vex_pty_resize(self.handle, size.columns, size.rows)) return error.PtyResizeFailed;
    }
    /// The process ID is diagnostic only, never a resource-owning handle.
    pub fn processId(self: *const PtyOwner) u32 {
        return c.vex_pty_process_id(self.handle);
    }
    /// The reply context is the native handle, not a pointer into a movable Zig array.
    pub fn responseSink(self: *const PtyOwner) terminal.ResponseSink {
        return .{ .context = self.handle, .write = writeReply };
    }
    fn writeReply(context: *anyopaque, bytes: []const u8) !void {
        const handle: *c.VexPty = @ptrCast(@alignCast(context));
        if (bytes.len > 0 and !c.vex_pty_write(handle, bytes.ptr, bytes.len)) return error.PtyReplyWriteFailed;
    }
};

/// Exact implemented workspace chords; plain Ctrl keys remain terminal input except Ctrl+Tab.
pub fn workspaceShortcut(key: u32, modifiers: u32) ?WorkspaceAction {
    if (modifiers == 3) return switch (key) {
        'T' => .new_tab,
        'R' => .split_right,
        'D' => .split_down,
        'W' => .close_active,
        9 => .previous_tab,
        else => null,
    };
    if (modifiers == 2 and key == 9) return .next_tab;
    return null;
}

/// The owner stays at a stable address until the native loop returns; all callback casts live here.
pub fn runWindow(owner: anytype) !void {
    const Owner = @TypeOf(owner);
    const Adapter = struct {
        fn recover(context: ?*anyopaque) ?Owner {
            return if (context) |pointer| @ptrCast(@alignCast(pointer)) else null;
        }
        fn output(context: ?*anyopaque, token: u32, bytes: [*c]const u8, length: usize) callconv(.c) bool {
            const app = recover(context) orelse return false;
            if (token == 0 or length > 65536 or (length != 0 and bytes == null)) {
                app.fail(error.InvalidPtyOutput);
                return false;
            }
            return app.onOutput(@enumFromInt(token), if (length == 0) &.{} else bytes[0..length]);
        }
        fn resize(context: ?*anyopaque, width: f32, height: f32, cell_width: f32, cell_height: f32, scale: f32) callconv(.c) void {
            const app = recover(context) orelse return;
            const metrics: WindowMetrics = .{ .width = width, .height = height, .cell_width = cell_width, .cell_height = cell_height, .scale = scale };
            metrics.validate() catch |err| {
                app.fail(err);
                return;
            };
            app.onResize(metrics);
        }
        fn paint(context: ?*anyopaque) callconv(.c) void {
            if (recover(context)) |app| app.onPaint();
        }
        fn key(context: ?*anyopaque, virtual_key: u32, modifiers: u32) callconv(.c) bool {
            const app = recover(context) orelse return false;
            return app.onKey(virtual_key, modifiers);
        }
        fn text(context: ?*anyopaque, bytes: [*c]const u8, length: usize) callconv(.c) void {
            const app = recover(context) orelse return;
            if (length > 1024 * 1024 or (length != 0 and bytes == null)) {
                app.fail(error.InvalidTextInput);
                return;
            }
            const input: []const u8 = if (length == 0) &.{} else bytes[0..length];
            if (!std.unicode.utf8ValidateSlice(input)) {
                app.fail(error.InvalidTextUtf8);
                return;
            }
            app.onText(input);
        }
        fn scroll(context: ?*anyopaque, delta: i32) callconv(.c) void {
            if (recover(context)) |app| app.onScroll(delta);
        }
        fn click(context: ?*anyopaque, x: f32, y: f32) callconv(.c) void {
            if (std.math.isFinite(x) and std.math.isFinite(y)) if (recover(context)) |app| app.onClick(x, y);
        }
        fn pointerInput(context: ?*anyopaque, value: u32, x: f32, y: f32) callconv(.c) u32 {
            const app = recover(context) orelse return 0;
            const event = std.meta.intToEnum(PointerEvent, value) catch {
                app.fail(error.InvalidPointerEvent);
                return 0;
            };
            if (!std.math.isFinite(x) or !std.math.isFinite(y)) {
                app.fail(error.InvalidDividerPoint);
                return 0;
            }
            return @intFromEnum(app.onPointer(event, x, y));
        }
        fn action(context: ?*anyopaque, value: u32, index: u32) callconv(.c) void {
            const app = recover(context) orelse return;
            const parsed = std.meta.intToEnum(WorkspaceAction, value) catch {
                app.fail(error.InvalidWorkspaceAction);
                return;
            };
            app.onAction(parsed, index);
        }
        fn exited(context: ?*anyopaque, token: u32, code: u32) callconv(.c) void {
            if (token != 0) if (recover(context)) |app| app.onExited(@enumFromInt(token), code);
        }
        fn probe(context: ?*anyopaque, query: u32) callconv(.c) u64 {
            const app = recover(context) orelse return 0;
            return app.onProbe(query);
        }
    };
    const code = c.vex_window_run(.{ .context = owner, .output = Adapter.output, .resize = Adapter.resize, .paint = Adapter.paint, .key = Adapter.key, .text = Adapter.text, .scroll = Adapter.scroll, .click = Adapter.click, .pointer = Adapter.pointerInput, .action = Adapter.action, .exited = Adapter.exited, .probe = Adapter.probe });
    if (code != 0) return error.WindowsPlatformFailed;
}

/// Clears an armed gesture or releases only this window's capture; capture-change callbacks may reenter the application.
pub fn cancelPointerCapture() !void {
    if (c.vex_window_cancel_pointer_capture() != 0) return error.PointerCaptureReleaseFailed;
}
/// Diagnostic capture check runs on the window thread, not a test thread's unrelated capture state.
pub fn hasPointerCapture() bool {
    return c.vex_window_has_pointer_capture();
}
/// Publishes parsed tab-strip geometry before paint or hit testing; the bridge copies scalars, not model pointers.
pub fn setTabStripLayout(layout: tab_strip.TabStripLayout) !void {
    if (c.vex_window_tab_strip(@intCast(layout.count), layout.tab_width) != 0) return error.InvalidTabStripGeometry;
}
/// Draws a finite DIP insertion marker only during the current frame.
pub fn drawTabInsertionMarker(x: f32) !void {
    if (c.vex_draw_tab_insertion_marker(x) != 0) return error.TabMarkerRenderingFailed;
}

/// Returns the actual local machine name, not a spoofable environment variable; caller owns the UTF-8 copy.
pub fn machineNameAlloc(allocator: std.mem.Allocator) ![]u8 {
    var buffer: [64]u8 = undefined;
    var length: usize = 0;
    if (c.vex_machine_name(&buffer, buffer.len, &length) != 0) return error.MachineNameReadFailed;
    if (length == 0 or length > buffer.len or !std.unicode.utf8ValidateSlice(buffer[0..length])) return error.InvalidMachineName;
    return allocator.dupe(u8, buffer[0..length]);
}
/// Copies a resolved shell display name into native chrome before opening the window.
pub fn setShellName(name: []const u8) !void {
    if (c.vex_window_shell_name(name.ptr, name.len) != 0) return error.InvalidShellDisplayName;
}
/// Resolves the actual Windows system directory without trusting WINDIR/SystemRoot environment variables.
pub fn systemDirectoryAlloc(allocator: std.mem.Allocator) ![]u8 {
    var buffer: [1024]u8 = undefined;
    var length: usize = 0;
    if (c.vex_system_directory(&buffer, buffer.len, &length) != 0) return error.SystemDirectoryReadFailed;
    if (length == 0 or length > buffer.len) return error.InvalidSystemDirectory;
    _ = try directory.DirectoryPath.parse(buffer[0..length]);
    return allocator.dupe(u8, buffer[0..length]);
}
/// Startup timestamps begin before terminal/DLL initialization, not OS process creation.
pub fn beginStartup() void {
    c.vex_startup_begin();
}
/// Coalesces presentation requests without a repeating idle timer.
pub fn invalidateWindow() void {
    c.vex_window_invalidate();
}
/// Counts native paint calls for opt-in desktop diagnostics, not GPU presentations.
pub fn paintCount() u64 {
    return c.vex_window_paint_count();
}
/// Stop the loop before reporting a fatal callback error; avoid modal reentrancy into live owners.
pub fn closeWindow() void {
    c.vex_window_close();
}
/// Fatal failures close the native window after showing one diagnostic message.
pub fn showFailure(err: anyerror) void {
    var buffer: [256]u8 = undefined;
    const message = std.fmt.bufPrintZ(&buffer, "Vex native rewrite failed: {s}", .{@errorName(err)}) catch "Vex native rewrite failed";
    c.vex_window_error(message);
}
/// Tab index/count come from the bounded workspace model, not external strings.
pub fn drawTab(index: usize, count: usize, active: bool, summary: status.ProgramStatusSummary, attention: bool) void {
    c.vex_draw_tab(@intCast(index), @intCast(count), active, nativeStatusBadge(summary), attention);
}
fn nativeStatusBadge(summary: status.ProgramStatusSummary) c.VexProgramStatus {
    return .{ .state = @intFromEnum(summary.state), .kind = if (summary.kind) |kind| switch (kind) {
        .permission => 1,
        .question => 2,
        .auth => 3,
    } else 0, .progress = if (summary.progress) |progress| progress else -1 };
}
/// Only visible badge fields affect hidden-output repaint scheduling; metadata remains owned by the tracker.
pub fn statusBadgeKey(summary: status.ProgramStatusSummary) u32 {
    const badge = nativeStatusBadge(summary);
    return badge.state | (badge.kind << 8) | (@as(u32, @intCast(badge.progress + 1)) << 16);
}
/// Begins a pane clip; every successful call must be paired with endPane, including errors.
pub fn beginPane(bounds: panes.PaneRect, focused: bool, summary: status.ProgramStatusSummary) void {
    c.vex_begin_pane(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height }, focused, nativeStatusBadge(summary));
}
/// Ends the current pane clip before drawing another pane or returning to chrome.
pub fn endPane() void {
    c.vex_end_pane();
}
/// Draws a borrowed parsed snapshot; no C handles or pointers enter the application model.
pub fn drawTerminalFrame(frame: *const terminal.TerminalFrame, focused: bool) !void {
    for (0..frame.rows) |row| {
        const cells = frame.cells.items[row * frame.columns ..][0..frame.columns];
        var start: usize = 0;
        var run_cells: usize = 0;
        var buffer: [4096]u8 = undefined;
        var length: usize = 0;
        var style: terminal.TextStyle = .{ .foreground = 0xe4e4e7, .background = 0x282c34 };
        for (cells, 0..) |cell, column| {
            if (cell.width == .tail) continue;
            const text = frame.cellText(cell);
            const ascii = cell.width == .narrow and (text.len == 0 or (text.len == 1 and text[0] >= 32 and text[0] <= 126));
            if (!ascii or !std.meta.eql(style, cell.style) or length == buffer.len) {
                try drawRun(start, row, run_cells, buffer[0..length], style);
                run_cells = 0;
                length = 0;
                start = column;
                style = cell.style;
            }
            if (!ascii) {
                const width: usize = if (cell.width == .wide) 2 else 1;
                try drawRun(column, row, width, text, cell.style);
                start = column + width;
            } else {
                buffer[length] = if (text.len == 0) ' ' else text[0];
                length += 1;
                run_cells += 1;
            }
        }
        try drawRun(start, row, run_cells, buffer[0..length], style);
    }
    const cursor = frame.cursor;
    if (focused and cursor.visible and cursor.column < frame.columns and cursor.row < frame.rows) c.vex_draw_cursor(cursor.column, cursor.row, @intFromEnum(cursor.shape), 0xe4e4e7);
}
fn drawRun(column: usize, row: usize, cells: usize, text: []const u8, style: terminal.TextStyle) !void {
    if (cells == 0) return;
    const flags = @as(u32, @intFromBool(style.bold)) | (@as(u32, @intFromBool(style.italic)) << 1) |
        (@as(u32, @intFromBool(style.underline)) << 2) | (@as(u32, @intFromBool(style.strikethrough)) << 3) | (@as(u32, @intFromBool(style.invisible)) << 4);
    if (c.vex_draw_run(@intCast(column), @intCast(row), @intCast(cells), text.ptr, text.len, style.foreground, style.background, flags) != 0) return error.NativeTextRenderingFailed;
}

test "workspace shortcuts exactly match implemented WPF chords without stealing terminal Ctrl keys" {
    try std.testing.expectEqual(@as(?WorkspaceAction, .new_tab), workspaceShortcut('T', 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .split_right), workspaceShortcut('R', 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .split_down), workspaceShortcut('D', 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .close_active), workspaceShortcut('W', 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .next_tab), workspaceShortcut(9, 2));
    try std.testing.expectEqual(@as(?WorkspaceAction, .previous_tab), workspaceShortcut(9, 3));
    for ([_]u32{ 'T', 'R', 'D', 'W', 'C', 'S' }) |key| try std.testing.expectEqual(@as(?WorkspaceAction, null), workspaceShortcut(key, 2));
    try std.testing.expectEqual(@as(?WorkspaceAction, null), workspaceShortcut('T', 7));
}

test "window geometry rejects NaN/zero metrics and clamps tiny/large terminal viewports" {
    const valid: WindowMetrics = .{ .width = 1120, .height = 700, .cell_width = 8, .cell_height = 17, .scale = 1.25 };
    const small = try valid.terminalSize(.{ .x = 0, .y = 0, .width = 1, .height = 1 });
    try std.testing.expectEqual(@as(u16, 2), small.columns);
    try std.testing.expectEqual(@as(u16, 1), small.rows);
    var invalid = valid;
    invalid.cell_width = std.math.nan(f32);
    try std.testing.expectError(error.InvalidWindowMetrics, invalid.validate());
    invalid = valid;
    invalid.scale = 0;
    try std.testing.expectError(error.InvalidWindowMetrics, invalid.validate());
}
