const std = @import("std");
const panes = @import("../workspace/pane_tree.zig");
const terminal = @import("../terminal/terminal.zig");
const directory = @import("../shell/directory.zig");
const status = @import("../terminal/program_status.zig");
const shell = @import("../shell/shell_launch.zig");
const tab_strip = @import("../workspace/tab_strip.zig");
const app_icon = @import("../terminal/app_icon.zig");

/// Raw Windows bridge declarations stay confined to native IO modules.
pub const c = @cImport({
    @cInclude("windows_platform.h");
});
/// Workspace controls use explicit actions, not terminal key escape sequences.
pub const WorkspaceAction = enum(u32) { new_tab = 0, split_right = 1, split_down = 2, close_active = 3, next_tab = 4, previous_tab = 5, select_tab = 6, close_tab = 7, rename_tab = 8 };
/// Pointer events are checked at the Win32 seam; their coordinates remain DIPs even during capture outside the window.
pub const PointerEvent = enum(u32) { hover = c.VEX_POINTER_HOVER, begin = c.VEX_POINTER_BEGIN, move = c.VEX_POINTER_MOVE, end = c.VEX_POINTER_END, cancel = c.VEX_POINTER_CANCEL };
/// Win32 owns capture: armed tabs wait for the drag threshold, while divider/tab/selection drags request immediate capture.
pub const PointerResponse = enum(u32) { normal = c.VEX_POINTER_NORMAL, right = c.VEX_POINTER_RIGHT, down = c.VEX_POINTER_DOWN, tab_armed = c.VEX_POINTER_TAB_ARMED, tab_dragging = c.VEX_POINTER_TAB_DRAGGING, selection = c.VEX_POINTER_SELECTION, link = c.VEX_POINTER_LINK };
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
            .columns = @intFromFloat(std.math.clamp(@floor((bounds.width - 12) / self.cell_width), 2, 1000)),
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
        const grid_right = bounds.x + bounds.width - 6;
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
    /// Maps a dragged pointer Y and initial grab offset to a target scrollback row offset.
    pub fn targetOffsetForY(self: ScrollbarGeometry, y: f32, grab_offset: f32) u64 {
        if (self.travel <= 0.0 or self.scrollable == 0) return 0;
        const desired = std.math.clamp(y - self.track_top - grab_offset, 0.0, self.travel);
        const ratio: f64 = @as(f64, desired) / @as(f64, self.travel);
        const target_f = @round(ratio * @as(f64, @floatFromInt(self.scrollable)));
        return @intFromFloat(std.math.clamp(target_f, 0.0, @as(f64, @floatFromInt(self.scrollable))));
    }
};

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
    /// Queries the cached deepest non-shell child executable and wrapper command line without heap allocation.
    pub fn foregroundProcess(self: *const PtyOwner, exe_buf: []u8, cmd_buf: []u8) struct { exe: []const u8, cmd: []const u8 } {
        if (exe_buf.len == 0 or cmd_buf.len == 0) return .{ .exe = &.{}, .cmd = &.{} };
        var exe_len: usize = 0;
        var cmd_len: usize = 0;
        if (c.vex_pty_foreground_process(self.handle, exe_buf.ptr, exe_buf.len, &exe_len, cmd_buf.ptr, cmd_buf.len, &cmd_len) != 0) {
            return .{ .exe = &.{}, .cmd = &.{} };
        }
        const safe_exe = if (exe_len <= exe_buf.len and std.unicode.utf8ValidateSlice(exe_buf[0..exe_len])) exe_buf[0..exe_len] else &.{};
        const safe_cmd = if (cmd_len <= cmd_buf.len and std.unicode.utf8ValidateSlice(cmd_buf[0..cmd_len])) cmd_buf[0..cmd_len] else &.{};
        return .{ .exe = safe_exe, .cmd = safe_cmd };
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
        fn tick(context: ?*anyopaque) callconv(.c) void {
            if (recover(context)) |app| app.onTick();
        }
        fn probe(context: ?*anyopaque, query: u32) callconv(.c) u64 {
            const app = recover(context) orelse return 0;
            return app.onProbe(query);
        }
    };
    const code = c.vex_window_run(.{ .context = owner, .output = Adapter.output, .resize = Adapter.resize, .paint = Adapter.paint, .key = Adapter.key, .text = Adapter.text, .paste = Adapter.paste, .scroll = Adapter.scroll, .click = Adapter.click, .mouse_button = Adapter.mouseButton, .pointer = Adapter.pointerInput, .action = Adapter.action, .exited = Adapter.exited, .tick = Adapter.tick, .probe = Adapter.probe });
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
/// Publishes the active project folder name and optional Git branch into top-bar chrome.
pub fn setProjectInfo(name: []const u8, branch: []const u8) !void {
    const name_ptr: [*c]const u8 = if (name.len == 0) null else name.ptr;
    const branch_ptr: [*c]const u8 = if (branch.len == 0) null else branch.ptr;
    if (c.vex_window_project_info(name_ptr, name.len, branch_ptr, branch.len) != 0) return error.InvalidProjectInfo;
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
/// Publishes validated UTF-8 text to the system clipboard; transient lock contention is ignored by the bridge.
pub fn writeClipboardText(text: []const u8) !void {
    if (text.len == 0 or std.mem.indexOfScalar(u8, text, 0) != null or !std.unicode.utf8ValidateSlice(text)) return error.InvalidClipboardText;
    if (c.vex_clipboard_write(text.ptr, text.len) != 0) return error.ClipboardWriteFailed;
}
/// Reads CF_UNICODETEXT from the system clipboard and routes it through the active session paste handler.
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
/// Tab index/count come from the bounded workspace model; empty title falls back to the shell display name.
pub fn drawTab(index: usize, count: usize, active: bool, editing: bool, title: []const u8, icons: []const app_icon.AppIcon, summary: status.ProgramStatusSummary, attention: bool) void {
    var native_icons: [8]c.VexAppIcon = undefined;
    const icon_count: usize = @min(icons.len, native_icons.len);
    for (icons[0..icon_count], 0..) |icon, idx| {
        native_icons[idx] = nativeAppIcon(icon);
    }
    const icons_ptr: [*c]const c.VexAppIcon = if (icon_count == 0) null else &native_icons;
    c.vex_draw_tab(@intCast(index), @intCast(count), active, editing, if (title.len == 0) null else title.ptr, title.len, icons_ptr, icon_count, nativeStatusBadge(summary), attention);
}
fn nativeAppIcon(icon: app_icon.AppIcon) c.VexAppIcon {
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
pub fn beginPane(bounds: panes.PaneRect, focused: bool, hovered: bool, can_focus: bool, zoomed: bool, hovered_action: ?panes.PaneHeaderAction, title: []const u8, icon: app_icon.AppIcon, summary: status.ProgramStatusSummary) void {
    const action_code: u8 = if (hovered_action) |action| @intFromEnum(action) else 0;
    c.vex_begin_pane(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height }, focused, hovered, can_focus, zoomed, action_code, if (title.len == 0) null else title.ptr, title.len, nativeAppIcon(icon), nativeStatusBadge(summary));
}
/// Ends the current pane clip before drawing another pane or returning to chrome.
pub fn endPane() void {
    c.vex_end_pane();
}
/// Draws the rounded scrollback thumb overlay along the right edge of a pane.
pub fn drawScrollbar(bounds: panes.PaneRect, geometry: ScrollbarGeometry, active: bool) void {
    c.vex_draw_scrollbar(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height }, geometry.thumb_y, geometry.thumb_height, active);
}
/// Draws the translucent split preview overlay for an active pane header drag-to-split gesture.
pub fn drawSplitPreview(bounds: panes.PaneRect) void {
    c.vex_draw_split_preview(.{ .x = bounds.x, .y = bounds.y, .width = bounds.width, .height = bounds.height });
}
/// Draws a borrowed parsed snapshot; no C handles or pointers enter the application model.
pub fn drawTerminalFrame(frame: *const terminal.TerminalFrame, focused: bool, hovered_link: ?HoveredLinkSpan) !void {
    const cursor = frame.cursor;
    const cursor_visible = focused and cursor.visible and cursor.column < frame.columns and cursor.row < frame.rows;
    const block_cursor = cursor_visible and cursor.shape == .block;
    var cached_fg: u32 = 0xe4e4e7;
    var cached_bg: u32 = 0x282c34;
    var cached_corrected_fg: u32 = 0xe4e4e7;

    for (0..frame.rows) |row| {
        const cells = frame.cells.items[row * frame.columns ..][0..frame.columns];
        const row_link = if (hovered_link) |hl| (if (hl.row == row) hl else null) else null;
        var start: usize = 0;
        var run_cells: usize = 0;
        var buffer: [4096]u8 = undefined;
        var length: usize = 0;
        var style: terminal.TextStyle = .{ .foreground = 0xe4e4e7, .background = 0x282c34 };
        for (cells, 0..) |cell, column| {
            if (cell.width == .tail) continue;
            const text = frame.cellText(cell);
            const is_block_cursor_cell = block_cursor and row == cursor.row and column == cursor.column;
            const in_link = if (row_link) |hl| (column >= hl.start_col and column <= hl.end_col) else false;
            var cell_style = cell.style;
            if (in_link) {
                cell_style.underline = true;
            }
            if (is_block_cursor_cell) {
                cell_style.background = 0xe4e4e7;
                cell_style.foreground = 0x282c34;
            } else if (cell.selected) {
                cell_style.background = 0x3b4261;
                cell_style.foreground = 0xffffff;
            } else if (in_link) {
                cell_style.foreground = 0x61afef;
            } else if (!cell_style.invisible and !isBlockGraphic(text)) {
                if (cell_style.foreground != cached_fg or cell_style.background != cached_bg) {
                    cached_fg = cell_style.foreground;
                    cached_bg = cell_style.background;
                    cached_corrected_fg = ensureMinimumContrast(cached_fg, cached_bg);
                }
                cell_style.foreground = cached_corrected_fg;
            }
            const ascii = cell.width == .narrow and (text.len == 0 or (text.len == 1 and text[0] >= 32 and text[0] <= 126));
            if (!ascii or !std.meta.eql(style, cell_style) or length == buffer.len) {
                try drawRun(start, row, run_cells, buffer[0..length], style);
                run_cells = 0;
                length = 0;
                start = column;
                style = cell_style;
            }
            if (!ascii) {
                const width: usize = if (cell.width == .wide) 2 else 1;
                try drawRun(column, row, width, text, cell_style);
                start = column + width;
            } else {
                buffer[length] = if (text.len == 0) ' ' else text[0];
                length += 1;
                run_cells += 1;
            }
        }
        try drawRun(start, row, run_cells, buffer[0..length], style);
    }
    if (cursor_visible and cursor.shape != .block) {
        c.vex_draw_cursor(cursor.column, cursor.row, @intFromEnum(cursor.shape), 0xe4e4e7);
    }
}

