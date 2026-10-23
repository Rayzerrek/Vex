const std = @import("std");
const panes = @import("../workspace/pane_tree.zig");
const terminal = @import("../terminal/terminal.zig");
const directory = @import("../shell/directory.zig");
const status = @import("../terminal/program_status.zig");
const status_presentation = @import("../terminal/status_presentation.zig");
const shell = @import("../shell/shell_launch.zig");
const tab_strip = @import("../workspace/tab_strip.zig");
const app_icon = @import("../terminal/app_icon.zig");
const terminal_theme = @import("../workspace/terminal_theme.zig");
const dialogs = @import("windows_dialog.zig");

/// Raw Windows bridge declarations stay confined to native IO modules.
pub const c = @cImport({
    @cInclude("windows_platform.h");
});
/// Workspace controls use explicit actions, not terminal key escape sequences.
pub const WorkspaceAction = enum(u32) { new_tab = 0, split_right = 1, split_down = 2, close_active = 3, next_tab = 4, previous_tab = 5, select_tab = 6, close_tab = 7, rename_tab = 8, command_palette = 9, theme_picker = 10, tab_peek = 11, path_completion = 12, zoom_in = 13, zoom_out = 14, zoom_reset = 15, request_close_tab = 16, toggle_cursor_blink = 17, shell_picker = 18, settings = 19 };
/// Pointer events are checked at the Win32 seam; their coordinates remain DIPs even during capture outside the window.
pub const PointerEvent = enum(u32) { hover = c.VEX_POINTER_HOVER, begin = c.VEX_POINTER_BEGIN, move = c.VEX_POINTER_MOVE, end = c.VEX_POINTER_END, cancel = c.VEX_POINTER_CANCEL };
/// Win32 owns capture: armed tabs wait for the drag threshold, while divider/tab/selection drags request immediate capture.
pub const PointerResponse = enum(u32) { normal = c.VEX_POINTER_NORMAL, right = c.VEX_POINTER_RIGHT, down = c.VEX_POINTER_DOWN, tab_armed = c.VEX_POINTER_TAB_ARMED, tab_dragging = c.VEX_POINTER_TAB_DRAGGING, selection = c.VEX_POINTER_SELECTION, link = c.VEX_POINTER_LINK, text = c.VEX_POINTER_TEXT };
/// Span of cells within a single viewport row highlighted as a clickable URL.
pub const HoveredLinkSpan = struct { row: u16, start_col: u16, end_col: u16 };
/// Viewport cell coordinate and physical surface position relative to a pane's terminal grid.
pub const ViewportPoint = struct { column: u16, row: u16, x_px: f64, y_px: f64 };
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
            .columns = @intFromFloat(std.math.clamp(@floor((bounds.width - 24) / self.cell_width), 2, 1000)),
            .rows = @intFromFloat(std.math.clamp(@floor((bounds.height - 36) / self.cell_height), 1, 1000)),
            .cell_width_px = @intFromFloat(std.math.clamp(@round(self.cell_width * self.scale), 1, 65536)),
            .cell_height_px = @intFromFloat(std.math.clamp(@round(self.cell_height * self.scale), 1, 65536)),
        };
    }
    /// Maps a window DIP point into a pane's terminal viewport, optionally clamping outside drags to the grid.
    pub fn viewportPoint(self: WindowMetrics, bounds: panes.PaneRect, x: f32, y: f32, clamp_outside: bool) !?ViewportPoint {
        if (!std.math.isFinite(x) or !std.math.isFinite(y)) return error.InvalidWindowMetrics;
        const size = try self.terminalSize(bounds);
        const grid_left = bounds.x + 6;
        const grid_top = bounds.y + 30;
        const grid_right = bounds.x + bounds.width - 18;
        const grid_bottom = bounds.y + bounds.height - 6;
        if (grid_right <= grid_left or grid_bottom <= grid_top) return null;
        if (!clamp_outside and (x < grid_left or x >= grid_right or y < grid_top or y >= grid_bottom)) return null;
        const max_x = @max(0, @as(f32, @floatFromInt(size.columns)) * self.cell_width - 0.001);
        const max_y = @max(0, @as(f32, @floatFromInt(size.rows)) * self.cell_height - 0.001);
        const clamped_x = std.math.clamp(x - grid_left, 0, max_x);
        const clamped_y = std.math.clamp(y - grid_top, 0, max_y);
        const column: u16 = @intFromFloat(std.math.clamp(@floor(clamped_x / self.cell_width), 0, @as(f32, @floatFromInt(size.columns - 1))));
        const row: u16 = @intFromFloat(std.math.clamp(@floor(clamped_y / self.cell_height), 0, @as(f32, @floatFromInt(size.rows - 1))));
        return .{
            .column = column,
            .row = row,
            .x_px = @as(f64, clamped_x) * @as(f64, self.scale),
            .y_px = @as(f64, clamped_y) * @as(f64, self.scale),
        };
    }
};
/// ConPTY-compatible grid dimensions and physical cell metrics.
pub const TerminalSize = struct { columns: u16, rows: u16, cell_width_px: u32, cell_height_px: u32 };
/// Null means Windows requests a page per wheel notch.
pub fn wheelScrollLines() ?u32 {
    const lines = c.vex_window_wheel_scroll_lines();
    return if (lines == std.math.maxInt(u32)) null else @min(lines, 1000);
}
/// Computed DIP geometry for a pane's vertical scrollback overlay thumb and track.
pub const ScrollbarGeometry = struct {
    track_top: f32,
    track_height: f32,
    thumb_y: f32,
    thumb_height: f32,
    travel: f32,
    scrollable: u64,

    pub const hit_width: f32 = 16.0;

    /// Returns null when the pane has no scrollback or its viewport track is too short.
    pub fn compute(bounds: panes.PaneRect, info: terminal.ScrollbarInfo) ?ScrollbarGeometry {
        if (!info.isVisible()) return null;
        const track_top = bounds.y + 30.0;
        const track_height = bounds.height - 36.0;
        if (!std.math.isFinite(track_top) or !std.math.isFinite(track_height) or track_height < 12.0 or bounds.width < 24.0) return null;
        const total_f: f32 = @floatFromInt(@max(1, info.total));
        const len_f: f32 = @floatFromInt(info.len);
        const min_thumb = @min(24.0, track_height);
        const thumb_height = std.math.clamp(track_height * (len_f / total_f), min_thumb, track_height);
        const travel = @max(0.0, track_height - thumb_height);
        const scrollable = info.scrollable();
        const offset_f: f32 = @floatFromInt(@min(info.offset, scrollable));
        const scrollable_f: f32 = @floatFromInt(@max(1, scrollable));
        const thumb_y = track_top + travel * (offset_f / scrollable_f);
        return .{
            .track_top = track_top,
            .track_height = track_height,
            .thumb_y = thumb_y,
            .thumb_height = thumb_height,
            .travel = travel,
            .scrollable = scrollable,
        };
    }
    /// True when (x, y) is inside the right-edge scrollbar hit strip.
    pub fn containsPoint(self: ScrollbarGeometry, bounds: panes.PaneRect, x: f32, y: f32) bool {
        const hit_left = @max(bounds.x, bounds.x + bounds.width - hit_width);
        return x >= hit_left and x < bounds.x + bounds.width and y >= self.track_top and y < self.track_top + self.track_height;
    }
    /// True when y falls on the draggable thumb rather than the track above/below it.
    pub fn containsThumb(self: ScrollbarGeometry, y: f32) bool {
        return y >= self.thumb_y and y <= self.thumb_y + self.thumb_height;
    }
    /// Drag feedback follows the pointer, not the rounded scrollback row selected by the emulator.
    pub fn thumbTopForY(self: ScrollbarGeometry, y: f32, grab_offset: f32) f32 {
        return self.track_top + std.math.clamp(y - self.track_top - grab_offset, 0, self.travel);
    }
    /// Maps a dragged pointer Y and initial grab offset to a target scrollback row offset.
    pub fn targetOffsetForY(self: ScrollbarGeometry, y: f32, grab_offset: f32) u64 {
        if (self.travel <= 0.0 or self.scrollable == 0) return 0;
        const desired = self.thumbTopForY(y, grab_offset) - self.track_top;
        const ratio: f64 = @as(f64, desired) / @as(f64, self.travel);
        const target_f = @round(ratio * @as(f64, @floatFromInt(self.scrollable)));
        return @intFromFloat(std.math.clamp(target_f, 0.0, @as(f64, @floatFromInt(self.scrollable))));
    }
};

var last_pty_launch_error: u32 = 0;
pub fn lastPtyLaunchError() u32 {
    return last_pty_launch_error;
}
pub fn resetPtyLaunchError() void {
    last_pty_launch_error = 0;
}

