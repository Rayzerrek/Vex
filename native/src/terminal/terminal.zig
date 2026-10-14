const std = @import("std");
const binding = @import("api.zig");
const c = binding.c;
const check = binding.checkResult;
const status = @import("program_status.zig");
const feed = @import("terminal_feed.zig");
const title_mod = @import("terminal_title.zig");
pub const TerminalTitle = title_mod.TerminalTitle;

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
pub const TerminalCell = struct { width: CellWidth, style: TextStyle, text_start: usize, text_length: usize, selected: bool = false };
/// Cursor position is in viewport cells; hidden cursors have no drawable position.
pub const TerminalCursor = struct {
    column: u16 = 0,
    row: u16 = 0,
    visible: bool = false,
    wide_tail: bool = false,
    shape: enum(u32) { bar = 0, block = 1, underline = 2, hollow = 3 } = .bar,
    /// Caret boundary column; wide-tail cursors sit after the second cell of the wide glyph.
    pub fn caretColumn(self: TerminalCursor) u16 {
        return self.column +| @intFromBool(self.wide_tail);
    }
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
    selection_gesture: std.meta.Child(c.GhosttySelectionGesture),
    press_event: std.meta.Child(c.GhosttySelectionGestureEvent),
    drag_event: std.meta.Child(c.GhosttySelectionGestureEvent),
    release_event: std.meta.Child(c.GhosttySelectionGestureEvent),
    key_encoder: std.meta.Child(c.GhosttyKeyEncoder),
    key_event: std.meta.Child(c.GhosttyKeyEvent),
    cell_width_px: u32 = 9,
    cell_height_px: u32 = 17,
    kb_selection_active: bool = false,
    kb_anchor_col: u16 = 0,
    kb_anchor_row: u16 = 0,
    kb_focus_col: u16 = 0,
    kb_focus_row: u16 = 0,
    frame: TerminalFrame = .{},
    grapheme: std.ArrayList(u8) = .empty,
    search_row: std.ArrayList(u8) = .empty,
    frame_valid: bool = false,
    feed_filter: feed.TerminalFeedFilter = .{},
    program_status: status.ProgramStatusTracker,
    title: title_mod.TerminalTitle = .{},
    status_clock: std.time.Timer,
    response_sink: ?ResponseSink = null,
    callback_error: ?anyerror = null,
    directory_sink: ?struct { context: *anyopaque, on_report: *const fn (*anyopaque, []const u8) anyerror!void } = null,
    clipboard_sink: ?struct { context: *anyopaque, on_write: *const fn (*anyopaque, []const u8) anyerror!void } = null,
    const max_frame_cells = 1024 * 1024;
    const max_grapheme_bytes = 64 * 1024;
    const max_frame_text_bytes = 64 * 1024 * 1024;
    const max_clipboard_bytes = 768 * 1024 - 4;

    /// Construction rolls back every successful native allocation on any later failure.
    pub fn init(allocator: std.mem.Allocator, api: *const binding.Api) !Terminal {
        var terminal: c.GhosttyTerminal = null;
        try check(api.terminal_new(null, &terminal, 80, 24));
        const handle = terminal orelse return error.TerminalNullHandle;
        errdefer api.terminal_free(handle);
        var render: c.GhosttyRenderState = null;
        try check(api.render_state_new(null, &render));
        const render_state = render orelse return error.TerminalNullHandle;
        errdefer api.render_state_free(render_state);
        var iterator: c.GhosttyRenderStateRowIterator = null;
        try check(api.render_state_row_iterator_new(null, &iterator));
        const row_iterator = iterator orelse return error.TerminalNullHandle;
        errdefer api.render_state_row_iterator_free(row_iterator);
        var cells: c.GhosttyRenderStateRowCells = null;
        try check(api.render_state_row_cells_new(null, &cells));
        const row_cells = cells orelse return error.TerminalNullHandle;
        errdefer api.render_state_row_cells_free(row_cells);
        var gesture: c.GhosttySelectionGesture = null;
        try check(api.selection_gesture_new(null, &gesture));
        const selection_gesture = gesture orelse return error.TerminalNullHandle;
        errdefer api.selection_gesture_free(selection_gesture, handle);
        var press: c.GhosttySelectionGestureEvent = null;
        try check(api.selection_gesture_event_new(null, &press, c.GHOSTTY_SELECTION_GESTURE_EVENT_TYPE_PRESS));
        const press_event = press orelse return error.TerminalNullHandle;
        errdefer api.selection_gesture_event_free(press_event);
        var drag: c.GhosttySelectionGestureEvent = null;
        try check(api.selection_gesture_event_new(null, &drag, c.GHOSTTY_SELECTION_GESTURE_EVENT_TYPE_DRAG));
        const drag_event = drag orelse return error.TerminalNullHandle;
        errdefer api.selection_gesture_event_free(drag_event);
        var release: c.GhosttySelectionGestureEvent = null;
        try check(api.selection_gesture_event_new(null, &release, c.GHOSTTY_SELECTION_GESTURE_EVENT_TYPE_RELEASE));
        const release_event = release orelse return error.TerminalNullHandle;
        errdefer api.selection_gesture_event_free(release_event);
        var encoder: c.GhosttyKeyEncoder = null;
        try check(api.key_encoder_new(null, &encoder));
        const key_encoder = encoder orelse return error.TerminalNullHandle;
        errdefer api.key_encoder_free(key_encoder);
        var event: c.GhosttyKeyEvent = null;
        try check(api.key_event_new(null, &event));
        const key_event = event orelse return error.TerminalNullHandle;
        errdefer api.key_event_free(key_event);
        var owner: Terminal = .{
            .allocator = allocator,
            .api = api,
            .handle = handle,
            .render_state = render_state,
            .row_iterator = row_iterator,
            .row_cells = row_cells,
            .selection_gesture = selection_gesture,
            .press_event = press_event,
            .drag_event = drag_event,
            .release_event = release_event,
            .key_encoder = key_encoder,
            .key_event = key_event,
            .program_status = .{ .allocator = allocator },
            .status_clock = try std.time.Timer.start(),
        };
        try owner.applyDefaultPalette();
        return owner;
    }
    /// Consumes this owner after all output callbacks have stopped; no worker owns these handles.
    pub fn deinit(self: *Terminal) void {
        self.api.key_event_free(self.key_event);
        self.api.key_encoder_free(self.key_encoder);
        self.api.selection_gesture_event_free(self.release_event);
        self.api.selection_gesture_event_free(self.drag_event);
        self.api.selection_gesture_event_free(self.press_event);
        self.api.selection_gesture_free(self.selection_gesture, self.handle);
        self.api.render_state_row_cells_free(self.row_cells);
        self.api.render_state_row_iterator_free(self.row_iterator);
        self.api.render_state_free(self.render_state);
        self.api.terminal_free(self.handle);
        self.frame.cells.deinit(self.allocator);
        self.frame.utf8.deinit(self.allocator);
        self.grapheme.deinit(self.allocator);
        self.search_row.deinit(self.allocator);
        self.program_status.deinit();
    }
    /// Call only after placing the owner at its final stable address; callbacks borrow self.
    pub fn attachResponseSink(self: *Terminal, sink: ResponseSink) !void {
        self.response_sink = sink;
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_USERDATA, self));
        const callback: c.GhosttyTerminalWritePtyFn = terminalResponse;
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_WRITE_PTY, @ptrCast(callback)));
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
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_USERDATA, self));
        const callback: c.GhosttyTerminalPwdChangedFn = directoryChanged;
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_PWD_CHANGED, @ptrCast(callback)));
    }
    /// Registers a stable typed clipboard owner for write-only OSC 52 and OSC 1337 Copy sequences.
    pub fn attachClipboardSink(self: *Terminal, owner: anytype) !void {
        const Owner = @TypeOf(owner);
        const Adapter = struct {
            fn write(context: *anyopaque, bytes: []const u8) !void {
                const typed: Owner = @ptrCast(@alignCast(context));
                try typed.onClipboardWrite(bytes);
            }
        };
        self.clipboard_sink = .{ .context = owner, .on_write = Adapter.write };
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_USERDATA, self));
        const callback: c.GhosttyTerminalClipboardWriteFn = clipboardWrite;
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_CLIPBOARD_WRITE, @ptrCast(callback)));
    }
    /// Consumes arbitrary VT bytes, including UTF-8 split across pipe reads; not a text-only input.
    pub fn feedOutput(self: *Terminal, bytes: []const u8) !void {
        if (self.callback_error) |err| return err;
        try self.feed_filter.feedTerminalOutput(self, bytes);
    }
    /// Internal feed-filter seam; every native callback failure reaches the caller before later reports.
    pub fn writeTerminalOutput(self: *Terminal, bytes: []const u8) !void {
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_USERDATA, self));
        self.api.terminal_vt_write(self.handle, bytes.ptr, bytes.len);
        if (self.callback_error) |err| return err;
    }
    /// Capability replies use this pane's PTY sink, never the currently focused terminal.
    pub fn onProgramStatusOsc(self: *Terminal, body: []const u8) !void {
        if (std.mem.eql(u8, body, "?")) {
            const sink = self.response_sink orelse return error.MissingResponseSink;
            try sink.write(sink.context, "\x1b]7501;?\x1b\\");
        } else try self.program_status.applyProgramStatus(body);
    }
    /// Shell command duration uses a monotonic per-terminal clock, not wall-clock timestamps.
    pub fn onShellMarkerOsc(self: *Terminal, body: []const u8) !void {
        try self.program_status.applyShellMarker(body, self.status_clock.read());
    }
    /// Legacy progress is a fallback until this pane receives explicit program status.
    pub fn onProgressOsc(self: *Terminal, body: []const u8) void {
        self.program_status.applyProgressReport(body);
    }
    /// RIS resets status and custom title after the emulator; screen switches/soft resets deliberately do not.
    pub fn onTerminalReset(self: *Terminal) void {
        self.program_status.resetProgramStatus();
        self.title.reset();
    }
    /// Borrows raw directory-report bytes only until the next feed/reset/deinit; the caller parses the URI.
    pub fn reportedDirectory(self: *const Terminal) ![]const u8 {
        var value: c.GhosttyString = .{ .ptr = null, .len = 0 };
        try check(self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_PWD, &value));
        if (value.len != 0 and value.ptr == null) return error.InvalidTerminalDirectoryString;
        return if (value.len == 0) &.{} else value.ptr[0..value.len];
    }
    /// Borrows raw OSC 0/2 title bytes only until the next feed/reset/deinit; the caller sanitizes the string.
    pub fn reportedTitle(self: *const Terminal) ![]const u8 {
        var value: c.GhosttyString = .{ .ptr = null, .len = 0 };
        try check(self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_TITLE, &value));
        if (value.len != 0 and value.ptr == null) return error.InvalidTerminalTitleString;
        return if (value.len == 0) &.{} else value.ptr[0..value.len];
    }
    /// Validate ConPTY-compatible dimensions before entering the native emulator.
    pub fn resize(self: *Terminal, columns: u16, rows: u16, width_px: u32, height_px: u32) !void {
        if (columns < 2 or rows == 0 or columns > 32767 or rows > 32767 or width_px == 0 or height_px == 0) return error.InvalidTerminalSize;
        if (self.callback_error) |err| return err;
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_USERDATA, self));
        try check(self.api.terminal_resize(self.handle, columns, rows, width_px, height_px));
        self.cell_width_px = width_px;
        self.cell_height_px = height_px;
        self.kb_selection_active = false;
        self.frame_valid = false;
        if (self.callback_error) |err| return err;
    }
    /// True when the active screen has a live selection range.
    pub fn hasSelection(self: *const Terminal) !bool {
        var selection = emptySelection();
        const result = self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_SELECTION, &selection);
        if (result == c.GHOSTTY_NO_VALUE) return false;
        try check(result);
        return true;
    }
    /// Clears the active screen selection and optionally resets multi-click gesture history.
    pub fn clearSelection(self: *Terminal, reset_gesture: bool) !void {
        self.kb_selection_active = false;
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_SELECTION, null));
        if (reset_gesture) self.api.selection_gesture_reset(self.selection_gesture, self.handle);
        self.frame_valid = false;
    }
    /// Starts or advances a pointer selection gesture, returning the click count (1=cell, 2=word, 3=line, 0=out of bounds).
    pub fn selectionPress(self: *Terminal, column: u16, row: u16, x_px: f64, y_px: f64) !u8 {
        self.kb_selection_active = false;
        var grid_ref = std.mem.zeroes(c.GhosttyGridRef);
        grid_ref.size = @sizeOf(c.GhosttyGridRef);
        const point: c.GhosttyPoint = .{
            .tag = c.GHOSTTY_POINT_TAG_VIEWPORT,
            .value = .{ .coordinate = .{ .x = column, .y = row } },
        };
        const ref_result = self.api.terminal_grid_ref(self.handle, point, &grid_ref);
        if (ref_result == c.GHOSTTY_INVALID_VALUE or ref_result == c.GHOSTTY_NO_VALUE) return 0;
        try check(ref_result);
        const now_ns: u64 = self.status_clock.read() +| 1_000_000_000;
        const position: c.GhosttySurfacePosition = .{ .x = x_px, .y = y_px };
        try check(self.api.selection_gesture_event_set(self.press_event, c.GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REF, &grid_ref));
        try check(self.api.selection_gesture_event_set(self.press_event, c.GHOSTTY_SELECTION_GESTURE_EVENT_OPT_TIME_NS, &now_ns));
        try check(self.api.selection_gesture_event_set(self.press_event, c.GHOSTTY_SELECTION_GESTURE_EVENT_OPT_POSITION, &position));
        var snapshot = emptySelection();
        const event_result = self.api.selection_gesture_event(self.selection_gesture, self.handle, self.press_event, &snapshot);
        if (event_result == c.GHOSTTY_SUCCESS) {
            try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_SELECTION, &snapshot));
            self.frame_valid = false;
        } else if (event_result == c.GHOSTTY_NO_VALUE) {
            try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_SELECTION, null));
            self.frame_valid = false;
        } else {
            try check(event_result);
        }
        var click_count: u8 = 0;
        try check(self.api.selection_gesture_get(self.selection_gesture, self.handle, c.GHOSTTY_SELECTION_GESTURE_DATA_CLICK_COUNT, &click_count));
        return click_count;
    }
    /// Updates an active pointer selection gesture during mouse drag.
    pub fn selectionDrag(self: *Terminal, column: u16, row: u16, x_px: f64, y_px: f64) !void {
        var grid_ref = std.mem.zeroes(c.GhosttyGridRef);
        grid_ref.size = @sizeOf(c.GhosttyGridRef);
        const point: c.GhosttyPoint = .{
            .tag = c.GHOSTTY_POINT_TAG_VIEWPORT,
            .value = .{ .coordinate = .{ .x = column, .y = row } },
        };
        const ref_result = self.api.terminal_grid_ref(self.handle, point, &grid_ref);
        if (ref_result == c.GHOSTTY_INVALID_VALUE or ref_result == c.GHOSTTY_NO_VALUE) return;
        try check(ref_result);
        var columns: u16 = 0;
        var rows: u16 = 0;
        try check(self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_COLS, &columns));
        try check(self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_ROWS, &rows));
        const geometry: c.GhosttySelectionGestureGeometry = .{
            .columns = @max(1, @as(u32, columns)),
            .cell_width = @max(1, self.cell_width_px),
            .padding_left = 0,
            .screen_height = @max(1, @as(u32, rows) * @max(1, self.cell_height_px)),
        };
        const position: c.GhosttySurfacePosition = .{ .x = x_px, .y = y_px };
        try check(self.api.selection_gesture_event_set(self.drag_event, c.GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REF, &grid_ref));
        try check(self.api.selection_gesture_event_set(self.drag_event, c.GHOSTTY_SELECTION_GESTURE_EVENT_OPT_GEOMETRY, &geometry));
        try check(self.api.selection_gesture_event_set(self.drag_event, c.GHOSTTY_SELECTION_GESTURE_EVENT_OPT_POSITION, &position));
        var snapshot = emptySelection();
        const event_result = self.api.selection_gesture_event(self.selection_gesture, self.handle, self.drag_event, &snapshot);
        if (event_result == c.GHOSTTY_SUCCESS) {
            try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_SELECTION, &snapshot));
            self.frame_valid = false;
        } else if (event_result != c.GHOSTTY_NO_VALUE) {
            try check(event_result);
        }
    }
    /// Completes the active pointer selection gesture on mouse button release.
    pub fn selectionRelease(self: *Terminal, column: u16, row: u16) !void {
        var grid_ref = std.mem.zeroes(c.GhosttyGridRef);
        grid_ref.size = @sizeOf(c.GhosttyGridRef);
        const point: c.GhosttyPoint = .{
            .tag = c.GHOSTTY_POINT_TAG_VIEWPORT,
            .value = .{ .coordinate = .{ .x = column, .y = row } },
        };
        if (self.api.terminal_grid_ref(self.handle, point, &grid_ref) == c.GHOSTTY_SUCCESS) {
            try check(self.api.selection_gesture_event_set(self.release_event, c.GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REF, &grid_ref));
        } else {
            try check(self.api.selection_gesture_event_set(self.release_event, c.GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REF, null));
        }
        var snapshot = emptySelection();
        const event_result = self.api.selection_gesture_event(self.selection_gesture, self.handle, self.release_event, &snapshot);
        if (event_result != c.GHOSTTY_SUCCESS and event_result != c.GHOSTTY_NO_VALUE) {
            try check(event_result);
        }
    }
    /// Sets keyboard selection between half-open viewport caret boundaries without reusing mouse multi-click state.
    pub fn setKeyboardSelection(self: *Terminal, anchor_col: u16, anchor_row: u16, focus_col: u16, focus_row: u16) !void {
        try self.clearSelection(true);
        var columns: u16 = 0;
        var rows: u16 = 0;
        try check(self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_COLS, &columns));
        try check(self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_ROWS, &rows));
        if (columns == 0 or rows == 0) return;
        const cols_u32: u32 = columns;
        const clamped_anchor_col: u32 = @min(@as(u32, anchor_col), cols_u32);
        const clamped_focus_col: u32 = @min(@as(u32, focus_col), cols_u32);
        const clamped_anchor_row: u32 = @min(@as(u32, anchor_row), @as(u32, rows - 1));
        const clamped_focus_row: u32 = @min(@as(u32, focus_row), @as(u32, rows - 1));
        self.kb_selection_active = true;
        self.kb_anchor_col = @intCast(clamped_anchor_col);
        self.kb_anchor_row = @intCast(clamped_anchor_row);
        self.kb_focus_col = @intCast(clamped_focus_col);
        self.kb_focus_row = @intCast(clamped_focus_row);
        var start: u32 = clamped_anchor_row * cols_u32 + clamped_anchor_col;
        var end: u32 = clamped_focus_row * cols_u32 + clamped_focus_col;
        if (start == end) return;
        if (start > end) std.mem.swap(u32, &start, &end);
        // Ghostty selects inclusive cells; keyboard carets delimit a half-open range.
        end -= 1;
        const start_col: u16 = @intCast(start % cols_u32);
        const start_row: u16 = @intCast(start / cols_u32);
        const end_col: u16 = @intCast(end % cols_u32);
        const end_row: u16 = @intCast(end / cols_u32);
        const cell_w: f64 = @floatFromInt(@max(1, self.cell_width_px));
        const cell_h: f64 = @floatFromInt(@max(1, self.cell_height_px));
        _ = try self.selectionPress(
            start_col,
            start_row,
            (@as(f64, @floatFromInt(start_col)) + 0.2) * cell_w,
            (@as(f64, @floatFromInt(start_row)) + 0.5) * cell_h,
        );
        try self.selectionDrag(
            end_col,
            end_row,
            (@as(f64, @floatFromInt(end_col)) + 0.8) * cell_w,
            (@as(f64, @floatFromInt(end_row)) + 0.5) * cell_h,
        );
        try self.selectionRelease(end_col, end_row);
        self.kb_selection_active = true;
    }
    /// Extends or starts a primary-screen keyboard selection on Shift/Ctrl+Shift + Arrow/Home/End.
    pub fn extendKeyboardSelection(self: *Terminal, virtual_key: u32, mods: u32) !bool {
        if (mods != 1 and mods != 3) return false;
        switch (virtual_key) {
            0x23, 0x24, 0x25, 0x26, 0x27, 0x28 => {},
            else => return false,
        }
        if (try self.isAlternateScreen()) return false;
        const frame = try self.readFrame();
        if (frame.columns == 0 or frame.rows == 0) return false;
        if (!self.kb_selection_active) {
            const start_col = @min(frame.cursor.caretColumn(), frame.columns);
            const start_row = @min(frame.cursor.row, frame.rows - 1);
            self.kb_anchor_col = start_col;
            self.kb_anchor_row = start_row;
            self.kb_focus_col = start_col;
            self.kb_focus_row = start_row;
            self.kb_selection_active = true;
        }
        var focus_col = @min(self.kb_focus_col, frame.columns);
        var focus_row = @min(self.kb_focus_row, frame.rows - 1);
        if (mods == 3) {
            switch (virtual_key) {
                0x25 => focus_col = wordBoundaryCol(frame, focus_row, focus_col, false),
                0x27 => focus_col = wordBoundaryCol(frame, focus_row, focus_col, true),
                0x26 => focus_row -|= 1,
                0x28 => focus_row = @min(frame.rows - 1, focus_row +| 1),
                0x24 => focus_col = 0,
                0x23 => focus_col = frame.columns,
                else => unreachable,
            }
        } else {
            switch (virtual_key) {
                0x25 => {
                    if (focus_col > 0) {
                        focus_col -= 1;
                    } else if (focus_row > 0) {
                        focus_row -= 1;
                        focus_col = frame.columns - 1;
                    }
                },
                0x27 => {
                    if (focus_col < frame.columns) {
                        focus_col += 1;
                    } else if (focus_row + 1 < frame.rows) {
                        focus_row += 1;
                        focus_col = 1;
                    }
                },
                0x26 => focus_row -|= 1,
                0x28 => focus_row = @min(frame.rows - 1, focus_row +| 1),
                0x24 => focus_col = 0,
                0x23 => focus_col = frame.columns,
                else => unreachable,
            }
        }
        try self.setKeyboardSelection(self.kb_anchor_col, self.kb_anchor_row, focus_col, focus_row);
        return true;
    }
    /// Consumes a keyboard selection ending at the live cursor and returns the number of 0x7f backspaces to send.
    pub fn consumeKeyboardSelectionBackspaces(self: *Terminal) !usize {
        if (!self.kb_selection_active or !try self.hasSelection() or try self.isAlternateScreen()) return 0;
        var scrollbar: c.GhosttyTerminalScrollbar = .{ .total = 0, .offset = 0, .len = 0 };
        try check(self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_SCROLLBAR, &scrollbar));
        if (scrollbar.offset +| scrollbar.len < scrollbar.total) return 0;
        const frame = try self.readFrame();
        if (frame.columns == 0 or frame.rows == 0 or !frame.cursor.visible) return 0;
        const cols_usize: usize = frame.columns;
        const start_linear: usize = @as(usize, @min(self.kb_anchor_row, frame.rows - 1)) * cols_usize + @min(self.kb_anchor_col, frame.columns);
        const end_linear: usize = @as(usize, @min(self.kb_focus_row, frame.rows - 1)) * cols_usize + @min(self.kb_focus_col, frame.columns);
        if (start_linear == end_linear) return 0;
        const sel_min = @min(start_linear, end_linear);
        const sel_max = @max(start_linear, end_linear);
        const cursor_linear: usize = @as(usize, frame.cursor.row) * cols_usize + frame.cursor.caretColumn();
        if (sel_max != cursor_linear or sel_max > frame.cells.items.len) return 0;
        const selected = (try self.selectedTextAlloc(self.allocator, false)) orelse return 0;
        defer self.allocator.free(selected);
        if (selected.len == 0 or std.mem.indexOfAny(u8, selected, "\r\n") != null) return 0;
        var count: usize = 0;
        for (frame.cells.items[sel_min..sel_max]) |cell| {
            if (cell.width != .tail) count += 1;
        }
        if (count == 0) return 0;
        try self.clearSelection(true);
        return count;
    }
    /// Formats the active selection as UTF-8 plain text with soft-wrap unwrapping; returns null when no selection is active.
    pub fn selectedTextAlloc(self: *const Terminal, allocator: std.mem.Allocator, trim: bool) !?[]u8 {
        const options: c.GhosttyTerminalSelectionFormatOptions = .{
            .size = @sizeOf(c.GhosttyTerminalSelectionFormatOptions),
            .emit = c.GHOSTTY_FORMATTER_FORMAT_PLAIN,
            .unwrap = true,
            .trim = trim,
            .selection = null,
        };
        var required: usize = 0;
        const probe = self.api.terminal_selection_format_buf(self.handle, options, null, 0, &required);
        if (probe == c.GHOSTTY_NO_VALUE) return null;
        if (probe != c.GHOSTTY_OUT_OF_SPACE and probe != c.GHOSTTY_SUCCESS) {
            try check(probe);
        }
        if (required > max_frame_text_bytes) return error.TerminalFrameTextLimit;
        const buffer = try allocator.alloc(u8, required);
        errdefer allocator.free(buffer);
        if (required == 0) return buffer;
        var written: usize = 0;
        try check(self.api.terminal_selection_format_buf(self.handle, options, buffer.ptr, buffer.len, &written));
        if (written != buffer.len) return error.InvalidTerminalBufferLength;
        return buffer;
    }
    /// Scroll changes invalidate the cached viewport without changing the live shell cursor.
    pub fn scroll(self: *Terminal, delta: i32) void {
        self.api.terminal_scroll_viewport(self.handle, .{ .tag = c.GHOSTTY_SCROLL_VIEWPORT_DELTA, .value = .{ .delta = delta } });
        self.kb_selection_active = false;
        self.frame_valid = false;
    }
    /// Scrolls the viewport to the oldest scrollback row and invalidates the cached snapshot.
    pub fn scrollToTop(self: *Terminal) void {
        self.api.terminal_scroll_viewport(self.handle, .{ .tag = c.GHOSTTY_SCROLL_VIEWPORT_TOP, .value = .{ .delta = 0 } });
        self.kb_selection_active = false;
        self.frame_valid = false;
    }
    /// Scrolls the viewport back to the live bottom screen and invalidates the cached snapshot.
    pub fn scrollToBottom(self: *Terminal) void {
        self.api.terminal_scroll_viewport(self.handle, .{ .tag = c.GHOSTTY_SCROLL_VIEWPORT_BOTTOM, .value = .{ .delta = 0 } });
        self.kb_selection_active = false;
        self.frame_valid = false;
    }
    /// True while the alternate screen buffer (?1049h / ?1047h / ?47h) is active.
    pub fn isAlternateScreen(self: *const Terminal) !bool {
        var screen: c.GhosttyTerminalScreen = c.GHOSTTY_TERMINAL_SCREEN_PRIMARY;
        try check(self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_ACTIVE_SCREEN, &screen));
        return switch (screen) {
            c.GHOSTTY_TERMINAL_SCREEN_PRIMARY => false,
            c.GHOSTTY_TERMINAL_SCREEN_ALTERNATE => true,
            else => error.InvalidTerminalScreen,
        };
    }
    /// Handles primary-screen viewport scroll chords (Ctrl+Home/End, Shift+PageUp/PageDown); yields on the alternate screen.
    pub fn scrollViewportShortcut(self: *Terminal, virtual_key: u32, mods: u32) !bool {
        if (try self.isAlternateScreen()) return false;
        if (mods == 2 and virtual_key == 0x24) {
            self.scrollToTop();
            return true;
        }
        if (mods == 2 and virtual_key == 0x23) {
            self.scrollToBottom();
            return true;
        }
        if (mods == 1 and (virtual_key == 0x21 or virtual_key == 0x22)) {
            var rows: u16 = 0;
            try check(self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_ROWS, &rows));
            if (rows == 0) return error.InvalidTerminalRowCount;
            const delta: i32 = @intCast(rows);
            self.scroll(if (virtual_key == 0x21) -delta else delta);
            return true;
        }
        return false;
    }
    /// Queries a DEC private mode from the emulator without caching stale host state.
    pub fn decMode(self: *const Terminal, mode: u15) !bool {
        var config: c.GhosttyTerminalModeConfig = .{ .mode = c.ghostty_mode_new(mode, false), .value = false };
        try check(self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_MODE, &config));
        return config.value;
    }
    /// True while DEC private mode 2004 (bracketed paste) is active.
    pub fn bracketedPaste(self: *const Terminal) !bool {
        return self.decMode(2004);
    }
    /// Normalizes CRLF/LF to CR, strips bracket-closing escapes, and wraps in DEC 2004 markers when active.
    pub fn encodePasteAlloc(self: *const Terminal, allocator: std.mem.Allocator, text: []const u8) ![]u8 {
        if (text.len == 0) return allocator.alloc(u8, 0);
        const bracketed = try self.bracketedPaste();
        var encoded: std.ArrayList(u8) = .empty;
        errdefer encoded.deinit(allocator);
        const wrapper_len: usize = if (bracketed) 12 else 0;
        try encoded.ensureTotalCapacity(allocator, try std.math.add(usize, text.len, wrapper_len));
        if (bracketed) encoded.appendSliceAssumeCapacity("\x1b[200~");
        var index: usize = 0;
        while (index < text.len) {
            if (std.mem.startsWith(u8, text[index..], "\x1b[201~")) {
                index += 6;
                continue;
            }
            if (text[index] == '\r') {
                encoded.appendAssumeCapacity('\r');
                index += if (index + 1 < text.len and text[index + 1] == '\n') 2 else 1;
                continue;
            }
            if (text[index] == '\n') {
                encoded.appendAssumeCapacity('\r');
                index += 1;
                continue;
            }
            encoded.appendAssumeCapacity(text[index]);
            index += 1;
        }
        if (bracketed) encoded.appendSliceAssumeCapacity("\x1b[201~");
        return encoded.toOwnedSlice(allocator);
    }
    /// Encode navigation/function keys with the terminal's currently negotiated ConPTY-compatible modes.
    pub fn encodeKey(self: *Terminal, virtual_key: u32, mods: u32, output: []u8) !?[]const u8 {
        if (mods > 7) return error.InvalidKeyModifiers;
        if (virtual_key == 0x08 and mods == 2) {
            if (output.len == 0) return error.InvalidTerminalBufferLength;
            output[0] = 0x17;
            return output[0..1];
        }
        const key = physicalTerminalKey(virtual_key) orelse return null;
        self.api.key_encoder_setopt_from_terminal(self.key_encoder, self.handle);
        // ConPTY's input parser cannot carry Kitty CSI-u or modifyOtherKeys state-2 sequences.
        const kitty_flags: c.GhosttyKittyKeyFlags = c.GHOSTTY_KITTY_KEY_DISABLED;
        const modify_other_keys = false;
        self.api.key_encoder_setopt(self.key_encoder, c.GHOSTTY_KEY_ENCODER_OPT_KITTY_FLAGS, &kitty_flags);
        self.api.key_encoder_setopt(self.key_encoder, c.GHOSTTY_KEY_ENCODER_OPT_MODIFY_OTHER_KEYS_STATE_2, &modify_other_keys);
        self.api.key_event_set_action(self.key_event, c.GHOSTTY_KEY_ACTION_PRESS);
        self.api.key_event_set_key(self.key_event, key);
        self.api.key_event_set_mods(self.key_event, @intCast(mods));
        var length: usize = 0;
        try check(self.api.key_encoder_encode(self.key_encoder, self.key_event, output.ptr, output.len, &length));
        if (length > output.len) return error.InvalidTerminalBufferLength;
        return output[0..length];
    }
    /// Clean frames reuse buffers. Dirty frames re-read every row to avoid Ghostty scroll under-reporting.
    pub fn readFrame(self: *Terminal) !*const TerminalFrame {
        try check(self.api.render_state_update(self.render_state, self.handle));
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
        while (self.api.render_state_row_iterator_next(self.row_iterator)) : (row += 1) {
            if (row >= rows) return error.InvalidTerminalRowCount;
            var row_selection: c.GhosttyRenderStateRowSelection = .{
                .size = @sizeOf(c.GhosttyRenderStateRowSelection),
                .start_x = 0,
                .end_x = 0,
            };
            const selection_result = self.api.render_state_row_get(self.row_iterator, c.GHOSTTY_RENDER_STATE_ROW_DATA_SELECTION, &row_selection);
            const has_row_selection = if (selection_result == c.GHOSTTY_NO_VALUE) false else blk: {
                try check(selection_result);
                break :blk true;
            };
            var cells: c.GhosttyRenderStateRowCells = self.row_cells;
            try check(self.api.render_state_row_get(self.row_iterator, c.GHOSTTY_RENDER_STATE_ROW_DATA_CELLS, @ptrCast(&cells)));
            var column: usize = 0;
            while (self.api.render_state_row_cells_next(self.row_cells)) : (column += 1) {
                if (column >= columns) return error.InvalidTerminalColumnCount;
                var cell = try self.readCell(foreground, background);
                cell.selected = has_row_selection and column >= row_selection.start_x and column <= row_selection.end_x;
                self.frame.cells.appendAssumeCapacity(cell);
            }
            if (column != columns) return error.InvalidTerminalColumnCount;
            const clean = false;
            try check(self.api.render_state_row_set(self.row_iterator, c.GHOSTTY_RENDER_STATE_ROW_OPTION_DIRTY, &clean));
        }
        if (row != rows or self.frame.cells.items.len != count) return error.InvalidTerminalRowCount;
        const clean: c.GhosttyRenderStateDirty = c.GHOSTTY_RENDER_STATE_DIRTY_FALSE;
        try check(self.api.render_state_set(self.render_state, c.GHOSTTY_RENDER_STATE_OPTION_DIRTY, &clean));
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
        try check(self.api.render_state_get(self.render_state, field, output));
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
        if (in_viewport) {
            try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_X, &cursor.column);
            try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_Y, &cursor.row);
            try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_WIDE_TAIL, &cursor.wide_tail);
        }
        return cursor;
    }
    fn readCell(self: *Terminal, default_fg: c.GhosttyColorRgb, default_bg: c.GhosttyColorRgb) !TerminalCell {
        var raw: c.GhosttyCell = undefined;
        try check(self.api.render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_RAW, &raw));
        var wide: c.GhosttyCellWide = undefined;
        try check(self.api.cell_get(raw, c.GHOSTTY_CELL_DATA_WIDE, &wide));
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
        const result = self.api.render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_UTF8, &text);
        const bytes: []const u8 = if (result == c.GHOSTTY_OUT_OF_SPACE) blk: {
            if (text.len > max_grapheme_bytes) return error.TerminalGraphemeLimit;
            try self.grapheme.resize(self.allocator, text.len);
            text.ptr = self.grapheme.items.ptr;
            text.cap = self.grapheme.items.len;
            try check(self.api.render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_UTF8, &text));
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
        const fg = self.api.render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_FG_COLOR, &foreground);
        if (fg == c.GHOSTTY_INVALID_VALUE) foreground = default_fg else try check(fg);
        const bg = self.api.render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR, &background);
        if (bg == c.GHOSTTY_INVALID_VALUE) background = default_bg else try check(bg);
        var style = std.mem.zeroes(c.GhosttyStyle);
        style.size = @sizeOf(c.GhosttyStyle);
        try check(self.api.render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_STYLE, &style));
        if (style.inverse) std.mem.swap(c.GhosttyColorRgb, &foreground, &background);
        if (style.faint) {
            foreground.r = @intCast((@as(u16, foreground.r) + background.r) / 2);
            foreground.g = @intCast((@as(u16, foreground.g) + background.g) / 2);
            foreground.b = @intCast((@as(u16, foreground.b) + background.b) / 2);
        }
        return .{ .foreground = packedRgb(foreground), .background = packedRgb(background), .bold = style.bold, .italic = style.italic, .underline = style.underline != 0, .strikethrough = style.strikethrough, .invisible = style.invisible };
    }
    fn applyDefaultPalette(self: *Terminal) !void {
        const behaviors: c.GhosttySelectionGestureBehaviors = .{
            .single_click = c.GHOSTTY_SELECTION_GESTURE_BEHAVIOR_CELL,
            .double_click = c.GHOSTTY_SELECTION_GESTURE_BEHAVIOR_WORD,
            .triple_click = c.GHOSTTY_SELECTION_GESTURE_BEHAVIOR_LINE,
        };
        const repeat_interval_ns: u64 = 500_000_000;
        const repeat_distance: f64 = 8.0;
        try check(self.api.selection_gesture_event_set(self.press_event, c.GHOSTTY_SELECTION_GESTURE_EVENT_OPT_BEHAVIORS, &behaviors));
        try check(self.api.selection_gesture_event_set(self.press_event, c.GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REPEAT_INTERVAL_NS, &repeat_interval_ns));
        try check(self.api.selection_gesture_event_set(self.press_event, c.GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REPEAT_DISTANCE, &repeat_distance));
        const cursor_style: c.GhosttyTerminalCursorStyle = c.GHOSTTY_TERMINAL_CURSOR_STYLE_BAR;
        const cursor_blink = true;
        // ConPTY maintains no scrollback buffer and leaves newly grown rows blank on resize.
        const pull_scrollback = false;
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_DEFAULT_CURSOR_STYLE, &cursor_style));
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_DEFAULT_CURSOR_BLINK, &cursor_blink));
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_RESIZE_PULL_SCROLLBACK, &pull_scrollback));
        const bell_callback: c.GhosttyTerminalBellFn = terminalBell;
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_BELL, @ptrCast(bell_callback)));
        const title_callback: c.GhosttyTerminalTitleChangedFn = titleChanged;
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_TITLE_CHANGED, @ptrCast(title_callback)));
        const foreground = colorRgb(0xe4e4e7);
        const background = colorRgb(0x282c34);
        const cursor = colorRgb(0xe4e4e7);
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND, &foreground));
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_COLOR_BACKGROUND, &background));
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_COLOR_CURSOR, &cursor));
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
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_COLOR_PALETTE, &palette));
    }
};

