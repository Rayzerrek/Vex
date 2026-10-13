const std = @import("std");
const binding = @import("api.zig");
const c = binding.c;
const check = binding.checkResult;

/// Rendered cell width comes from the public Ghostty API, never packed cell bits.
pub const CellWidth = enum { narrow, wide, tail, head };
/// Parsed terminal text style; internal callers do not inspect C flags or color unions.
pub const TextStyle = struct {
    foreground: u32,
    background: u32,
    bold: bool = false,
    italic: bool = false,
    underline: bool = false,
    strikethrough: bool = false,
    invisible: bool = false,
};
/// Text offsets borrow the owning frame's UTF-8 buffer, valid until the next dirty snapshot.
pub const TerminalCell = struct { width: CellWidth, style: TextStyle, text_start: usize, text_length: usize };
/// Cursor position is in viewport cells; hidden cursors have no drawable position.
pub const TerminalCursor = struct {
    column: u16 = 0,
    row: u16 = 0,
    visible: bool = false,
    shape: enum(u32) { bar = 0, block = 1, underline = 2, hollow = 3 } = .block,
};
/// A terminal owns this reusable snapshot; consumers must not retain slices across updates.
pub const TerminalFrame = struct {
    columns: u16 = 0,
    rows: u16 = 0,
    cells: std.ArrayList(TerminalCell) = .empty,
    utf8: std.ArrayList(u8) = .empty,
    cursor: TerminalCursor = .{},
    generation: u64 = 0,
    /// Bounds-checked UTF-8 text for one parsed cell, never a null-terminated C string.
    pub fn cellText(self: *const TerminalFrame, cell: TerminalCell) []const u8 {
        return self.utf8.items[cell.text_start..][0..cell.text_length];
    }
};
/// PTY replies are borrowed only during write; failures are returned by feedOutput.
pub const ResponseSink = struct { context: *anyopaque, write: *const fn (*anyopaque, []const u8) anyerror!void };