/// Uniquely owns a native PTY token; deinit detaches it before asynchronous native teardown.
pub const PtyOwner = struct {
    handle: ?*c.VexPty,
    /// Tokens must be fresh, nonzero pane IDs; failed startup releases all partial resources.
    pub fn init(allocator: std.mem.Allocator, id: panes.PaneId, path: directory.DirectoryPath, launch: *const shell.ShellLaunch) !PtyOwner {
        const program = try std.unicode.utf8ToUtf16LeAllocZ(allocator, launch.program);
        defer allocator.free(program);
        const wsl_process = @import("../shell/wsl_process.zig");
        var random: [16]u8 = undefined;
        std.crypto.random.bytes(&random);
        const token = std.fmt.bytesToHex(random, .lower);
        const command_bytes = try launch.commandForSessionAlloc(allocator, path, &token);
        defer allocator.free(command_bytes);
        const command = try std.unicode.utf8ToUtf16LeAllocZ(allocator, command_bytes);
        defer allocator.free(command);
        // WSL receives its directory through --cd; CreateProcessW must not probe the Linux filesystem view.
        const windows_directory = if (launch.backend == .wsl) std.fs.path.dirname(launch.program) orelse return error.InvalidShellProgram else path.bytes;
        const working_directory = try std.unicode.utf8ToUtf16LeAllocZ(allocator, windows_directory);
        defer allocator.free(working_directory);
        const probe_bytes = if (launch.backend == .wsl) try wsl_process.probeCommandAlloc(allocator, launch.program, launch.backend.wsl.distribution.bytes, &token) else null;
        defer if (probe_bytes) |owned| allocator.free(owned);
        const probe = if (probe_bytes) |text| try std.unicode.utf8ToUtf16LeAllocZ(allocator, text) else null;
        defer if (probe) |owned| allocator.free(owned);
        var pointer: ?*c.VexPty = null;
        const native_launch: c.VexPtyLaunch = .{ .program = program.ptr, .program_length = program.len, .command = command.ptr, .command_length = command.len, .directory = working_directory.ptr, .directory_length = working_directory.len, .environment = launch.environment.ptr, .environment_length = launch.environment.len, .declared_shell = launch.kind != .custom, .wsl_probe_command = if (probe) |value| value.ptr else null, .wsl_probe_command_length = if (probe) |value| value.len else 0 };
        const result = c.vex_pty_open(@intFromEnum(id), 80, 24, native_launch, &pointer);
        last_pty_launch_error = @bitCast(result);
        if (result != 0) return error.PtyLaunchFailed;
        return .{ .handle = pointer orelse return error.PtyNullHandle };
    }
    /// Consumes this owner. Native workers retain only native storage, never a Zig owner.
    pub fn deinit(self: *PtyOwner) void {
        c.vex_pty_close(self.handle);
        self.handle = null;
    }
    /// Queue input without blocking the window thread; overflow is never discarded silently.
    pub fn write(self: *const PtyOwner, bytes: []const u8) !void {
        if (bytes.len == 0) return;
        const result = c.vex_pty_write(self.handle, bytes.ptr, bytes.len);
        if (result == c.VEX_PTY_INPUT_FULL) return error.PtyInputFull;
        if (result != 0) return error.PtyWriteFailed;
    }
    /// Resize the process before publishing new emulator geometry.
    pub fn resize(self: *const PtyOwner, size: TerminalSize) !void {
        if (!c.vex_pty_resize(self.handle, size.columns, size.rows)) return error.PtyResizeFailed;
    }
    /// The process ID is diagnostic only, never a resource-owning handle.
    pub fn processId(self: *const PtyOwner) u32 {
        return c.vex_pty_process_id(self.handle);
    }
    /// Queries the foreground application or fallback shell, preserving root identity for bootstrap handling.
    pub fn foregroundProcess(self: *const PtyOwner, exe_buf: []u8, cmd_buf: []u8) struct { exe: []const u8, cmd: []const u8, is_root: bool = false, is_shell: bool = false, is_linux: bool = false } {
        if (exe_buf.len == 0 or cmd_buf.len == 0) return .{ .exe = &.{}, .cmd = &.{} };
        const wsl_process = @import("../shell/wsl_process.zig");
        var snapshot: [wsl_process.max_snapshot_bytes]u8 = undefined;
        var snapshot_len: usize = 0;
        if (c.vex_pty_wsl_snapshot(self.handle, &snapshot, snapshot.len, &snapshot_len) and snapshot_len <= snapshot.len) {
            if (wsl_process.parseSnapshot(snapshot[0..snapshot_len])) |process| {
                const exe = @import("../terminal/terminal_title.zig").sanitizeDisplayText(process.name, exe_buf) orelse "";
                const cmd = @import("../terminal/terminal_title.zig").sanitizeDisplayText(process.command, cmd_buf) orelse "";
                return .{ .exe = exe, .cmd = cmd, .is_root = process.is_shell, .is_shell = process.is_shell, .is_linux = true };
            }
        }
        var exe_len: usize = 0;
        var cmd_len: usize = 0;
        var is_root = false;
        var is_shell = false;
        if (c.vex_pty_foreground_process(self.handle, exe_buf.ptr, exe_buf.len, &exe_len, cmd_buf.ptr, cmd_buf.len, &cmd_len, &is_root, &is_shell) != 0) {
            return .{ .exe = &.{}, .cmd = &.{} };
        }
        const safe_exe = if (exe_len <= exe_buf.len and std.unicode.utf8ValidateSlice(exe_buf[0..exe_len])) exe_buf[0..exe_len] else &.{};
        const safe_cmd = if (cmd_len <= cmd_buf.len and std.unicode.utf8ValidateSlice(cmd_buf[0..cmd_len])) cmd_buf[0..cmd_len] else &.{};
        return .{ .exe = safe_exe, .cmd = safe_cmd, .is_root = is_root, .is_shell = is_shell };
    }
    /// The reply context is the native handle, not a pointer into a movable Zig array.
    pub fn responseSink(self: *const PtyOwner) terminal.ResponseSink {
        return .{ .context = self.handle orelse unreachable, .write = writeReply };
    }
    fn writeReply(context: *anyopaque, bytes: []const u8) !void {
        const handle: *c.VexPty = @ptrCast(@alignCast(context));
        if (bytes.len > 0 and c.vex_pty_write_response(handle, bytes.ptr, bytes.len) != 0) return error.PtyReplyWriteFailed;
    }
};

/// Join detached native owners before application shutdown/allocator diagnostics.
pub fn drainPtyClosures() void {
    c.vex_pty_drain_closed();
}