fn terminalBell(_: c.GhosttyTerminal, userdata: ?*anyopaque) callconv(.c) void {
    const context = userdata orelse return;
    const self: *Terminal = @ptrCast(@alignCast(context));
    self.program_status.onBell();
}

fn titleChanged(_: c.GhosttyTerminal, userdata: ?*anyopaque) callconv(.c) void {
    const context = userdata orelse return;
    const self: *Terminal = @ptrCast(@alignCast(context));
    if (self.callback_error != null) return;
    const raw = self.reportedTitle() catch |err| {
        self.callback_error = err;
        return;
    };
    self.title.applyReport(raw) catch {};
}

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
fn clipboardWrite(_: c.GhosttyTerminal, userdata: ?*anyopaque, write_ptr: ?*const c.GhosttyClipboardWrite) callconv(.c) c.GhosttyClipboardWriteResult {
    const context = userdata orelse return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_DENIED;
    const self: *Terminal = @ptrCast(@alignCast(context));
    if (self.callback_error != null) return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_IO_ERROR;
    const sink = self.clipboard_sink orelse return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_DENIED;
    const write = write_ptr orelse {
        self.callback_error = error.InvalidTerminalClipboardWrite;
        return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_INVALID_DATA;
    };
    if (write.size < @sizeOf(c.GhosttyClipboardWrite) or (write.contents_len != 0 and write.contents == null)) {
        self.callback_error = error.InvalidTerminalClipboardWrite;
        return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_INVALID_DATA;
    }
    if (write.location != c.GHOSTTY_CLIPBOARD_LOCATION_STANDARD and write.location != c.GHOSTTY_CLIPBOARD_LOCATION_SELECTION) {
        return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_UNSUPPORTED;
    }
    if (write.contents_len == 0) {
        sink.on_write(sink.context, &.{}) catch |err| {
            self.callback_error = err;
            return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_IO_ERROR;
        };
        return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_SUCCESS;
    }
    for (write.contents[0..write.contents_len]) |entry| {
        if ((entry.mime.len != 0 and entry.mime.ptr == null) or (entry.data.len != 0 and entry.data.ptr == null)) {
            self.callback_error = error.InvalidTerminalClipboardWrite;
            return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_INVALID_DATA;
        }
        const mime: []const u8 = if (entry.mime.len == 0) &.{} else entry.mime.ptr[0..entry.mime.len];
        if (!std.mem.eql(u8, mime, "text/plain") and !std.mem.startsWith(u8, mime, "text/plain;")) continue;
        const bytes: []const u8 = if (entry.data.len == 0) &.{} else entry.data.ptr[0..entry.data.len];
        if (bytes.len > Terminal.max_clipboard_bytes or std.mem.indexOfScalar(u8, bytes, 0) != null or !std.unicode.utf8ValidateSlice(bytes)) {
            return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_INVALID_DATA;
        }
        sink.on_write(sink.context, bytes) catch |err| {
            self.callback_error = err;
            return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_IO_ERROR;
        };
        return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_SUCCESS;
    }
    return c.GHOSTTY_CLIPBOARD_WRITE_RESULT_UNSUPPORTED;
}