/// Owns one emulator, iterators, key encoder and frame buffers. Never copy a live owner.
pub const Terminal = struct {
    allocator: std.mem.Allocator,
    api: *const binding.Api,
    handle: std.meta.Child(c.GhosttyTerminal),
    render_state: std.meta.Child(c.GhosttyRenderState),
    row_iterator: std.meta.Child(c.GhosttyRenderStateRowIterator),
    row_cells: std.meta.Child(c.GhosttyRenderStateRowCells),
    key_encoder: std.meta.Child(c.GhosttyKeyEncoder),
    key_event: std.meta.Child(c.GhosttyKeyEvent),
    frame: TerminalFrame = .{},
    grapheme: std.ArrayList(u8) = .empty,
    search_row: std.ArrayList(u8) = .empty,
    frame_valid: bool = false,
    response_sink: ?ResponseSink = null,
    callback_error: ?anyerror = null,
    directory_sink: ?struct { context: *anyopaque, on_report: *const fn (*anyopaque, []const u8) anyerror!void } = null,
    const max_frame_cells = 1024 * 1024;
    const max_grapheme_bytes = 64 * 1024;
    const max_frame_text_bytes = 64 * 1024 * 1024;

    /// Construction rolls back every successful native allocation on any later failure.
    pub fn init(allocator: std.mem.Allocator, api: *const binding.Api) !Terminal {
        var terminal: c.GhosttyTerminal = null;
        try check(api.ghostty_terminal_new(null, &terminal, 80, 24));
        const handle = terminal orelse return error.TerminalNullHandle;
        errdefer api.ghostty_terminal_free(handle);
        var render: c.GhosttyRenderState = null;
        try check(api.ghostty_render_state_new(null, &render));
        const render_state = render orelse return error.TerminalNullHandle;
        errdefer api.ghostty_render_state_free(render_state);
        var iterator: c.GhosttyRenderStateRowIterator = null;
        try check(api.ghostty_render_state_row_iterator_new(null, &iterator));
        const row_iterator = iterator orelse return error.TerminalNullHandle;
        errdefer api.ghostty_render_state_row_iterator_free(row_iterator);
        var cells: c.GhosttyRenderStateRowCells = null;
        try check(api.ghostty_render_state_row_cells_new(null, &cells));
        const row_cells = cells orelse return error.TerminalNullHandle;
        errdefer api.ghostty_render_state_row_cells_free(row_cells);
        var encoder: c.GhosttyKeyEncoder = null;
        try check(api.ghostty_key_encoder_new(null, &encoder));
        const key_encoder = encoder orelse return error.TerminalNullHandle;
        errdefer api.ghostty_key_encoder_free(key_encoder);
        var event: c.GhosttyKeyEvent = null;
        try check(api.ghostty_key_event_new(null, &event));
        const key_event = event orelse return error.TerminalNullHandle;
        errdefer api.ghostty_key_event_free(key_event);
        var owner: Terminal = .{ .allocator = allocator, .api = api, .handle = handle, .render_state = render_state, .row_iterator = row_iterator, .row_cells = row_cells, .key_encoder = key_encoder, .key_event = key_event };
        try owner.applyDefaultPalette();
        return owner;
    }
    /// Consumes this owner after all output callbacks have stopped; no worker owns these handles.
    pub fn deinit(self: *Terminal) void {
        self.api.ghostty_key_event_free(self.key_event);
        self.api.ghostty_key_encoder_free(self.key_encoder);
        self.api.ghostty_render_state_row_cells_free(self.row_cells);
        self.api.ghostty_render_state_row_iterator_free(self.row_iterator);
        self.api.ghostty_render_state_free(self.render_state);
        self.api.ghostty_terminal_free(self.handle);
        self.frame.cells.deinit(self.allocator);
        self.frame.utf8.deinit(self.allocator);
        self.grapheme.deinit(self.allocator);
        self.search_row.deinit(self.allocator);
    }
    /// Call only after placing the owner at its final stable address; callbacks borrow self.
    pub fn attachResponseSink(self: *Terminal, sink: ResponseSink) !void {
        self.response_sink = sink;
        try check(self.api.ghostty_terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_USERDATA, self));
        const callback: c.GhosttyTerminalWritePtyFn = terminalResponse;
        try check(self.api.ghostty_terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_WRITE_PTY, @ptrCast(callback)));
    }
    /// Registers a stable typed directory owner; each report is parsed before a later report can overwrite it.
    pub fn attachDirectorySink(self: *Terminal, owner: anytype) !void {
        const Owner = @TypeOf(owner);
        const Adapter = struct {
            fn write(context: *anyopaque, bytes: []const u8) !void {
                const typed: Owner = @ptrCast(@alignCast(context));
                try typed.onDirectoryReport(bytes);
            }
        };
        self.directory_sink = .{ .context = owner, .on_report = Adapter.write };
        try check(self.api.ghostty_terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_USERDATA, self));
        const callback: c.GhosttyTerminalPwdChangedFn = directoryChanged;
        try check(self.api.ghostty_terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_PWD_CHANGED, @ptrCast(callback)));
    }
    /// Consumes arbitrary VT bytes, including UTF-8 split across pipe reads; not a text-only input.
    pub fn feedOutput(self: *Terminal, bytes: []const u8) !void {
        if (self.callback_error) |err| return err;
        self.api.ghostty_terminal_vt_write(self.handle, bytes.ptr, bytes.len);
        if (self.callback_error) |err| return err;
    }
    /// Borrows raw directory-report bytes only until the next feed/reset/deinit; the caller parses the URI.
    pub fn reportedDirectory(self: *const Terminal) ![]const u8 {
        var value: c.GhosttyString = .{ .ptr = null, .len = 0 };
        try check(self.api.ghostty_terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_PWD, &value));
        if (value.len != 0 and value.ptr == null) return error.InvalidTerminalDirectoryString;
        return if (value.len == 0) &.{} else value.ptr[0..value.len];
    }
    /// Validate ConPTY-compatible dimensions before entering the native emulator.
    pub fn resize(self: *Terminal, columns: u16, rows: u16, width_px: u32, height_px: u32) !void {
        if (columns < 2 or rows == 0 or columns > 32767 or rows > 32767 or width_px == 0 or height_px == 0) return error.InvalidTerminalSize;
        if (self.callback_error) |err| return err;
        try check(self.api.ghostty_terminal_resize(self.handle, columns, rows, width_px, height_px));
        self.frame_valid = false;
        if (self.callback_error) |err| return err;
    }
    /// Scroll changes invalidate the cached viewport without changing the live shell cursor.
    pub fn scroll(self: *Terminal, delta: i32) void {
        self.api.ghostty_terminal_scroll_viewport(self.handle, .{ .tag = c.GHOSTTY_SCROLL_VIEWPORT_DELTA, .value = .{ .delta = delta } });
        self.frame_valid = false;
    }
    /// Encode navigation/function keys with the terminal's currently negotiated modes.
    pub fn encodeKey(self: *Terminal, virtual_key: u32, mods: u32, output: []u8) !?[]const u8 {
        const key = physicalTerminalKey(virtual_key) orelse return null;
        if (mods > 7) return error.InvalidKeyModifiers;
        self.api.ghostty_key_encoder_setopt_from_terminal(self.key_encoder, self.handle);
        self.api.ghostty_key_event_set_action(self.key_event, c.GHOSTTY_KEY_ACTION_PRESS);
        self.api.ghostty_key_event_set_key(self.key_event, key);
        self.api.ghostty_key_event_set_mods(self.key_event, @intCast(mods));
        var length: usize = 0;
        try check(self.api.ghostty_key_encoder_encode(self.key_encoder, self.key_event, output.ptr, output.len, &length));
        if (length > output.len) return error.InvalidTerminalBufferLength;
        return output[0..length];
    }
    /// Clean frames reuse buffers. Dirty frames re-read every row to avoid Ghostty scroll under-reporting.
    pub fn readFrame(self: *Terminal) !*const TerminalFrame {
        try check(self.api.ghostty_render_state_update(self.render_state, self.handle));
        var dirty: c.GhosttyRenderStateDirty = undefined;
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_DIRTY, &dirty);
        self.frame.cursor = try self.readCursor();
        if (dirty == c.GHOSTTY_RENDER_STATE_DIRTY_FALSE and self.frame_valid) return &self.frame;
        if (dirty > c.GHOSTTY_RENDER_STATE_DIRTY_FULL) return error.InvalidTerminalDirtyState;
        self.frame_valid = false;
        var columns: u16 = 0;
        var rows: u16 = 0;
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_COLS, &columns);
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_ROWS, &rows);
        const count = try std.math.mul(usize, columns, rows);
        if (columns == 0 or rows == 0 or count > max_frame_cells) return error.TerminalFrameLimit;
        try self.frame.cells.ensureTotalCapacity(self.allocator, count);
        self.frame.cells.clearRetainingCapacity();
        self.frame.utf8.clearRetainingCapacity();
        var foreground: c.GhosttyColorRgb = undefined;
        var background: c.GhosttyColorRgb = undefined;
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_COLOR_FOREGROUND, &foreground);
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_COLOR_BACKGROUND, &background);
        var iterator: c.GhosttyRenderStateRowIterator = self.row_iterator;
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR, @ptrCast(&iterator));
        var row: usize = 0;
        while (self.api.ghostty_render_state_row_iterator_next(self.row_iterator)) : (row += 1) {
            if (row >= rows) return error.InvalidTerminalRowCount;
            var cells: c.GhosttyRenderStateRowCells = self.row_cells;
            try check(self.api.ghostty_render_state_row_get(self.row_iterator, c.GHOSTTY_RENDER_STATE_ROW_DATA_CELLS, @ptrCast(&cells)));
            var column: usize = 0;
            while (self.api.ghostty_render_state_row_cells_next(self.row_cells)) : (column += 1) {
                if (column >= columns) return error.InvalidTerminalColumnCount;
                const cell = try self.readCell(foreground, background);
                self.frame.cells.appendAssumeCapacity(cell);
            }
            if (column != columns) return error.InvalidTerminalColumnCount;
            const clean = false;
            try check(self.api.ghostty_render_state_row_set(self.row_iterator, c.GHOSTTY_RENDER_STATE_ROW_OPTION_DIRTY, &clean));
        }
        if (row != rows or self.frame.cells.items.len != count) return error.InvalidTerminalRowCount;
        const clean: c.GhosttyRenderStateDirty = c.GHOSTTY_RENDER_STATE_DIRTY_FALSE;
        try check(self.api.ghostty_render_state_set(self.render_state, c.GHOSTTY_RENDER_STATE_OPTION_DIRTY, &clean));
        self.frame.columns = columns;
        self.frame.rows = rows;
        self.frame.generation = try std.math.add(u64, self.frame.generation, 1);
        self.frame_valid = true;
        return &self.frame;
    }
    /// Viewport search is for diagnostics; it shares the renderer's parsed snapshot, not raw PTY bytes.
    pub fn containsViewportText(self: *Terminal, text: []const u8) !bool {
        const frame = try self.readFrame();
        for (0..frame.rows) |row| {
            self.search_row.clearRetainingCapacity();
            for (frame.cells.items[row * frame.columns ..][0..frame.columns]) |cell| {
                if (cell.width == .tail) continue;
                const bytes = frame.cellText(cell);
                try self.search_row.appendSlice(self.allocator, if (bytes.len == 0) " " else bytes);
            }
            if (std.mem.indexOf(u8, self.search_row.items, text) != null) return true;
        }
        return false;
    }

    fn getRender(self: *Terminal, field: c.GhosttyRenderStateData, output: ?*anyopaque) !void {
        try check(self.api.ghostty_render_state_get(self.render_state, field, output));
    }
    fn readCursor(self: *Terminal) !TerminalCursor {
        var cursor: TerminalCursor = .{};
        var in_viewport = false;
        var shape: c.GhosttyRenderStateCursorVisualStyle = 0;
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VISIBLE, &cursor.visible);
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_HAS_VALUE, &in_viewport);
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VISUAL_STYLE, &shape);
        cursor.shape = switch (shape) {
            0 => .bar,
            1 => .block,
            2 => .underline,
            3 => .hollow,
            else => return error.InvalidTerminalCursorShape,
        };
        cursor.visible = cursor.visible and in_viewport;
        if (cursor.visible) {
            try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_X, &cursor.column);
            try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_Y, &cursor.row);
        }
        return cursor;
    }
    fn readCell(self: *Terminal, default_fg: c.GhosttyColorRgb, default_bg: c.GhosttyColorRgb) !TerminalCell {
        var raw: c.GhosttyCell = undefined;
        try check(self.api.ghostty_render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_RAW, &raw));
        var wide: c.GhosttyCellWide = undefined;
        try check(self.api.ghostty_cell_get(raw, c.GHOSTTY_CELL_DATA_WIDE, &wide));
        const width: CellWidth = switch (wide) {
            0 => .narrow,
            1 => .wide,
            2 => .tail,
            3 => .head,
            else => return error.InvalidTerminalCellWidth,
        };
        const style = try self.readStyle(default_fg, default_bg);
        var scratch: [128]u8 = undefined;
        var text: c.GhosttyBuffer = .{ .ptr = &scratch, .cap = scratch.len, .len = 0 };
        const result = self.api.ghostty_render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_UTF8, &text);
        const bytes: []const u8 = if (result == c.GHOSTTY_OUT_OF_SPACE) blk: {
            if (text.len > max_grapheme_bytes) return error.TerminalGraphemeLimit;
            try self.grapheme.resize(self.allocator, text.len);
            text.ptr = self.grapheme.items.ptr;
            text.cap = self.grapheme.items.len;
            try check(self.api.ghostty_render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_UTF8, &text));
            if (text.len > self.grapheme.items.len) return error.InvalidTerminalBufferLength;
            break :blk self.grapheme.items[0..text.len];
        } else blk: {
            try check(result);
            if (text.len > scratch.len) return error.InvalidTerminalBufferLength;
            break :blk scratch[0..text.len];
        };
        if (!std.unicode.utf8ValidateSlice(bytes)) return error.InvalidTerminalUtf8;
        const start = self.frame.utf8.items.len;
        if (bytes.len > max_frame_text_bytes - start) return error.TerminalFrameTextLimit;
        try self.frame.utf8.appendSlice(self.allocator, bytes);
        return .{ .width = width, .style = style, .text_start = start, .text_length = bytes.len };
    }
    fn readStyle(self: *Terminal, default_fg: c.GhosttyColorRgb, default_bg: c.GhosttyColorRgb) !TextStyle {
        var foreground = default_fg;
        var background = default_bg;
        const fg = self.api.ghostty_render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_FG_COLOR, &foreground);
        if (fg == c.GHOSTTY_INVALID_VALUE) foreground = default_fg else try check(fg);
        const bg = self.api.ghostty_render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR, &background);
        if (bg == c.GHOSTTY_INVALID_VALUE) background = default_bg else try check(bg);
        var style = std.mem.zeroes(c.GhosttyStyle);
        style.size = @sizeOf(c.GhosttyStyle);
        try check(self.api.ghostty_render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_STYLE, &style));
        if (style.inverse) std.mem.swap(c.GhosttyColorRgb, &foreground, &background);
        if (style.faint) {
            foreground.r = @intCast((@as(u16, foreground.r) + background.r) / 2);
            foreground.g = @intCast((@as(u16, foreground.g) + background.g) / 2);
            foreground.b = @intCast((@as(u16, foreground.b) + background.b) / 2);
        }
        return .{ .foreground = packedRgb(foreground), .background = packedRgb(background), .bold = style.bold, .italic = style.italic, .underline = style.underline != 0, .strikethrough = style.strikethrough, .invisible = style.invisible };
    }
    fn applyDefaultPalette(self: *Terminal) !void {
        const foreground = colorRgb(0xe4e4e7);
        const background = colorRgb(0x282c34);
        try check(self.api.ghostty_terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND, &foreground));
        try check(self.api.ghostty_terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_COLOR_BACKGROUND, &background));
        var palette: [256]c.GhosttyColorRgb = undefined;
        const ansi = [_]u32{ 0x282c34, 0xff5a5a, 0x5af05a, 0xf0d25a, 0x5a5aff, 0xf05af0, 0x5af0f0, 0xe4e4e7, 0x3d4354, 0xff8c8c, 0x8cff8c, 0xffe68c, 0x8c8cff, 0xff8cff, 0x8cffff, 0xffffff };
        for (ansi, 0..) |color, index| palette[index] = colorRgb(color);
        const levels = [_]u8{ 0, 95, 135, 175, 215, 255 };
        for (16..232) |index| {
            const offset = index - 16;
            palette[index] = .{ .r = levels[offset / 36], .g = levels[(offset / 6) % 6], .b = levels[offset % 6] };
        }
        for (232..256) |index| {
            const level: u8 = @intCast(8 + (index - 232) * 10);
            palette[index] = .{ .r = level, .g = level, .b = level };
        }
        try check(self.api.ghostty_terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_COLOR_PALETTE, &palette));
    }
};