/// Exact implemented workspace chords; plain Ctrl keys remain terminal input except Ctrl+Tab and font zoom.
pub fn workspaceShortcut(key: u32, modifiers: u32) ?WorkspaceAction {
    if (modifiers == 3) return switch (key) {
        'T' => .new_tab,
        'R' => .split_right,
        'D' => .split_down,
        'W' => .close_active,
        'P' => .command_palette,
        'M' => .theme_picker,
        'F' => .path_completion,
        0x20 => .tab_peek,
        9 => .previous_tab,
        0xBB, 0x6B => .zoom_in,
        0xBD, 0x6D => .zoom_out,
        else => null,
    };
    if (modifiers == 2) return switch (key) {
        9 => .next_tab,
        0xBB, 0x6B => .zoom_in,
        0xBD, 0x6D => .zoom_out,
        '0', 0x60 => .zoom_reset,
        0xBC => .settings,
        else => null,
    };
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
        fn paste(context: ?*anyopaque, bytes: [*c]const u8, length: usize) callconv(.c) void {
            const app = recover(context) orelse return;
            if (length > 1024 * 1024 or (length != 0 and bytes == null)) {
                app.fail(error.InvalidPasteInput);
                return;
            }
            const input: []const u8 = if (length == 0) &.{} else bytes[0..length];
            if (!std.unicode.utf8ValidateSlice(input)) {
                app.fail(error.InvalidPasteUtf8);
                return;
            }
            app.onPaste(input);
        }
        fn clipboard(context: ?*anyopaque, token: u32, bytes: [*c]const u8, length: usize) callconv(.c) void {
            const app = recover(context) orelse return;
            if (length == 0 or length > 1024 * 1024 or bytes == null) return;
            const input = bytes[0..length];
            if (!std.unicode.utf8ValidateSlice(input)) return;
            app.onClipboard(token, input);
        }
        fn scroll(context: ?*anyopaque, delta: i32, x: f32, y: f32, modifiers: u32) callconv(.c) void {
            if (std.math.isFinite(x) and std.math.isFinite(y)) if (recover(context)) |app| app.onScroll(delta, x, y, modifiers);
        }
        fn click(context: ?*anyopaque, x: f32, y: f32) callconv(.c) void {
            if (std.math.isFinite(x) and std.math.isFinite(y)) if (recover(context)) |app| app.onClick(x, y);
        }
        fn mouseButton(context: ?*anyopaque, button: u32, pressed: bool, x: f32, y: f32, modifiers: u32) callconv(.c) void {
            const app = recover(context) orelse return;
            const parsed: terminal.MouseButton = switch (button) {
                1 => .left,
                2 => .right,
                3 => .middle,
                else => {
                    app.fail(error.InvalidMouseButton);
                    return;
                },
            };
            if (!std.math.isFinite(x) or !std.math.isFinite(y)) {
                app.fail(error.InvalidDividerPoint);
                return;
            }
            app.onMouseButton(parsed, pressed, x, y, modifiers);
        }
        fn pointerInput(context: ?*anyopaque, value: u32, x: f32, y: f32, modifiers: u32) callconv(.c) u32 {
            const app = recover(context) orelse return 0;
            const event = std.meta.intToEnum(PointerEvent, value) catch {
                app.fail(error.InvalidPointerEvent);
                return 0;
            };
            if (!std.math.isFinite(x) or !std.math.isFinite(y)) {
                app.fail(error.InvalidDividerPoint);
                return 0;
            }
            return @intFromEnum(app.onPointer(event, x, y, modifiers));
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
        fn sessionError(context: ?*anyopaque, token: u32, code: u32) callconv(.c) void {
            if (token != 0) if (recover(context)) |app| app.onSessionError(@enumFromInt(token), code);
        }
        fn tick(context: ?*anyopaque) callconv(.c) void {
            if (recover(context)) |app| app.onTick();
        }
        fn closeRequested(context: ?*anyopaque) callconv(.c) bool {
            const app = recover(context) orelse return true;
            return app.onCloseRequested();
        }
        fn cursorWake(context: ?*anyopaque) callconv(.c) void {
            if (recover(context)) |app| app.onCursorWake();
        }
        fn cursorActivity(context: ?*anyopaque, active: bool) callconv(.c) void {
            if (recover(context)) |app| app.onCursorActivity(active);
        }
        fn dialog(context: ?*anyopaque, event: u32, value: u32, bytes: [*c]const u8, length: usize, settings: c.VexUserSettings) callconv(.c) void {
            const app = recover(context) orelse return;
            if (length > (if (event == 7) @as(usize, 256 * 1024) else 128) or (length != 0 and bytes == null)) {
                app.fail(error.InvalidDialogInput);
                return;
            }
            const parsed = dialogs.decodeDialogEvent(event, value, if (length == 0) &.{} else bytes[0..length], settings) catch |err| {
                app.fail(err);
                return;
            };
            app.onDialogEvent(parsed);
        }
        fn probe(context: ?*anyopaque, query: u32) callconv(.c) u64 {
            const app = recover(context) orelse return 0;
            return app.onProbe(query);
        }
    };
    const code = c.vex_window_run(.{ .context = owner, .output = Adapter.output, .resize = Adapter.resize, .paint = Adapter.paint, .key = Adapter.key, .text = Adapter.text, .paste = Adapter.paste, .clipboard = Adapter.clipboard, .scroll = Adapter.scroll, .click = Adapter.click, .mouse_button = Adapter.mouseButton, .pointer = Adapter.pointerInput, .action = Adapter.action, .exited = Adapter.exited, .session_error = Adapter.sessionError, .tick = Adapter.tick, .cursor_wake = Adapter.cursorWake, .cursor_activity = Adapter.cursorActivity, .close_requested = Adapter.closeRequested, .probe = Adapter.probe, .dialog = Adapter.dialog });
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
/// Paints tab backgrounds before labels so moving selection never covers text.
pub fn drawTabStripBackground(active_index: usize) void {
    c.vex_draw_tab_strip_background(@intCast(active_index));
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
test "custom shell names fit native chrome without imposing a second shorter name limit" {
    defer setShellName("Command Prompt") catch {};
    try setShellName("x" ** 255);
    try setShellName("音" ** 85);
    try std.testing.expectError(error.InvalidShellDisplayName, setShellName("x" ** 256));
    try std.testing.expectError(error.InvalidShellDisplayName, setShellName("\xff"));
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
/// Preserves launcher overrides while making newly installed PATH executables discoverable.
pub fn shellEnvironment(allocator: std.mem.Allocator) !std.process.EnvMap {
    var environment = try std.process.getEnvMap(allocator);
    errdefer environment.deinit();
    var buffer: [128 * 1024]u8 = undefined;
    var length: usize = 0;
    if (c.vex_shell_path(&buffer, buffer.len, &length) == 0 and length > 0 and length <= buffer.len) {
        const inherited_path = environment.get("PATH") orelse "";
        // Usually the persisted and inherited PATH agree; don't probe every directory twice.
        if (std.ascii.eqlIgnoreCase(buffer[0..length], inherited_path)) return environment;
        const path = try std.fmt.allocPrint(allocator, "{s};{s}", .{ buffer[0..length], inherited_path });
        defer allocator.free(path);
        try environment.put("PATH", path);
    }
    return environment;
}
test "clipboard lock contention is reported instead of acknowledging a lost copy or cut" {
    const ClipboardLock = struct {
        const native = @cImport({
            @cInclude("windows.h");
        });

        ready: std.Thread.ResetEvent = .{},
        release: std.Thread.ResetEvent = .{},
        acquired: bool = false,

        fn hold(self: *@This()) void {
            const window = native.CreateWindowExW(0, std.unicode.utf8ToUtf16LeStringLiteral("STATIC"), null, 0, 0, 0, 1, 1, null, null, null, null);
            defer if (window != null) {
                _ = native.DestroyWindow(window);
            };
            self.acquired = window != null and native.OpenClipboard(window) != 0;
            self.ready.set();
            self.release.wait();
            if (self.acquired) _ = native.CloseClipboard();
        }
    };
    var lock: ClipboardLock = .{};
    const thread = try std.Thread.spawn(.{}, ClipboardLock.hold, .{&lock});
    defer {
        lock.release.set();
        thread.join();
    }
    lock.ready.wait();
    if (!lock.acquired) return error.SkipZigTest;
    try std.testing.expectError(error.ClipboardWriteFailed, writeClipboardText("keep this tab name"));
}

/// Publishes validated UTF-8 text; callers retain their source text when the clipboard is busy or fails.
pub fn writeClipboardText(text: []const u8) !void {
    if (text.len == 0 or std.mem.indexOfScalar(u8, text, 0) != null or !std.unicode.utf8ValidateSlice(text)) return error.InvalidClipboardText;
    if (c.vex_clipboard_write(text.ptr, text.len) != 0) return error.ClipboardWriteFailed;
}
/// Reads CF_UNICODETEXT from the system clipboard and routes it through the active session paste handler.
pub fn scheduleCopyCheck(delay_ms: u32) void {
    c.vex_window_schedule_copy_check(delay_ms);
}

pub fn pasteClipboard() void {
    c.vex_window_paste_clipboard();
}
/// Opens a validated http(s):// or file:// URL via the system browser.
pub fn openUrl(url: []const u8) !void {
    if (url.len == 0 or std.mem.indexOfScalar(u8, url, 0) != null or !std.unicode.utf8ValidateSlice(url)) return error.InvalidUrl;
    if (c.vex_open_url(url.ptr, url.len) != 0) return error.OpenUrlFailed;
}
/// Startup timestamps begin before terminal/DLL initialization, not OS process creation.
pub fn beginStartup() void {
    c.vex_startup_begin();
}
/// Coalesces presentation requests without a repeating idle timer.
pub fn invalidateWindow() void {
    c.vex_window_invalidate();
}
/// Extends the pending frame deadline up to 100ms while a DEC 2026 synchronized output block is open.
pub fn deferSynchronizedInvalidation() void {
    c.vex_window_defer_synchronized();
}
/// Counts native paint calls for opt-in desktop diagnostics, not GPU presentations.
pub fn paintCount() u64 {
    return c.vex_window_paint_count();
}
/// Arms a single cursor wake in milliseconds, or cancels it when the cursor is ineligible.
pub fn animationsEnabled() bool {
    return c.vex_window_animations_enabled();
}
pub fn scheduleCursorWake(delay_ms: ?u32) !void {
    if (c.vex_window_schedule_cursor_wake(delay_ms orelse 0) != 0) return error.CursorWakeSchedulingFailed;
}
/// Reports whether the native window currently owns a cursor animation timer.
pub fn cursorWakeArmed() bool {
    return c.vex_window_cursor_wake_armed();
}
/// Stop the loop before reporting a fatal callback error; avoid modal reentrancy into live owners.
pub fn closeWindow() void {
    c.vex_window_close();
}
var operation_notices: @import("../app/error_policy.zig").NoticeLimiter = .{};

/// Error-only, allocation-free path; no modal message loop or successful-input overhead.
pub fn reportOperationFailure(err: anyerror) void {
    if (!operation_notices.allow(err, std.time.milliTimestamp())) return;
    const message = switch (err) {
        error.PtyInputFull => "Input queue is full. This input was not sent. Wait for the program to consume pending input, then retry or paste a smaller block.",
        error.SessionTransportFailed => "The connection to this pane failed. Its process was stopped; the screen remains available for copying. Other panes are still running.",
        error.ClipboardWriteFailed => "Could not copy to the clipboard. It may be busy in another application. Please retry; your selection and sessions are preserved.",
        error.NativeDialogFailure => "The dialog could not be opened. Try again; your sessions remain open.",
        error.ProcessInitializationFailed => "Windows could not initialize the process (0xc0000142). The failed pane was retained; other sessions keep running. Try again or check Windows/WSL diagnostics.",
        error.ShellStartFailed => if (last_pty_launch_error == 170)
            "Previous terminals are still closing. Wait a moment and try again; existing sessions are unchanged."
        else
            "The shell could not be started. Check its executable and working directory. Existing sessions are unchanged.",
        else => "The operation could not be completed. Existing sessions are still running.",
    };
    var buffer: [512]u8 = undefined;
    const text = if (err == error.ShellStartFailed and last_pty_launch_error != 0)
        std.fmt.bufPrint(&buffer, "{s} ({s}; Win32 {d})", .{ message, @errorName(err), last_pty_launch_error }) catch message
    else
        std.fmt.bufPrint(&buffer, "{s} ({s})", .{ message, @errorName(err) }) catch message;
    dialogs.showCloseFailure(text);
    invalidateWindow();
}

/// Fatal failures close the native window after showing one diagnostic message.
pub fn showFailure(err: anyerror) void {
    var buffer: [256]u8 = undefined;
    const message = std.fmt.bufPrintZ(&buffer, "Vex native rewrite failed: {s}", .{@errorName(err)}) catch "Vex native rewrite failed";
    c.vex_window_error(message);
}
/// Borrowed UTF-8 tab rename state; the cursor is a byte offset at a codepoint boundary.
pub const TabEdit = struct {
    text: []const u8,
    placeholder: []const u8,
    cursor: u8,
    select_all: bool,
};

test "tab rename reuses DirectWrite layout across repaints and invalidates changed text and fonts" {
    const allocator = std.testing.allocator;
    const executable_directory = try std.fs.selfExeDirPathAlloc(allocator);
    defer allocator.free(executable_directory);
    const fixture = try std.fs.path.join(allocator, &.{ executable_directory, "vex-conpty-fixture.exe" });
    defer allocator.free(fixture);
    const output = try std.process.Child.run(.{ .allocator = allocator, .argv = &.{ fixture, "tab-rename-layout" }, .max_output_bytes = 4096 });
    defer allocator.free(output.stdout);
    defer allocator.free(output.stderr);
    try std.testing.expectEqual(std.process.Child.Term{ .Exited = 0 }, output.term);
}

/// Tab index/count come from the bounded workspace model; empty title falls back to the shell display name.
pub fn drawTab(index: usize, count: usize, active: bool, edit: ?TabEdit, title: []const u8, icons: []const app_icon.AppIcon, summary: status.ProgramStatusSummary, phase: u32, attention: bool) void {
    var native_icons: [8]c.VexAppIcon = undefined;
    const icon_count: usize = @min(icons.len, native_icons.len);
    for (icons[0..icon_count], 0..) |icon, idx| {
        native_icons[idx] = nativeAppIcon(icon);
    }
    const icons_ptr: [*c]const c.VexAppIcon = if (icon_count == 0) null else &native_icons;
    const display = if (edit) |e| e.text else title;
    const native_edit: c.VexTabEdit = if (edit) |e| .{
        .editing = true,
        .select_all = e.select_all,
        .cursor = e.cursor,
        .placeholder = if (e.placeholder.len == 0) null else e.placeholder.ptr,
        .placeholder_length = e.placeholder.len,
    } else .{
        .editing = false,
        .select_all = false,
        .cursor = 0,
        .placeholder = null,
        .placeholder_length = 0,
    };
    c.vex_draw_tab(@intCast(index), @intCast(count), active, native_edit, if (display.len == 0) null else display.ptr, display.len, icons_ptr, icon_count, nativeProgramStatus(summary, phase), attention);
}
pub fn nativeAppIcon(icon: app_icon.AppIcon) c.VexAppIcon {
    var badge = [4]u8{ 0, 0, 0, 0 };
    const len: usize = @min(@as(usize, icon.badge_len), 2);
    @memcpy(badge[0..len], icon.badge_text[0..len]);
    return .{
        .kind = @intFromEnum(icon.kind),
        .glyph = @intFromEnum(icon.glyph),
        .badge_len = @intCast(len),
        .reserved = 0,
        .color_rgb = icon.color_rgb,
        .badge = badge,
    };
}
/// Projects status with borrowed UTF-8-bounded detail and one icon wave step, consumed synchronously by native drawing.
pub fn nativeProgramStatus(summary: status.ProgramStatusSummary, phase: u32) c.VexProgramStatus {
    const detail = status_presentation.statusDetail(summary);
    return .{ .state = @intFromEnum(summary.state), .kind = if (summary.kind) |kind| switch (kind) {
        .permission => 1,
        .question => 2,
        .auth => 3,
    } else 0, .progress = if (summary.progress) |progress| progress else -1, .detail = if (detail.len > 0) detail.ptr else null, .detail_length = detail.len, .phase = phase };
}
/// Updates native window chrome colors to match the active dark or light theme.
pub fn setChromePalette(palette: terminal_theme.ChromePalette) void {
    c.vex_window_set_chrome_palette(nativeChromePalette(palette));
}
/// Projects the same palette into the main window and its owned dialog controls.
pub fn nativeChromePalette(palette: terminal_theme.ChromePalette) c.VexChromePalette {
    return .{
        .window_bg = palette.window_bg,
        .titlebar_bg = palette.titlebar_bg,
        .border = palette.border,
        .pane_bg = palette.pane_bg,
        .pane_header_bg = palette.pane_header_bg,
        .active_tab_bg = palette.active_tab_bg,
        .active_tab_border = palette.active_tab_border,
        .inactive_tab_hover_bg = palette.inactive_tab_hover_bg,
        .text_primary = palette.text_primary,
        .text_muted = palette.text_muted,
        .accent = palette.accent,
        .is_dark = palette.is_dark,
    };
}
/// Draws the cursor-anchored inline path completion popup.
pub fn drawPathCompletion(bounds: panes.PaneRect, dir_text: []const u8, query: []const u8, items: []const c.VexPathCompletionItem, scan_error: bool, loading: bool, truncated: bool) void {
    c.vex_draw_path_completion(
        .{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height },
        if (dir_text.len == 0) null else dir_text.ptr,
        dir_text.len,
        if (query.len == 0) null else query.ptr,
        query.len,
        if (items.len == 0) null else items.ptr,
        items.len,
        @as(u32, @intFromBool(scan_error)) | (@as(u32, @intFromBool(loading)) << 1) | (@as(u32, @intFromBool(truncated)) << 2),
    );
}
/// Dynamically updates the terminal font size in DIPs [9.0, 32.0] and triggers a grid layout resize.
pub fn setFontSize(size: f32) !void {
    if (c.vex_window_set_font_size(size) != 0) return error.InvalidFontSize;
}
/// Updates font family and size together, rebuilding font resources and grid layout at most once.
pub fn setFontPreferences(family: *const [128]u8, size: f32) !void {
    if (c.vex_window_set_font_preferences(family, size) != 0) return error.InvalidFontPreferences;
}

test "startup font preferences are accepted before window resources exist and reject invalid updates" {
    const defaults = @import("../app/user_settings.zig").default_font;
    var family = [_]u8{0} ** 128;
    @memcpy(family[0..8], "Consolas");
    try setFontPreferences(&family, 19);
    defer setFontPreferences(&defaults, 14) catch unreachable;
    try std.testing.expectEqual(@as(f32, 19), fontSize());
    try std.testing.expectError(error.InvalidFontPreferences, setFontPreferences(&family, std.math.nan(f32)));
    try std.testing.expectEqual(@as(f32, 19), fontSize());
    family[0] = 0xff;
    try std.testing.expectError(error.InvalidFontPreferences, setFontPreferences(&family, 12));
    try std.testing.expectEqual(@as(f32, 19), fontSize());
}

/// Returns the active terminal font size in DIPs.
pub fn fontSize() f32 {
    return c.vex_window_font_size();
}
/// Begins a pane clip; every successful call must be paired with endPane, including errors.
pub fn beginPane(bounds: panes.PaneRect, focused: bool, hovered: bool, can_focus: bool, zoomed: bool, hovered_action: ?panes.PaneHeaderAction, title: []const u8, icon: app_icon.AppIcon, summary: status.ProgramStatusSummary, phase: u32) void {
    const action_code: u8 = if (hovered_action) |action| @intFromEnum(action) else 0;
    c.vex_begin_pane(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height }, focused, hovered, can_focus, zoomed, action_code, if (title.len == 0) null else title.ptr, title.len, nativeAppIcon(icon), nativeProgramStatus(summary, phase));
}
/// Ends the current pane clip before drawing another pane or returning to chrome.
pub fn endPane() void {
    c.vex_end_pane();
}
/// Draws the rounded scrollback thumb overlay along the right edge of a pane.
pub fn drawScrollbar(bounds: panes.PaneRect, geometry: ScrollbarGeometry, active: bool, opacity: f32) void {
    c.vex_draw_scrollbar(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height }, geometry.thumb_y, geometry.thumb_height, active, opacity);
}
/// Draws the specular sweep confirmation over recently copied terminal selection spans.
pub fn drawCopyFeedback(bounds: panes.PaneRect, cell_width: f32, cell_height: f32, spans: []const c.VexCopySpan, progress: f32) void {
    if (spans.len == 0) return;
    c.vex_draw_copy_feedback(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height }, cell_width, cell_height, spans.ptr, spans.len, progress);
}
/// Draws the retained final session's exit code without writing UI text into terminal output.
pub fn drawSessionExit(bounds: panes.PaneRect, exit_code: u32) void {
    c.vex_draw_session_exit(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height }, exit_code);
}
/// Draws the translucent split preview overlay for an active pane header drag-to-split gesture.
pub fn drawSplitPreview(bounds: panes.PaneRect) void {
    c.vex_draw_split_preview(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height });
}
pub fn drawTerminalPreview(pane_id: panes.PaneId, bounds: panes.PaneRect, frame: *const terminal.TerminalFrame, theme: terminal_theme.TerminalTheme) !void {
    c.vex_begin_terminal_preview(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height }, frame.columns, frame.rows);
    defer endPane();
    // Borrow the terminal's generation-cached frame and the existing glyph atlas; never resize the PTY.
    try drawTerminalFrame(pane_id, frame, false, false, null, theme);
}
pub fn drawPreviewClose(bounds: panes.PaneRect, hovered: bool) void {
    c.vex_draw_preview_close(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height }, hovered);
}
pub fn drawTabPreviewCard(bounds: panes.PaneRect, title: []const u8, selected: bool) void {
    c.vex_draw_tab_preview_card(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height }, title.ptr, title.len, selected);
}