fn emptySelection() c.GhosttySelection {
    var selection = std.mem.zeroes(c.GhosttySelection);
    selection.size = @sizeOf(c.GhosttySelection);
    selection.start.size = @sizeOf(c.GhosttyGridRef);
    selection.end.size = @sizeOf(c.GhosttyGridRef);
    return selection;
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
fn wordBoundaryCol(frame: *const TerminalFrame, row: u16, col: u16, forward: bool) u16 {
    var cursor = @min(col, frame.columns);
    if (!forward) {
        while (cursor > 0 and isWhitespaceOrEmptyCell(frame, row, cursor - 1)) : (cursor -= 1) {}
        while (cursor > 0 and !isWhitespaceOrEmptyCell(frame, row, cursor - 1)) : (cursor -= 1) {}
    } else {
        while (cursor < frame.columns and isWhitespaceOrEmptyCell(frame, row, cursor)) : (cursor += 1) {}
        while (cursor < frame.columns and !isWhitespaceOrEmptyCell(frame, row, cursor)) : (cursor += 1) {}
    }
    return cursor;
}
fn isWhitespaceOrEmptyCell(frame: *const TerminalFrame, row: u16, col: u16) bool {
    if (row >= frame.rows or col >= frame.columns) return true;
    const index: usize = @as(usize, row) * @as(usize, frame.columns) + @as(usize, col);
    if (index >= frame.cells.items.len) return true;
    const cell = frame.cells.items[index];
    if (cell.width == .tail) return false;
    const bytes = frame.cellText(cell);
    return std.mem.trim(u8, bytes, " \t\r\n\x0b\x0c").len == 0;
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

test "shell markers preserve the actual DLL prompt grid and legacy fallback cannot overwrite explicit status" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    const bytes = "prompt> \x1b]133;A\x07\x1b]133;C\x07\x1b]133;D;7\x07\x1b]133;A\x07";
    for (bytes) |byte| try terminal.feedOutput(&.{byte});
    try std.testing.expect(try terminal.containsViewportText("prompt> "));
    try std.testing.expectEqual(@as(u16, 8), (try terminal.readFrame()).cursor.column);
    try std.testing.expectEqual(.failed, terminal.program_status.programStatus().state);
    terminal.program_status.acknowledgeProgramStatus();
    try terminal.feedOutput("\x1b]9;4;1;30\x07");
    try std.testing.expectEqual(@as(?u8, 30), terminal.program_status.programStatus().progress);
    try terminal.feedOutput("\x1b]7501;state=blocked:kind=auth\x07\x1b]9;4;1;90\x07");
    try std.testing.expectEqual(@as(?status.ProgramStatusKind, .auth), terminal.program_status.programStatus().kind);
}

const StatusReplyLog = struct {
    bytes: std.ArrayList(u8) = .empty,
    fn write(context: *anyopaque, bytes: []const u8) !void {
        const self: *@This() = @ptrCast(@alignCast(context));
        try self.bytes.appendSlice(std.testing.allocator, bytes);
    }
};

test "OSC 7501 real DLL transport preserves text, reply order and pane isolation at every boundary" {
    const api = try binding.Api.load();
    for ([_][]const u8{ "\x07", "\x1b\\" }) |terminator| {
        const bytes = try std.mem.concat(std.testing.allocator, u8, &.{ "before\x1b]7501;state=working:progress=42", terminator, "after\x1b]7501;?", terminator, "\x1b[6n" });
        defer std.testing.allocator.free(bytes);
        for (0..bytes.len + 1) |split| {
            var terminal = try Terminal.init(std.testing.allocator, &api);
            defer terminal.deinit();
            var other = try Terminal.init(std.testing.allocator, &api);
            defer other.deinit();
            var replies: StatusReplyLog = .{};
            defer replies.bytes.deinit(std.testing.allocator);
            try terminal.attachResponseSink(.{ .context = &replies, .write = StatusReplyLog.write });
            try terminal.feedOutput(bytes[0..split]);
            try terminal.feedOutput(bytes[split..]);
            try std.testing.expectEqual(.working, terminal.program_status.programStatus().state);
            try std.testing.expectEqual(@as(?u8, 42), terminal.program_status.programStatus().progress);
            try std.testing.expectEqual(.none, other.program_status.programStatus().state);
            try std.testing.expectEqualStrings("\x1b]7501;?\x1b\\\x1b[1;12R", replies.bytes.items);
            try std.testing.expect(try terminal.containsViewportText("beforeafter"));
        }
    }
}

test "status survives alternate screen and soft reset; user acknowledgement and RIS are independent" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b]7501;state=done\x07\x1b[?1049h\x1b[?1049l\x1b[!p");
    try std.testing.expectEqual(.done, terminal.program_status.programStatus().state);
    terminal.program_status.acknowledgeProgramStatus();
    try std.testing.expectEqual(.none, terminal.program_status.programStatus().state);
    try terminal.feedOutput("\x1b]7501;state=blocked:kind=auth\x07");
    terminal.program_status.acknowledgeProgramStatus();
    try std.testing.expectEqual(.blocked, terminal.program_status.programStatus().state);
    try terminal.feedOutput("\x1bc");
    try std.testing.expectEqual(.none, terminal.program_status.programStatus().state);
}

fn checkStatusFeedAllocations(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b]7501;state=working:progress=42\x07");
    terminal.feedOutput("\x1b]7501;state=error\x07") catch |err| {
        try std.testing.expectEqual(.working, terminal.program_status.programStatus().state);
        return err;
    };
}
test "status feed allocation and capability sink errors reach the real terminal caller" {
    const api = try binding.Api.load();
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkStatusFeedAllocations, .{&api});
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var replies: TestReply = .{};
    try terminal.attachResponseSink(.{ .context = &replies, .write = TestReply.reject });
    try std.testing.expectError(error.TestPtyWriteFailure, terminal.feedOutput("\x1b]7501;?\x07"));
}