fn terminalResponse(_: c.GhosttyTerminal, userdata: ?*anyopaque, bytes: [*c]const u8, length: usize) callconv(.c) void {
    const context = userdata orelse return;
    // Callback userdata is registered only after the terminal owner reaches its stable address.
    const self: *Terminal = @ptrCast(@alignCast(context));
    if (self.callback_error != null or length == 0) return;
    if (bytes == null or length > 64 * 1024) {
        self.callback_error = error.InvalidTerminalResponse;
        return;
    }
    const sink = self.response_sink orelse {
        self.callback_error = error.MissingResponseSink;
        return;
    };
    sink.write(sink.context, bytes[0..length]) catch |err| {
        self.callback_error = err;
    };
}
fn directoryChanged(_: c.GhosttyTerminal, userdata: ?*anyopaque) callconv(.c) void {
    const context = userdata orelse return;
    const self: *Terminal = @ptrCast(@alignCast(context));
    if (self.callback_error != null) return;
    const sink = self.directory_sink orelse return;
    const report = self.reportedDirectory() catch |err| {
        self.callback_error = err;
        return;
    };
    sink.on_report(sink.context, report) catch |err| {
        self.callback_error = err;
    };
}

fn colorRgb(color: u32) c.GhosttyColorRgb {
    return .{ .r = @truncate(color >> 16), .g = @truncate(color >> 8), .b = @truncate(color) };
}
fn packedRgb(color: c.GhosttyColorRgb) u32 {
    return (@as(u32, color.r) << 16) | (@as(u32, color.g) << 8) | color.b;
}
fn physicalTerminalKey(virtual_key: u32) ?c.GhosttyKey {
    return switch (virtual_key) {
        0x21 => c.GHOSTTY_KEY_PAGE_UP,
        0x22 => c.GHOSTTY_KEY_PAGE_DOWN,
        0x23 => c.GHOSTTY_KEY_END,
        0x24 => c.GHOSTTY_KEY_HOME,
        0x25 => c.GHOSTTY_KEY_ARROW_LEFT,
        0x26 => c.GHOSTTY_KEY_ARROW_UP,
        0x27 => c.GHOSTTY_KEY_ARROW_RIGHT,
        0x28 => c.GHOSTTY_KEY_ARROW_DOWN,
        0x2d => c.GHOSTTY_KEY_INSERT,
        0x2e => c.GHOSTTY_KEY_DELETE,
        0x70...0x87 => @as(c.GhosttyKey, c.GHOSTTY_KEY_F1) + virtual_key - 0x70,
        else => null,
    };
}