pub fn isBlockGraphic(text: []const u8) bool {
    if (text.len != 3 or (text[0] & 0xF0) != 0xE0) return false;
    const cp: u32 = (@as(u32, text[0] & 0x0F) << 12) | (@as(u32, text[1] & 0x3F) << 6) | @as(u32, text[2] & 0x3F);
    return (cp >= 0x2580 and cp <= 0x259F) or (cp >= 0xE0B0 and cp <= 0xE0B7);
}

pub fn ensureMinimumContrast(fg_rgb: u32, bg_rgb: u32) u32 {
    if (fg_rgb == bg_rgb) return fg_rgb;
    const min_delta_l: f32 = 0.40;
    const bg_l = srgbToOklabL(bg_rgb);
    const fg_lab = srgbToOklab(fg_rgb);
    const delta = @abs(fg_lab.l - bg_l);
    if (delta >= min_delta_l) return fg_rgb;

    var target_l: f32 = undefined;
    if (bg_l < 0.5) {
        target_l = @min(@as(f32, 0.96), bg_l + min_delta_l);
        if (target_l - bg_l < min_delta_l * 0.6 and bg_l > min_delta_l) {
            target_l = @max(@as(f32, 0.04), bg_l - min_delta_l);
        }
    } else {
        target_l = @max(@as(f32, 0.04), bg_l - min_delta_l);
        if (bg_l - target_l < min_delta_l * 0.6 and (1.0 - bg_l) > min_delta_l) {
            target_l = @min(@as(f32, 0.96), bg_l + min_delta_l);
        }
    }
    return oklabToSrgb(target_l, fg_lab.a, fg_lab.b);
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

fn srgbToOklabL(rgb_val: u32) f32 {
    const r = srgbChannelToLinear(rgb_val >> 16);
    const g = srgbChannelToLinear(rgb_val >> 8);
    const b = srgbChannelToLinear(rgb_val);
    const l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
    const m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
    const s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;
    const l_ = std.math.cbrt(l);
    const m_ = std.math.cbrt(m);
    const s_ = std.math.cbrt(s);
    return 0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_;
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

fn drawRun(column: usize, row: usize, cells: usize, text: []const u8, style: terminal.TextStyle) !void {
    if (cells == 0) return;
    if (style.background == 0x282c34 and !style.underline and !style.strikethrough) {
        if (std.mem.indexOfNonePos(u8, text, 0, " ") == null) return;
    }
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

test "ensureMinimumContrast preserves readable colors and boosts low-contrast foregrounds while exempting block graphics" {
    // High contrast default foreground on default background stays untouched.
    try std.testing.expectEqual(@as(u32, 0xe4e4e7), ensureMinimumContrast(0xe4e4e7, 0x282c34));
    // Identical fg and bg (intentionally hidden text) stays untouched.
    try std.testing.expectEqual(@as(u32, 0x282c34), ensureMinimumContrast(0x282c34, 0x282c34));
    // Dark navy foreground on dark background gets lifted in lightness.
    const boosted = ensureMinimumContrast(0x1f2430, 0x282c34);
    try std.testing.expect(boosted != 0x1f2430);
    try std.testing.expect(srgbToOklabL(boosted) - srgbToOklabL(0x282c34) >= 0.38);
    // Block elements and Powerline glyphs are recognized so their exact shade/separator colors are preserved.
    try std.testing.expect(isBlockGraphic("█"));
    try std.testing.expect(isBlockGraphic("░"));
    try std.testing.expect(isBlockGraphic("\u{E0B0}"));
    try std.testing.expect(!isBlockGraphic("─"));
    try std.testing.expect(!isBlockGraphic("A"));
}