const paths = @import("../shell/directory.zig");
const TestDirectoryOwner = struct {
    directory: paths.PaneDirectory,
    pub fn onDirectoryReport(self: *@This(), report: []const u8) !void {
        try self.directory.applyReport(report, "my-pc");
    }
};

test "WSL OSC 7 directory callbacks decode Linux paths at every real-DLL chunk boundary" {
    const api = try binding.Api.load();
    const distribution = try @import("../shell/wsl_paths.zig").WslDistribution.parse("Ubuntu");
    const output = "\x1b]7;file://localhost/home/Żółć%20O%27Brien\x1b\\\x1b]7;file://remote/home/other\x07";
    for (0..output.len + 1) |boundary| {
        var owner: TestDirectoryOwner = .{ .directory = try paths.PaneDirectory.initForContext(std.testing.allocator, try paths.DirectoryPath.parse("C:\\launch"), .{ .wsl = distribution }) };
        defer owner.directory.deinit();
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        try terminal.attachDirectorySink(&owner);
        try terminal.feedOutput(output[0..boundary]);
        try terminal.feedOutput(output[boundary..]);
        try std.testing.expectEqualStrings("\\\\wsl.localhost\\Ubuntu\\home\\Żółć O'Brien", owner.directory.current().bytes);
    }
}

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