test "pinned sized structs and UTF-8/color/wide cells match the actual DLL" {
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(c.GhosttyStyle));
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(c.GhosttyBuffer));
    try std.testing.expectEqual(@as(usize, 3), @sizeOf(c.GhosttyColorRgb));
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b[31mA\x1b[0mż界");
    const frame = try terminal.readFrame();
    try std.testing.expectEqualStrings("A", frame.cellText(frame.cells.items[0]));
    try std.testing.expectEqual(@as(u32, 0xff5a5a), frame.cells.items[0].style.foreground);
    try std.testing.expectEqualStrings("ż", frame.cellText(frame.cells.items[1]));
    try std.testing.expectEqualStrings("界", frame.cellText(frame.cells.items[2]));
    try std.testing.expectEqual(CellWidth.wide, frame.cells.items[2].width);
    try std.testing.expectEqual(CellWidth.tail, frame.cells.items[3].width);
    try terminal.resize(120, 40, 9, 17);
    try std.testing.expectEqual(@as(u16, 120), (try terminal.readFrame()).columns);
    try std.testing.expectError(error.InvalidTerminalSize, terminal.resize(0, 0, 0, 0));
}

test "terminal instances isolate output and clean snapshots retain generation and storage" {
    const api = try binding.Api.load();
    var first = try Terminal.init(std.testing.allocator, &api);
    defer first.deinit();
    var second = try Terminal.init(std.testing.allocator, &api);
    defer second.deinit();
    try first.feedOutput("ONLY_FIRST");
    try second.feedOutput("ONLY_SECOND");
    try std.testing.expect(try first.containsViewportText("ONLY_FIRST"));
    try std.testing.expect(!try first.containsViewportText("ONLY_SECOND"));
    try std.testing.expect(try second.containsViewportText("ONLY_SECOND"));
    const frame = try first.readFrame();
    const generation = frame.generation;
    const storage = frame.cells.items.ptr;
    _ = try first.readFrame();
    try std.testing.expectEqual(generation, frame.generation);
    try std.testing.expectEqual(storage, frame.cells.items.ptr);
}

test "alternate screen, scrollback, cursor keys and split UTF-8 retain terminal semantics" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("MARKER\xc5");
    try terminal.feedOutput("\xbc");
    try std.testing.expect(try terminal.containsViewportText("MARKERż"));
    try terminal.feedOutput("\x1b[?1049hALT");
    try std.testing.expect(!try terminal.containsViewportText("MARKER"));
    try terminal.feedOutput("\x1b[?1049l");
    try std.testing.expect(try terminal.containsViewportText("MARKER"));
    for (0..80) |_| try terminal.feedOutput("\r\nnext");
    try std.testing.expect(!try terminal.containsViewportText("MARKER"));
    terminal.scroll(-1000);
    try std.testing.expect(try terminal.containsViewportText("MARKER"));
    var bytes: [64]u8 = undefined;
    const normal = try terminal.encodeKey(0x26, 0, &bytes) orelse return error.MissingEncodedKey;
    try std.testing.expectEqualStrings("\x1b[A", normal);
    try terminal.feedOutput("\x1b[?1h");
    const application = try terminal.encodeKey(0x26, 0, &bytes) orelse return error.MissingEncodedKey;
    try std.testing.expectEqualStrings("\x1bOA", application);
    try std.testing.expectEqual(@as(?[]const u8, null), try terminal.encodeKey('A', 0, &bytes));
}