/// Draws a borrowed parsed snapshot; no C handles or pointers enter the application model.
pub fn drawTerminalFrame(pane_id: panes.PaneId, frame: *const terminal.TerminalFrame, focused: bool, cursor_shown: bool, hovered_link: ?HoveredLinkSpan, theme: terminal_theme.TerminalTheme) !void {
    const cursor = frame.cursor;
    const cursor_visible = focused and cursor_shown and cursor.visible and cursor.column < frame.columns and cursor.row < frame.rows;
    const block_cursor = cursor_visible and cursor.shape == .block;
    const key = terminalGeometryKey(pane_id, frame, hovered_link, theme);
    var reused = false;
    if (c.vex_terminal_geometry_begin(key, &reused) != 0) return error.NativeTextRenderingFailed;
    if (!reused) {
        var complete = false;
        defer c.vex_terminal_geometry_end(complete);
        try drawTerminalCells(frame, hovered_link, theme);
        complete = true;
    }
    if (block_cursor) {
        if (blockCursorRun(frame, hovered_link, theme)) |run| {
            for (0..run.cells) |offset| c.vex_draw_cursor(cursor.column + @as(u16, @intCast(offset)), cursor.row, 1, theme.cursor);
            try drawRun(cursor.column, cursor.row, run.cells, run.text, run.style, theme.background);
        }
    } else if (cursor_visible) {
        c.vex_draw_cursor(cursor.column, cursor.row, @intFromEnum(cursor.shape), theme.cursor);
    }
}