test "ConPTY-stripped DCS payloads never reach the real DLL grid while intact XTGETTCAP replies and ordinary text survive" {
    const api = try binding.Api.load();
    const stream = "+q4D73\x1b[H+q5463;524742;73657472676266;73657472676262\x1b[H$qm\x1b[K\x1bP+q5463\x1b\\visible xx+qCD12yy cost $q? maybe";
    for (0..stream.len + 1) |split| {
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        var replies: StatusReplyLog = .{};
        defer replies.bytes.deinit(std.testing.allocator);
        try terminal.attachResponseSink(.{ .context = &replies, .write = StatusReplyLog.write });
        try terminal.feedOutput(stream[0..split]);
        try terminal.feedOutput(stream[split..]);
        try std.testing.expect(!try terminal.containsViewportText("+q4D73"));
        try std.testing.expect(!try terminal.containsViewportText("+q5463"));
        try std.testing.expect(!try terminal.containsViewportText("$qm"));
        try std.testing.expect(try terminal.containsViewportText("visible xx+qCD12yy cost $q? maybe"));
        try std.testing.expect(replies.bytes.items.len > 0);
    }
}

test "growing rows on resize does not pull scrollback into the active ConPTY screen" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(5, 3, 8, 16);
    try terminal.feedOutput("1\r\n2\r\n3\r\n4\r\n5");
    try terminal.resize(5, 5, 8, 16);
    const frame = try terminal.readFrame();
    const expected = [_][]const u8{ "3", "4", "5", "", "" };
    for (expected, 0..) |want, row| {
        const first = frame.cellText(frame.cells.items[row * frame.columns]);
        try std.testing.expectEqualStrings(want, first);
    }
}