const TestReply = struct {
    bytes: [64]u8 = undefined,
    length: usize = 0,
    fn write(context: *anyopaque, bytes: []const u8) !void {
        const self: *TestReply = @ptrCast(@alignCast(context));
        if (bytes.len > self.bytes.len) return error.TestReplyOverflow;
        @memcpy(self.bytes[0..bytes.len], bytes);
        self.length = bytes.len;
    }
    fn reject(_: *anyopaque, _: []const u8) !void {
        return error.TestPtyWriteFailure;
    }
};

test "PTY reply callback targets its own terminal and propagates sink errors" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var reply: TestReply = .{};
    try terminal.attachResponseSink(.{ .context = &reply, .write = TestReply.write });
    try terminal.feedOutput("\x1b[6n");
    try std.testing.expectEqualStrings("\x1b[1;1R", reply.bytes[0..reply.length]);
    try terminal.attachResponseSink(.{ .context = &reply, .write = TestReply.reject });
    try std.testing.expectError(error.TestPtyWriteFailure, terminal.feedOutput("\x1b[6n"));
    try std.testing.expectError(error.TestPtyWriteFailure, terminal.resize(90, 30, 8, 16));
}

fn checkFrameAllocationFailures(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    try terminal.feedOutput("unicode: ż界\x1b[31mred\x1b[0m");
    _ = try terminal.readFrame();
    try std.testing.expect(try terminal.containsViewportText("unicode"));
    try terminal.feedOutput("\r\nmore");
    _ = try terminal.readFrame();
}