fn terminalGeometryKey(
    pane_id: panes.PaneId,
    frame: *const terminal.TerminalFrame,
    hovered_link: ?HoveredLinkSpan,
    theme: terminal_theme.TerminalTheme,
) c.VexTerminalGeometryKey {
    const absent = std.math.maxInt(u32);
    return .{
        .generation = frame.generation,
        .pane_id = @intFromEnum(pane_id),
        .columns = frame.columns,
        .rows = frame.rows,
        .background = theme.background,
        .foreground = theme.foreground,
        .cursor = theme.cursor,
        .selection_background = theme.selection_background,
        .accent = theme.accent,
        .is_dark = @intFromBool(theme.is_dark),
        .link_row = if (hovered_link) |link| link.row else absent,
        .link_start = if (hovered_link) |link| link.start_col else absent,
        .link_end = if (hovered_link) |link| link.end_col else absent,
    };
}

const ContrastCache = struct {
    const Entry = struct {
        key: u64 = std.math.maxInt(u64),
        corrected: u32 = 0,
    };
    const Bucket = struct {
        entries: [4]Entry = @splat(.{}),
        next: u2 = 0,
    };

    buckets: [1024]Bucket = @splat(.{}),

    fn get(self: *ContrastCache, foreground: u32, background: u32) u32 {
        const key = (@as(u64, foreground) << 32) | background;
        const hash = std.hash.Wyhash.hash(0, std.mem.asBytes(&key));
        const bucket = &self.buckets[hash & (self.buckets.len - 1)];
        for (bucket.entries) |entry| {
            if (entry.key == key) return entry.corrected;
        }
        const corrected = ensureMinimumContrast(foreground, background);
        bucket.entries[bucket.next] = .{ .key = key, .corrected = corrected };
        bucket.next +%= 1;
        return corrected;
    }
};

// Window-thread-only. The transform depends solely on these two colors, not the active theme or pane.
var contrast_cache: ContrastCache = .{};

const BlockCursorRun = struct { cells: usize, text: []const u8, style: terminal.TextStyle };

fn blockCursorRun(frame: *const terminal.TerminalFrame, hovered_link: ?HoveredLinkSpan, theme: terminal_theme.TerminalTheme) ?BlockCursorRun {
    const cursor = frame.cursor;
    if (cursor.column >= frame.columns or cursor.row >= frame.rows) return null;
    const cell = frame.cells.items[@as(usize, cursor.row) * frame.columns + cursor.column];
    if (cell.width == .tail) return null;
    var style = cell.style;
    style.background = theme.cursor;
    style.foreground = theme.background;
    if (hovered_link) |link| {
        if (link.row == cursor.row and cursor.column >= link.start_col and cursor.column <= link.end_col) style.underline = true;
    }
    return .{ .cells = if (cell.width == .wide) 2 else 1, .text = frame.cellText(cell), .style = style };
}

/// A dominant neutral surface identifies a program's palette, not its name or process.
/// Keep already-matching palettes and isolated bright/dark accents untouched.
const AppearanceCorrection = struct {
    const Entry = struct { source: u32 = 0xffffffff, corrected: u32 = 0 };

    source: u32,
    target: terminal_theme.TerminalTheme,
    source_lab: Oklab,
    background_lab: Oklab,
    foreground_lab: Oklab,
    cache: [512]Entry = @splat(.{}),

    fn init(frame: *const terminal.TerminalFrame, theme: terminal_theme.TerminalTheme) ?AppearanceCorrection {
        if (frame.cells.items.len == 0) return null;
        var candidate: u32 = 0;
        var votes: usize = 0;
        for (frame.cells.items) |cell| {
            if (votes == 0) candidate = cell.style.background;
            if (cell.style.background == candidate) votes += 1 else votes -= 1;
        }
        var count: usize = 0;
        for (frame.cells.items) |cell| {
            if (cell.style.background == candidate) count += 1;
        }
        if (count <= frame.cells.items.len / 2) return null;
        const source_lab = srgbToOklab(candidate);
        const opposite = if (theme.is_dark) source_lab.l > 0.72 else source_lab.l < 0.48;
        if (!opposite or source_lab.a * source_lab.a + source_lab.b * source_lab.b > 0.0144) return null;
        return .{
            .source = candidate,
            .target = theme,
            .source_lab = source_lab,
            .background_lab = srgbToOklab(theme.background),
            .foreground_lab = srgbToOklab(theme.foreground),
        };
    }

    fn map(self: *AppearanceCorrection, color: u32) u32 {
        if (color == self.source) return self.target.background;
        const index = std.hash.Wyhash.hash(0, std.mem.asBytes(&color)) & (self.cache.len - 1);
        const entry = &self.cache[index];
        if (entry.source == color) return entry.corrected;
        const lab = srgbToOklab(color);
        const towards_text = if (self.target.is_dark)
            (self.source_lab.l - lab.l) / @max(0.001, self.source_lab.l)
        else
            (lab.l - self.source_lab.l) / @max(0.001, 1 - self.source_lab.l);
        const amount = std.math.clamp(towards_text, 0, 1);
        const lightness = if (towards_text >= 0)
            self.background_lab.l + (self.foreground_lab.l - self.background_lab.l) * amount
        else if (self.target.is_dark)
            self.background_lab.l - (lab.l - self.source_lab.l)
        else
            self.background_lab.l + (self.source_lab.l - lab.l);
        // Tint neutral surfaces towards the host theme, preserving saturated syntax hues.
        const neutral = std.math.clamp(1 - @sqrt(lab.a * lab.a + lab.b * lab.b) / 0.12, 0, 1);
        const tint_a = self.background_lab.a + (self.foreground_lab.a - self.background_lab.a) * amount;
        const tint_b = self.background_lab.b + (self.foreground_lab.b - self.background_lab.b) * amount;
        const corrected = oklabToSrgb(std.math.clamp(lightness, 0, 1), lab.a * (1 - neutral) + tint_a * neutral, lab.b * (1 - neutral) + tint_b * neutral);
        entry.* = .{ .source = color, .corrected = corrected };
        return corrected;
    }

    fn apply(self: *AppearanceCorrection, style: terminal.TextStyle) terminal.TextStyle {
        var result = style;
        result.foreground = self.map(style.foreground);
        result.background = self.map(style.background);
        return result;
    }
};