test "default cursor is bar, DECSCUSR updates and resets shape, and encodeKey stays ConPTY-compatible under Kitty/modifyOtherKeys" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var replies: StatusReplyLog = .{};
    defer replies.bytes.deinit(std.testing.allocator);
    try terminal.attachResponseSink(.{ .context = &replies, .write = StatusReplyLog.write });
    try std.testing.expectEqual(.bar, (try terminal.readFrame()).cursor.shape);
    try terminal.feedOutput("\x1b[2 q");
    try std.testing.expectEqual(.block, (try terminal.readFrame()).cursor.shape);
    try terminal.feedOutput("\x1b[4 q");
    try std.testing.expectEqual(.underline, (try terminal.readFrame()).cursor.shape);
    try terminal.feedOutput("\x1b[0 q");
    try std.testing.expectEqual(.bar, (try terminal.readFrame()).cursor.shape);
    try terminal.feedOutput("\x1b[>1u\x1b[>4;2m\x1b[?u");
    try std.testing.expectEqualStrings("\x1b[?1u", replies.bytes.items);
    var encoded: [64]u8 = undefined;
    try std.testing.expectEqualStrings("\x1b[A", (try terminal.encodeKey(0x26, 0, &encoded)).?);
    try std.testing.expectEqualStrings("\x1b[1;2A", (try terminal.encodeKey(0x26, 1, &encoded)).?);
}