test "all Zig frame allocation failures free native owners and snapshot buffers" {
    const api = try binding.Api.load();
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkFrameAllocationFailures, .{&api});
}

test "viewport text keeps blank-cell gaps and row boundaries, including overlapping prefixes" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("AAAB\x1b[2CC\r\nXYZ");
    try std.testing.expect(try terminal.containsViewportText("AAB"));
    try std.testing.expect(try terminal.containsViewportText("B  C"));
    try std.testing.expect(!try terminal.containsViewportText("BC"));
    try std.testing.expect(!try terminal.containsViewportText("CXYZ"));
}

test "long combining graphemes grow owned buffers without truncation" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var input: [129]u8 = undefined;
    input[0] = 'e';
    for (0..64) |index| {
        input[1 + index * 2] = 0xcc;
        input[2 + index * 2] = 0x81;
    }
    try terminal.feedOutput(&input);
    const frame = try terminal.readFrame();
    try std.testing.expectEqualStrings(&input, frame.cellText(frame.cells.items[0]));
    try std.testing.expect(terminal.grapheme.capacity >= input.len);
}

test "OSC 7 borrowed directory reports survive every UTF-8/terminator chunk boundary" {
    const api = try binding.Api.load();
    for ([_][]const u8{ "\x07", "\x1b\\" }) |terminator| {
        const report = "file:///C:/My%20files/%C5%BB%C3%B3%C5%82%C4%87";
        const bytes = try std.mem.concat(std.testing.allocator, u8, &.{ "\x1b]7;", report, terminator });
        defer std.testing.allocator.free(bytes);
        for (1..bytes.len) |split| {
            var terminal = try Terminal.init(std.testing.allocator, &api);
            defer terminal.deinit();
            try terminal.feedOutput(bytes[0..split]);
            try terminal.feedOutput(bytes[split..]);
            try std.testing.expectEqualStrings(report, try terminal.reportedDirectory());
        }
    }
}