// Reverse-video and explicit TUI highlights are surfaces, not text. Mapping them
// all the way to the foreground endpoint produces black/white selection bars.
fn softenHighlight(style: terminal.TextStyle, theme: terminal_theme.TerminalTheme) terminal.TextStyle {
    const surface = srgbToOklab(style.background);
    if (surface.a * surface.a + surface.b * surface.b > 0.0144) return style;
    const base = srgbToOklab(theme.background);
    const selection = srgbToOklab(theme.selection_background);
    const direction: f32 = if (theme.is_dark) 1 else -1;
    if ((surface.l - selection.l) * direction <= 0 or (selection.l - base.l) * direction <= 0) return style;
    var result = style;
    result.background = theme.selection_background;
    // Keep intentionally blank/hidden cells blank after changing their surface.
    if (style.foreground == style.background) result.foreground = result.background;
    return result;
}

fn drawTerminalCells(frame: *const terminal.TerminalFrame, hovered_link: ?HoveredLinkSpan, theme: terminal_theme.TerminalTheme) !void {
    var appearance = AppearanceCorrection.init(frame, theme);
    var highlight_source: ?terminal.TextStyle = null;
    var highlight_style: terminal.TextStyle = undefined;
    var cached_fg: u32 = theme.foreground;
    var cached_bg: u32 = theme.background;
    var cached_corrected_fg: u32 = theme.foreground;
    for (0..frame.rows) |row| {
        const cells = frame.cells.items[row * frame.columns ..][0..frame.columns];
        const row_link = if (hovered_link) |hl| (if (hl.row == row) hl else null) else null;
        var start: usize = 0;
        var run_cells: usize = 0;
        var buffer: [4096]u8 = undefined;
        var length: usize = 0;
        var style: terminal.TextStyle = .{ .foreground = theme.foreground, .background = theme.background };
        for (cells, 0..) |cell, column| {
            if (cell.width == .tail) continue;
            const text = frame.cellText(cell);
            const in_link = if (row_link) |hl| (column >= hl.start_col and column <= hl.end_col) else false;
            var cell_style = cell.style;
            if (appearance) |*correction| cell_style = correction.apply(cell_style);
            if (!isBlockGraphic(text)) {
                if (highlight_source == null or !std.meta.eql(highlight_source.?, cell_style)) {
                    highlight_source = cell_style;
                    highlight_style = softenHighlight(cell_style, theme);
                }
                cell_style = highlight_style;
            }
            if (in_link) {
                cell_style.underline = true;
            }
            if (cell.selected) {
                cell_style.background = theme.selection_background;
                cell_style.foreground = if (theme.is_dark) 0xffffff else 0x111318;
            } else if (in_link) {
                cell_style.foreground = theme.accent;
            } else if (!cell_style.invisible and !isBlockGraphic(text)) {
                if (cell_style.foreground != cached_fg or cell_style.background != cached_bg) {
                    cached_fg = cell_style.foreground;
                    cached_bg = cell_style.background;
                    cached_corrected_fg = contrast_cache.get(cached_fg, cached_bg);
                }
                cell_style.foreground = cached_corrected_fg;
            }
            const ascii = cell.width == .narrow and (text.len == 0 or (text.len == 1 and text[0] >= 32 and text[0] <= 126));
            if (!ascii or !std.meta.eql(style, cell_style) or length == buffer.len) {
                try drawRun(start, row, run_cells, buffer[0..length], style, theme.background);
                run_cells = 0;
                length = 0;
                start = column;
                style = cell_style;
            }
            if (!ascii) {
                const width: usize = if (cell.width == .wide) 2 else 1;
                try drawRun(column, row, width, text, cell_style, theme.background);
                start = column + width;
            } else {
                buffer[length] = if (text.len == 0) ' ' else text[0];
                length += 1;
                run_cells += 1;
            }
        }
        try drawRun(start, row, run_cells, buffer[0..length], style, theme.background);
    }
}

test "contrast cache preserves exact correction across hits, collisions and appearance changes" {
    var cache: ContrastCache = .{};
    for (0..8192) |index| {
        const foreground: u32 = @intCast((index * 2654435761) & 0xffffff);
        const background: u32 = if (index % 2 == 0) 0x282c34 else 0xfafafa;
        const expected = ensureMinimumContrast(foreground, background);
        try std.testing.expectEqual(expected, cache.get(foreground, background));
        try std.testing.expectEqual(expected, cache.get(foreground, background));
        try std.testing.expectEqual(ensureMinimumContrast(foreground, background ^ 0xffffff), cache.get(foreground, background ^ 0xffffff));
    }
}

test "terminal geometry excludes cursor shape, blink and motion including block cursors" {
    const testing = std.testing;
    const theme = terminal_theme.themes[0];
    var frame: terminal.TerminalFrame = .{ .columns = 80, .rows = 24, .generation = 1 };
    const pane: panes.PaneId = @enumFromInt(1);
    const initial = terminalGeometryKey(pane, &frame, null, theme);
    frame.cursor = .{ .column = 12, .row = 3, .visible = true, .shape = .bar };
    try testing.expect(std.meta.eql(initial, terminalGeometryKey(pane, &frame, null, theme)));
    frame.cursor.shape = .block;
    frame.cursor.column += 1;
    try testing.expect(std.meta.eql(initial, terminalGeometryKey(pane, &frame, null, theme)));
    frame.cursor.visible = false;
    try testing.expect(std.meta.eql(initial, terminalGeometryKey(pane, &frame, null, theme)));
}

test "block cursor overlay preserves wide text and decorations without inheriting selection colors" {
    const api = try @import("../terminal/api.zig").Api.load();
    var emulator = try terminal.Terminal.init(std.testing.allocator, &api);
    defer emulator.deinit();
    try emulator.feedOutput("\x1b[1;3;9m界é\x1b[H\x1b[2 q");
    try emulator.setKeyboardSelection(0, 0, 2, 0);
    const frame = try emulator.readFrame();
    try std.testing.expect(frame.cells.items[0].selected);
    const theme = terminal_theme.themes[0];
    const link: HoveredLinkSpan = .{ .row = 0, .start_col = 0, .end_col = 2 };
    const run = blockCursorRun(frame, link, theme) orelse return error.MissingBlockCursor;
    try std.testing.expectEqual(@as(usize, 2), run.cells);
    try std.testing.expectEqualStrings("界", run.text);
    try std.testing.expect(run.style.bold and run.style.italic and run.style.underline and run.style.strikethrough);
    try std.testing.expectEqual(theme.cursor, run.style.background);
    try std.testing.expectEqual(theme.background, run.style.foreground);
    emulator.frame.cursor.column = 1;
    try std.testing.expect(blockCursorRun(frame, null, theme) == null);
    emulator.frame.cursor.column = 2;
    const grapheme = blockCursorRun(frame, null, theme) orelse return error.MissingBlockCursor;
    try std.testing.expectEqualStrings("é", grapheme.text);
    emulator.frame.cursor.column = frame.columns;
    try std.testing.expect(blockCursorRun(frame, null, theme) == null);
}

test "terminal geometry keys distinguish content owners, themes and hovered links" {
    const testing = std.testing;
    const theme = terminal_theme.themes[0];
    var frame: terminal.TerminalFrame = .{ .columns = 80, .rows = 24, .generation = 1 };
    const pane: panes.PaneId = @enumFromInt(1);
    const initial = terminalGeometryKey(pane, &frame, null, theme);
    try testing.expect(!std.meta.eql(initial, terminalGeometryKey(@enumFromInt(2), &frame, null, theme)));
    frame.generation += 1;
    try testing.expect(!std.meta.eql(initial, terminalGeometryKey(pane, &frame, null, theme)));
    frame.generation -= 1;
    frame.columns += 1;
    try testing.expect(!std.meta.eql(initial, terminalGeometryKey(pane, &frame, null, theme)));
    frame.columns -= 1;
    var changed_theme = theme;
    changed_theme.selection_background ^= 1;
    try testing.expect(!std.meta.eql(initial, terminalGeometryKey(pane, &frame, null, changed_theme)));
    const link: HoveredLinkSpan = .{ .row = 1, .start_col = 2, .end_col = 8 };
    const linked = terminalGeometryKey(pane, &frame, link, theme);
    try testing.expect(!std.meta.eql(initial, linked));
    var changed_link = link;
    changed_link.end_col += 1;
    try testing.expect(!std.meta.eql(linked, terminalGeometryKey(pane, &frame, changed_link, theme)));
}

pub fn isBlockGraphic(text: []const u8) bool {
    if (text.len != 3 or (text[0] & 0xF0) != 0xE0) return false;
    const cp: u32 = (@as(u32, text[0] & 0x0F) << 12) | (@as(u32, text[1] & 0x3F) << 6) | @as(u32, text[2] & 0x3F);
    return (cp >= 0x2580 and cp <= 0x259F) or (cp >= 0xE0B0 and cp <= 0xE0B7);
}