fn checkPasteAllocationFailures(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b[?2004h");
    const pasted = try terminal.encodePasteAlloc(allocator, "first\r\nsecond\n\x1b[201~third");
    defer allocator.free(pasted);
    try std.testing.expectEqualStrings("\x1b[200~first\rsecond\rthird\x1b[201~", pasted);
}

test "encodePasteAlloc normalizes newlines, tracks DEC 2004 across screens/resets, and strips breakout sequences" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try std.testing.expect(!try terminal.bracketedPaste());
    const plain = try terminal.encodePasteAlloc(std.testing.allocator, "a\r\nb\nc\rd");
    defer std.testing.allocator.free(plain);
    try std.testing.expectEqualStrings("a\rb\rc\rd", plain);
    try terminal.feedOutput("\x1b[?2004h");
    try std.testing.expect(try terminal.bracketedPaste());
    const bracketed = try terminal.encodePasteAlloc(std.testing.allocator, "line1\r\nline2\n\x1b[201~safe");
    defer std.testing.allocator.free(bracketed);
    try std.testing.expectEqualStrings("\x1b[200~line1\rline2\rsafe\x1b[201~", bracketed);
    try terminal.feedOutput("\x1b[?2004l");
    try std.testing.expect(!try terminal.bracketedPaste());
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkPasteAllocationFailures, .{&api});
}

test "viewport scroll shortcuts move primary scrollback, yield on alternate screen, and encode Ctrl+Backspace" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(10, 2, 8, 16);
    try terminal.feedOutput("ROW_TOP\r\nROW_MID1\r\nROW_MID2\r\nROW_BOT1\r\nROW_BOT2");
    try std.testing.expect(try terminal.containsViewportText("ROW_BOT2"));
    try std.testing.expect(try terminal.scrollViewportShortcut(0x21, 1));
    try std.testing.expect(try terminal.containsViewportText("ROW_MID1"));
    try std.testing.expect(try terminal.scrollViewportShortcut(0x24, 2));
    try std.testing.expect(try terminal.containsViewportText("ROW_TOP"));
    try std.testing.expect(try terminal.scrollViewportShortcut(0x22, 1));
    try std.testing.expect(try terminal.containsViewportText("ROW_MID2"));
    try std.testing.expect(try terminal.scrollViewportShortcut(0x23, 2));
    try std.testing.expect(try terminal.containsViewportText("ROW_BOT2"));
    try terminal.feedOutput("\x1b[?1049h");
    try std.testing.expect(try terminal.isAlternateScreen());
    try std.testing.expect(!try terminal.scrollViewportShortcut(0x24, 2));
    try std.testing.expect(!try terminal.scrollViewportShortcut(0x21, 1));
    var encoded: [64]u8 = undefined;
    try std.testing.expectEqualStrings("\x17", (try terminal.encodeKey(0x08, 2, &encoded)).?);
    try std.testing.expectEqual(@as(?[]const u8, null), try terminal.encodeKey(0x08, 0, &encoded));
}

const ClipboardLog = struct {
    allocator: std.mem.Allocator,
    writes: std.ArrayList([]u8) = .empty,
    pub fn deinit(self: *ClipboardLog) void {
        for (self.writes.items) |entry| self.allocator.free(entry);
        self.writes.deinit(self.allocator);
    }
    pub fn onClipboardWrite(self: *ClipboardLog, bytes: []const u8) !void {
        const copy = try self.allocator.dupe(u8, bytes);
        errdefer self.allocator.free(copy);
        try self.writes.append(self.allocator, copy);
    }
};

fn checkClipboardAllocationFailures(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    var log: ClipboardLog = .{ .allocator = allocator };
    defer log.deinit();
    try terminal.attachClipboardSink(&log);
    try terminal.feedOutput("\x1b]52;c;b2s=\x07");
    try std.testing.expectEqual(@as(usize, 1), log.writes.items.len);
    try std.testing.expectEqualStrings("ok", log.writes.items[0]);
}