const paths = @import("directory.zig");
const TestDirectoryOwner = struct {
    directory: paths.PaneDirectory,
    pub fn onDirectoryReport(self: *@This(), report: []const u8) !void {
        try self.directory.applyReport(report, "my-pc");
    }
};

test "directory callbacks preserve valid reports before invalid/empty reports in the same read" {
    const api = try binding.Api.load();
    var owner: TestDirectoryOwner = .{ .directory = try paths.PaneDirectory.init(std.testing.allocator, try paths.DirectoryPath.parse("C:\\launch")) };
    defer owner.directory.deinit();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.attachDirectorySink(&owner);
    try terminal.feedOutput("\x1b]7;file:///C:/good\x07\x1b]7;file://remote/C:/bad\x07\x1b]7;\x07");
    try std.testing.expectEqualStrings("C:\\good", owner.directory.current().bytes);
}

fn checkDirectoryCallbackAllocations(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var owner: TestDirectoryOwner = .{ .directory = try paths.PaneDirectory.init(allocator, try paths.DirectoryPath.parse("C:\\launch")) };
    defer owner.directory.deinit();
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    try terminal.attachDirectorySink(&owner);
    terminal.feedOutput("\x1b]7;file:///C:/reported\x07") catch |err| {
        try std.testing.expectEqualStrings("C:\\launch", owner.directory.current().bytes);
        return err;
    };
    try std.testing.expectEqualStrings("C:\\reported", owner.directory.current().bytes);
}

test "native directory callback OOM reaches feedOutput and releases both owners" {
    const api = try binding.Api.load();
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkDirectoryCallbackAllocations, .{&api});
}

test "OSC palette updates invalidate cached resolved colors" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b[31mX");
    const before = try terminal.readFrame();
    const generation = before.generation;
    try std.testing.expectEqual(@as(u32, 0xff5a5a), before.cells.items[0].style.foreground);
    try terminal.feedOutput("\x1b]4;1;rgb:00/ff/00\x1b\\");
    const after = try terminal.readFrame();
    try std.testing.expectEqual(@as(u32, 0x00ff00), after.cells.items[0].style.foreground);
    try std.testing.expect(after.generation > generation);
}