pub fn ensureMinimumContrast(fg_rgb: u32, bg_rgb: u32) u32 {
    if (fg_rgb == bg_rgb) return fg_rgb;
    const minimum_ratio: f32 = 4.5;
    if (contrastRatio(fg_rgb, bg_rgb) >= minimum_ratio) return fg_rgb;
    const lab = srgbToOklab(fg_rgb);
    const towards_white = contrastRatio(0xffffff, bg_rgb) > contrastRatio(0, bg_rgb);
    const endpoint: f32 = if (towards_white) 1 else 0;
    var low: f32 = 0;
    var high: f32 = 1;
    var corrected: u32 = if (towards_white) 0xffffff else 0;
    // Check the gamut-clipped, quantized RGB result, not just perceptual lightness.
    // Reducing chroma towards the endpoint preserves hue without clipping saturated text.
    for (0..16) |_| {
        const amount = (low + high) * 0.5;
        const candidate = oklabToSrgb(lab.l + (endpoint - lab.l) * amount, lab.a * (1 - amount), lab.b * (1 - amount));
        if (contrastRatio(candidate, bg_rgb) >= minimum_ratio) {
            corrected = candidate;
            high = amount;
        } else low = amount;
    }
    return corrected;
}

const Oklab = struct { l: f32, a: f32, b: f32 };

fn srgbChannelToLinear(c_val: u32) f32 {
    const v = @as(f32, @floatFromInt(c_val & 0xFF)) / 255.0;
    return if (v <= 0.04045) v / 12.92 else std.math.pow(f32, (v + 0.055) / 1.055, 2.4);
}

fn linearChannelToSrgb(v: f32) u32 {
    const clamped = std.math.clamp(v, 0.0, 1.0);
    const s = if (clamped <= 0.0031308) clamped * 12.92 else 1.055 * std.math.pow(f32, clamped, 1.0 / 2.4) - 0.055;
    return @intFromFloat(@round(std.math.clamp(s * 255.0, 0.0, 255.0)));
}

fn relativeLuminance(rgb_val: u32) f32 {
    return 0.2126 * srgbChannelToLinear(rgb_val >> 16) + 0.7152 * srgbChannelToLinear(rgb_val >> 8) + 0.0722 * srgbChannelToLinear(rgb_val);
}

fn contrastRatio(foreground: u32, background: u32) f32 {
    const fg = relativeLuminance(foreground);
    const bg = relativeLuminance(background);
    return (@max(fg, bg) + 0.05) / (@min(fg, bg) + 0.05);
}

fn srgbToOklab(rgb_val: u32) Oklab {
    const r = srgbChannelToLinear(rgb_val >> 16);
    const g = srgbChannelToLinear(rgb_val >> 8);
    const b = srgbChannelToLinear(rgb_val);
    const l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
    const m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
    const s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;
    const l_ = std.math.cbrt(l);
    const m_ = std.math.cbrt(m);
    const s_ = std.math.cbrt(s);
    return .{
        .l = 0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
        .a = 1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
        .b = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_,
    };
}

fn oklabToSrgb(l_val: f32, a_val: f32, b_val: f32) u32 {
    const l_ = l_val + 0.3963377774 * a_val + 0.2158037573 * b_val;
    const m_ = l_val - 0.1055613458 * a_val - 0.0638541728 * b_val;
    const s_ = l_val - 0.0894841775 * a_val - 1.2914855480 * b_val;
    const l = l_ * l_ * l_;
    const m = m_ * m_ * m_;
    const s = s_ * s_ * s_;
    const r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
    const g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
    const b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
    return (linearChannelToSrgb(r) << 16) | (linearChannelToSrgb(g) << 8) | linearChannelToSrgb(b);
}

fn drawRun(column: usize, row: usize, cells: usize, text: []const u8, style: terminal.TextStyle, default_bg: u32) !void {
    if (cells == 0) return;
    if (style.background == default_bg and !style.underline and !style.strikethrough) {
        if (std.mem.indexOfNonePos(u8, text, 0, " ") == null) return;
    }
    const flags = @as(u32, @intFromBool(style.bold)) | (@as(u32, @intFromBool(style.italic)) << 1) |
        (@as(u32, @intFromBool(style.underline)) << 2) | (@as(u32, @intFromBool(style.strikethrough)) << 3) | (@as(u32, @intFromBool(style.invisible)) << 4);
    if (c.vex_draw_run(@intCast(column), @intCast(row), @intCast(cells), text.ptr, text.len, style.foreground, style.background, flags) != 0) return error.NativeTextRenderingFailed;
}

test "native status bridge carries bounded UTF-8 detail only for synchronous presentation" {
    var message: [260]u8 = @splat('a');
    @memcpy(message[255..259], "😀");
    const projected = nativeProgramStatus(.{ .state = .working, .progress = 42, .message = &message }, 2);
    try std.testing.expectEqual(@as(u32, 2), projected.state);
    try std.testing.expectEqual(@as(u32, 2), projected.phase);
    try std.testing.expectEqual(@as(i32, 42), projected.progress);
    try std.testing.expectEqual(@as(usize, 255), projected.detail_length);
    try std.testing.expect(projected.detail != null);
    try std.testing.expectEqualStrings(message[0..255], projected.detail[0..projected.detail_length]);
    const empty = nativeProgramStatus(.{}, 0);
    try std.testing.expectEqual(@as(usize, 0), empty.detail_length);
    try std.testing.expect(empty.detail == null);
}

test "workspace shortcuts exactly match implemented WPF chords without stealing terminal Ctrl keys" {
    try std.testing.expectEqual(@as(?WorkspaceAction, .new_tab), workspaceShortcut('T', 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .split_right), workspaceShortcut('R', 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .split_down), workspaceShortcut('D', 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .close_active), workspaceShortcut('W', 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .command_palette), workspaceShortcut('P', 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .theme_picker), workspaceShortcut('M', 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .path_completion), workspaceShortcut('F', 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .tab_peek), workspaceShortcut(0x20, 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .next_tab), workspaceShortcut(9, 2));
    try std.testing.expectEqual(@as(?WorkspaceAction, .previous_tab), workspaceShortcut(9, 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .zoom_in), workspaceShortcut(0xBB, 2));
    try std.testing.expectEqual(@as(?WorkspaceAction, .zoom_in), workspaceShortcut(0xBB, 3));
    try std.testing.expectEqual(@as(?WorkspaceAction, .zoom_out), workspaceShortcut(0xBD, 2));
    try std.testing.expectEqual(@as(?WorkspaceAction, .zoom_reset), workspaceShortcut('0', 2));
    for ([_]u32{ 'T', 'R', 'D', 'W', 'C', 'S', 'P', 'M', 'F' }) |key| try std.testing.expectEqual(@as(?WorkspaceAction, null), workspaceShortcut(key, 2));
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

test "viewportPoint rejects header clicks unless clamping and maps DIPs to grid cells and scaled pixels" {
    const metrics: WindowMetrics = .{ .width = 1120, .height = 700, .cell_width = 8, .cell_height = 16, .scale = 1.5 };
    const bounds: panes.PaneRect = .{ .x = 10, .y = 50, .width = 332, .height = 228 };
    // Header clicks (y < bounds.y + 30) do not start a viewport selection.
    try std.testing.expectEqual(@as(?ViewportPoint, null), try metrics.viewportPoint(bounds, 24, 62, false));
    // Inside the terminal grid (grid_left = 16, grid_top = 80):
    const inside = (try metrics.viewportPoint(bounds, 36, 114, false)).?;
    try std.testing.expectEqual(@as(u16, 2), inside.column);
    try std.testing.expectEqual(@as(u16, 2), inside.row);
    try std.testing.expectApproxEqAbs(@as(f64, 30.0), inside.x_px, 0.01);
    try std.testing.expectApproxEqAbs(@as(f64, 51.0), inside.y_px, 0.01);
    // The scrollbar gutter is never terminal text, even when no scrollbar is visible yet.
    try std.testing.expectEqual(@as(?ViewportPoint, null), try metrics.viewportPoint(bounds, 330, 114, false));
    try std.testing.expectEqual(@as(u16, 38), (try metrics.terminalSize(bounds)).columns);
    // Clamped drag outside the pane clamps to the boundary cells.
    const clamped = (try metrics.viewportPoint(bounds, -50, 900, true)).?;
    try std.testing.expectEqual(@as(u16, 0), clamped.column);
    try std.testing.expectEqual(@as(u16, 11), clamped.row);
}

test "ScrollbarGeometry computes thumb travel, right-edge hit zone, and drag target offsets" {
    const bounds: panes.PaneRect = .{ .x = 10, .y = 40, .width = 300, .height = 236 };
    // Not visible when total <= len.
    try std.testing.expectEqual(@as(?ScrollbarGeometry, null), ScrollbarGeometry.compute(bounds, .{ .total = 24, .offset = 0, .len = 24 }));
    // Track height = 236 - 36 = 200, track_top = 70. With total=100, len=25: thumb_height = 50, travel = 150, scrollable = 75.
    const mid = ScrollbarGeometry.compute(bounds, .{ .total = 100, .offset = 25, .len = 25 }).?;
    try std.testing.expectApproxEqAbs(@as(f32, 70.0), mid.track_top, 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 200.0), mid.track_height, 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 50.0), mid.thumb_height, 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 150.0), mid.travel, 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 123.5), mid.thumbTopForY(133.5, 10), 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 70), mid.thumbTopForY(-100, 10), 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 220), mid.thumbTopForY(900, 10), 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 120.0), mid.thumb_y, 0.01);
    try std.testing.expect(mid.containsPoint(bounds, 305.0, 130.0));
    try std.testing.expect(!mid.containsPoint(bounds, 280.0, 130.0));
    try std.testing.expect(mid.containsThumb(130.0));
    try std.testing.expect(!mid.containsThumb(90.0));
    // Dragging with grab_offset=10.0 to y=80.0 -> desired=0 -> offset 0; to y=230.0 -> desired=150 -> offset 75.
    try std.testing.expectEqual(@as(u64, 0), mid.targetOffsetForY(80.0, 10.0));
    try std.testing.expectEqual(@as(u64, 38), mid.targetOffsetForY(155.0, 10.0));
    try std.testing.expectEqual(@as(u64, 75), mid.targetOffsetForY(230.0, 10.0));
}