test "write-only OSC 52 and OSC 1337 Copy sequences decode Unicode across splits while ignoring reads, primary selection, invalid UTF-8 and oversized writes" {
    const api = try binding.Api.load();
    const copied = "Zażółć 漢 🦀 e\u{0301}\nsecond line";
    var encoded_buf: [128]u8 = undefined;
    const encoded = std.base64.standard.Encoder.encode(&encoded_buf, copied);
    for ([_][]const u8{ "\x07", "\x1b\\" }) |terminator| {
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        var log: ClipboardLog = .{ .allocator = std.testing.allocator };
        defer log.deinit();
        try terminal.attachClipboardSink(&log);
        var stream: [256]u8 = undefined;
        const payload = try std.fmt.bufPrint(&stream, "before\x1b]52;c;{s}{s}after", .{ encoded, terminator });
        for (payload) |byte| try terminal.feedOutput(&.{byte});
        try std.testing.expectEqual(@as(usize, 1), log.writes.items.len);
        try std.testing.expectEqualStrings(copied, log.writes.items[0]);
        try std.testing.expect(try terminal.containsViewportText("beforeafter"));
    }
    {
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        var log: ClipboardLog = .{ .allocator = std.testing.allocator };
        defer log.deinit();
        try terminal.attachClipboardSink(&log);
        try terminal.feedOutput("\x1b]52;c;?\x07\x1b]52;c;invalid!\x07\x1b]52;p;aGVsbG8=\x07\x1b]52;c;/w==\x07after");
        try std.testing.expectEqual(@as(usize, 0), log.writes.items.len);
        try std.testing.expect(try terminal.containsViewportText("after"));
        try terminal.feedOutput("\x1b]1337;Copy=:b2s=\x07");
        try std.testing.expectEqual(@as(usize, 1), log.writes.items.len);
        try std.testing.expectEqualStrings("ok", log.writes.items[0]);
    }
    {
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        var log: ClipboardLog = .{ .allocator = std.testing.allocator };
        defer log.deinit();
        try terminal.attachClipboardSink(&log);
        const text = try std.testing.allocator.alloc(u8, 20_000);
        defer std.testing.allocator.free(text);
        @memset(text, 'x');
        const b64_len = std.base64.standard.Encoder.calcSize(text.len);
        const packet = try std.testing.allocator.alloc(u8, 7 + b64_len + 2);
        defer std.testing.allocator.free(packet);
        @memcpy(packet[0..7], "\x1b]52;c;");
        _ = std.base64.standard.Encoder.encode(packet[7 .. 7 + b64_len], text);
        @memcpy(packet[7 + b64_len ..], "\x1b\\");
        var offset: usize = 0;
        while (offset < packet.len) : (offset += 37) {
            try terminal.feedOutput(packet[offset..@min(offset + 37, packet.len)]);
        }
        try std.testing.expectEqual(@as(usize, 1), log.writes.items.len);
        try std.testing.expectEqualStrings(text, log.writes.items[0]);
    }
    {
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        var log: ClipboardLog = .{ .allocator = std.testing.allocator };
        defer log.deinit();
        try terminal.attachClipboardSink(&log);
        const huge = try std.testing.allocator.alloc(u8, 7 + 1024 * 1024 + 13);
        defer std.testing.allocator.free(huge);
        @memcpy(huge[0..7], "\x1b]52;c;");
        @memset(huge[7 .. 7 + 1024 * 1024], 'A');
        @memcpy(huge[7 + 1024 * 1024 ..], "\x07\x1b]52;c;b2s=\x07");
        try terminal.feedOutput(huge);
        try std.testing.expectEqual(@as(usize, 1), log.writes.items.len);
        try std.testing.expectEqualStrings("ok", log.writes.items[0]);
    }
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkClipboardAllocationFailures, .{&api});
}

test "BEL control latches background attention without firing on OSC BEL terminators or changing program status" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b]7;file:///C:/project\x07\x1b]7501;state=working\x07\x1b]133;A\x07\x1b]0;Window Title\x07");
    try std.testing.expect(!terminal.program_status.takeAttention());
    try terminal.feedOutput("before\x07after");
    try std.testing.expect(terminal.program_status.takeAttention());
    try std.testing.expect(!terminal.program_status.takeAttention());
    try std.testing.expectEqual(.idle, terminal.program_status.programStatus().state);
    try std.testing.expect(try terminal.containsViewportText("beforeafter"));
    try terminal.feedOutput("\x07\x1bc");
    try std.testing.expect(!terminal.program_status.takeAttention());
}

test "OSC 0 and OSC 2 update sanitized terminal title and reset on shell prefix or RIS" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try std.testing.expectEqualStrings("", terminal.title.slice());
    try terminal.feedOutput("\x1b]0;App.xaml.cs [+] - nvim\x07");
    try std.testing.expectEqualStrings("App.xaml.cs*", terminal.title.slice());
    try terminal.feedOutput("\x1b]2;Administrator: Windows PowerShell\x1b\\");
    try std.testing.expectEqualStrings("", terminal.title.slice());
    try terminal.feedOutput("\x1b]2;claude - Fix rendering pipeline\x07");
    try std.testing.expectEqualStrings("Fix rendering pipeline", terminal.title.slice());
    try terminal.feedOutput("\x1bc");
    try std.testing.expectEqualStrings("", terminal.title.slice());
}

fn checkSelectionAllocationFailures(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    try terminal.resize(10, 4, 8, 16);
    try terminal.feedOutput("hello_world\r\nsecond");
    try terminal.setKeyboardSelection(0, 0, 5, 0);
    const selected = (try terminal.selectedTextAlloc(allocator, true)) orelse return error.MissingSelectedText;
    defer allocator.free(selected);
    try std.testing.expectEqualStrings("hello", selected);
}

test "selection gestures, keyboard selection, cell highlight flags and soft-wrap unwrapping match Ghostty" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(8, 4, 8, 16);
    try terminal.feedOutput("abcdefghij\r\nword_two rest");
    try std.testing.expect(!try terminal.hasSelection());
    try std.testing.expectEqual(@as(?[]u8, null), try terminal.selectedTextAlloc(std.testing.allocator, true));

    // Soft-wrapped line selection (row 0..1) unwraps without inserting an artificial newline.
    try terminal.setKeyboardSelection(0, 0, 2, 1);
    try std.testing.expect(try terminal.hasSelection());
    const unwrapped = (try terminal.selectedTextAlloc(std.testing.allocator, true)).?;
    defer std.testing.allocator.free(unwrapped);
    try std.testing.expectEqualStrings("abcdefghij", unwrapped);
    const frame = try terminal.readFrame();
    try std.testing.expect(frame.cells.items[0].selected);
    try std.testing.expect(frame.cells.items[7].selected);
    try std.testing.expect(frame.cells.items[8].selected);
    try std.testing.expect(frame.cells.items[9].selected);
    try std.testing.expect(!frame.cells.items[10].selected);

    // Double-click on "word_two" on row 2 selects the whole word.
    try terminal.clearSelection(true);
    try std.testing.expect(!try terminal.hasSelection());
    try std.testing.expectEqual(@as(u8, 1), try terminal.selectionPress(1, 2, 12.0, 40.0));
    try terminal.selectionRelease(1, 2);
    try std.testing.expectEqual(@as(u8, 2), try terminal.selectionPress(1, 2, 12.0, 40.0));
    try terminal.selectionRelease(1, 2);
    const word = (try terminal.selectedTextAlloc(std.testing.allocator, true)).?;
    defer std.testing.allocator.free(word);
    try std.testing.expectEqualStrings("word_two", word);

    // Triple-click on row 2 selects the entire logical line (including soft-wrapped continuation on row 3).
    try std.testing.expectEqual(@as(u8, 3), try terminal.selectionPress(1, 2, 12.0, 40.0));
    try terminal.selectionRelease(1, 2);
    const line = (try terminal.selectedTextAlloc(std.testing.allocator, true)).?;
    defer std.testing.allocator.free(line);
    try std.testing.expectEqualStrings("word_two rest", line);

    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkSelectionAllocationFailures, .{&api});
}

test "extendKeyboardSelection and consumeKeyboardSelectionBackspaces handle word navigation, wide glyphs, and cursor-end deletion" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(20, 4, 8, 16);
    try terminal.feedOutput("first\r\ncmd arg1 界x");

    // Ctrl+Shift+Left from cursor at end of "cmd arg1 界x" (col 12, row 1) selects "界x" (3 cells, 2 codepoints).
    try std.testing.expect(try terminal.extendKeyboardSelection(0x25, 3));
    {
        const text = (try terminal.selectedTextAlloc(std.testing.allocator, true)).?;
        defer std.testing.allocator.free(text);
        try std.testing.expectEqualStrings("界x", text);
    }
    try std.testing.expectEqual(@as(usize, 2), try terminal.consumeKeyboardSelectionBackspaces());
    try std.testing.expect(!try terminal.hasSelection());

    // Shift+Home selects the entire current line up to the cursor ("cmd arg1 界x" = 11 codepoints).
    try std.testing.expect(try terminal.extendKeyboardSelection(0x24, 1));
    try std.testing.expectEqual(@as(usize, 11), try terminal.consumeKeyboardSelectionBackspaces());

    // Selections crossing a hard newline cannot be deleted via backspaces.
    try terminal.setKeyboardSelection(0, 0, 12, 1);
    try std.testing.expectEqual(@as(usize, 0), try terminal.consumeKeyboardSelectionBackspaces());

    // Alternate screen yields keyboard selection chords back to the terminal application.
    try terminal.feedOutput("\x1b[?1049h");
    try std.testing.expect(!try terminal.extendKeyboardSelection(0x25, 1));
}