test "appearance correction adapts explicit program palettes in both directions without changing the source frame" {
    var cells: [8]terminal.TerminalCell = @splat(.{ .width = .narrow, .style = .{ .foreground = 0x202020, .background = 0xfafafa }, .text_start = 0, .text_length = 0 });
    var frame: terminal.TerminalFrame = .{ .columns = 8, .rows = 1, .cells = .{ .items = &cells, .capacity = cells.len } };
    const original = cells;
    for ([_]usize{ terminal_theme.default_dark_index, terminal_theme.default_light_index, terminal_theme.default_dark_index }) |index| {
        const theme = terminal_theme.themes[index];
        if (!theme.is_dark) {
            try std.testing.expect(AppearanceCorrection.init(&frame, theme) == null);
            continue;
        }
        var correction = AppearanceCorrection.init(&frame, theme) orelse return error.MissingAppearanceCorrection;
        const mapped = correction.apply(cells[0].style);
        try std.testing.expectEqual(theme.background, mapped.background);
        try std.testing.expect(contrastRatio(ensureMinimumContrast(mapped.foreground, mapped.background), mapped.background) >= 4.5);
        try std.testing.expectEqual(mapped.foreground, correction.map(cells[0].style.foreground));
        const hidden = correction.apply(.{ .foreground = 0xfafafa, .background = 0xfafafa, .invisible = true });
        try std.testing.expectEqual(hidden.foreground, hidden.background);
        try std.testing.expect(hidden.invisible);
        const raised = correction.map(0xeeeeee);
        try std.testing.expect(srgbToOklab(raised).l > srgbToOklab(theme.background).l);
    }
    try std.testing.expectEqualDeep(original, cells);
    for (&cells) |*cell| cell.style = .{ .foreground = 0xeeeeee, .background = 0x181818 };
    const light = terminal_theme.themes[terminal_theme.default_light_index];
    var correction = AppearanceCorrection.init(&frame, light) orelse return error.MissingAppearanceCorrection;
    const mapped = correction.apply(cells[0].style);
    try std.testing.expectEqual(light.background, mapped.background);
    try std.testing.expect(srgbToOklab(mapped.foreground).l < srgbToOklab(mapped.background).l);
    try std.testing.expect(AppearanceCorrection.init(&frame, terminal_theme.themes[terminal_theme.default_dark_index]) == null);
}

test "explicit RGB alternate-screen output follows host mode without program cooperation" {
    const binding = @import("../terminal/api.zig");
    const api = try binding.Api.load();
    var emulator = try terminal.Terminal.init(std.testing.allocator, &api);
    defer emulator.deinit();
    try emulator.feedOutput("\x1b[?1049h\x1b[48;2;250;250;250m\x1b[2J\x1b[H\x1b[38;2;32;32;32mX");
    const frame = try emulator.readFrame();
    try std.testing.expect(!try emulator.decMode(2031));
    const generation = frame.generation;
    const dark = terminal_theme.themes[terminal_theme.default_dark_index];
    var correction = AppearanceCorrection.init(frame, dark) orelse return error.MissingAppearanceCorrection;
    const mapped = correction.apply(frame.cells.items[0].style);
    try std.testing.expectEqual(dark.background, mapped.background);
    try std.testing.expect(srgbToOklab(mapped.foreground).l > srgbToOklab(mapped.background).l);
    try std.testing.expect(AppearanceCorrection.init(frame, terminal_theme.themes[terminal_theme.default_light_index]) == null);
    try std.testing.expectEqual(@as(u32, 0xfafafa), frame.cells.items[0].style.background);
    try std.testing.expectEqual(generation, (try emulator.readFrame()).generation);
}

test "TUI highlights use theme surfaces across dark light dark switches without mutating RGB output" {
    for ([_]bool{ false, true }) |dark_source| {
        const background: u32 = if (dark_source) 0x181818 else 0xfafafa;
        const foreground: u32 = if (dark_source) 0xe6e6e6 else 0x202020;
        var cells: [8]terminal.TerminalCell = @splat(.{ .width = .narrow, .style = .{ .foreground = foreground, .background = background }, .text_start = 0, .text_length = 0 });
        cells[0].style = .{ .foreground = background, .background = foreground };
        var frame: terminal.TerminalFrame = .{ .cells = .{ .items = &cells, .capacity = cells.len } };
        const original = cells;
        for ([_]usize{ terminal_theme.default_dark_index, terminal_theme.default_light_index, terminal_theme.default_dark_index }) |index| {
            const theme = terminal_theme.themes[index];
            var correction = AppearanceCorrection.init(&frame, theme);
            const mapped = if (correction) |*value| value.apply(cells[0].style) else cells[0].style;
            const highlight = softenHighlight(mapped, theme);
            try std.testing.expectEqual(theme.selection_background, highlight.background);
            try std.testing.expect(contrastRatio(ensureMinimumContrast(highlight.foreground, highlight.background), highlight.background) >= 4.5);
            try std.testing.expectEqualDeep(highlight, softenHighlight(highlight, theme));
            const normal = terminal.TextStyle{ .foreground = theme.foreground, .background = theme.background };
            try std.testing.expectEqualDeep(normal, softenHighlight(normal, theme));
            const hidden = softenHighlight(.{ .foreground = mapped.background, .background = mapped.background }, theme);
            try std.testing.expectEqual(hidden.foreground, hidden.background);
        }
        try std.testing.expectEqualDeep(original, cells);
    }
    const theme = terminal_theme.themes[terminal_theme.default_dark_index];
    const colored = terminal.TextStyle{ .foreground = 0, .background = 0xffff00 };
    try std.testing.expectEqualDeep(colored, softenHighlight(colored, theme));
}

test "appearance correction ignores isolated opposite accents, split palettes and saturated surfaces" {
    const theme = terminal_theme.themes[terminal_theme.default_dark_index];
    var cells: [8]terminal.TerminalCell = @splat(.{ .width = .narrow, .style = .{ .foreground = 0xffffff, .background = theme.background }, .text_start = 0, .text_length = 0 });
    var frame: terminal.TerminalFrame = .{ .cells = .{ .items = &cells, .capacity = cells.len } };
    cells[0].style.background = 0xffffff;
    try std.testing.expect(AppearanceCorrection.init(&frame, theme) == null);
    for (cells[0..4]) |*cell| cell.style.background = 0xffffff;
    try std.testing.expect(AppearanceCorrection.init(&frame, theme) == null);
    for (&cells) |*cell| cell.style.background = 0xffff00;
    try std.testing.expect(AppearanceCorrection.init(&frame, theme) == null);
}

test "contrast correction meets WCAG AA after RGB conversion on dark, light and midtone backgrounds" {
    for ([_]u32{ 0x000000, 0xffffff, 0xf5f0e8, 0x282c34, 0x777777, 0x808080, 0x00ff00, 0xff00ff }) |background| {
        for ([_]u32{ 0x111111, 0xeeeeee, 0xff0000, 0x00ff00, 0x0000ff, 0xffff00, 0x808080, 0xf0e0d0 }) |foreground| {
            if (foreground == background) continue;
            const corrected = ensureMinimumContrast(foreground, background);
            try std.testing.expect(contrastRatio(corrected, background) >= 4.5);
            if (contrastRatio(foreground, background) >= 4.5) try std.testing.expectEqual(foreground, corrected);
            try std.testing.expectEqual(corrected, ensureMinimumContrast(corrected, background));
        }
    }
}

test "ensureMinimumContrast preserves readable colors and boosts low-contrast foregrounds while exempting block graphics" {
    // High contrast default foreground on default background stays untouched.
    try std.testing.expectEqual(@as(u32, 0xe4e4e7), ensureMinimumContrast(0xe4e4e7, 0x282c34));
    // Identical fg and bg (intentionally hidden text) stays untouched.
    try std.testing.expectEqual(@as(u32, 0x282c34), ensureMinimumContrast(0x282c34, 0x282c34));
    // Dark navy foreground on dark background gets lifted in lightness.
    const boosted = ensureMinimumContrast(0x1f2430, 0x282c34);
    try std.testing.expect(boosted != 0x1f2430);
    try std.testing.expect(contrastRatio(boosted, 0x282c34) >= 4.5);
    // Block elements and Powerline glyphs are recognized so their exact shade/separator colors are preserved.
    try std.testing.expect(isBlockGraphic("█"));
    try std.testing.expect(isBlockGraphic("░"));
    try std.testing.expect(isBlockGraphic("\u{E0B0}"));
    try std.testing.expect(!isBlockGraphic("─"));
    try std.testing.expect(!isBlockGraphic("A"));
}
