const std = @import("std");
const binding = @import("api.zig");
const c = binding.c;
const check = binding.checkResult;
const status = @import("program_status.zig");
const feed = @import("terminal_feed.zig");
const title_mod = @import("terminal_title.zig");
pub const kitty = @import("kitty_graphics.zig");
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
/// Snapshot text is capped at 64 MiB; bounded offsets avoid pointer-sized fields in every cell.
/// Text borrows the owning frame's UTF-8 buffer until the next dirty snapshot.
pub const TerminalCell = struct { width: CellWidth, style: TextStyle, text_start: u32, text_length: u32, selected: bool = false };
/// Cursor position is in viewport cells when in_viewport is true, even if drawing is disabled.
pub const TerminalCursor = struct {
    column: u16 = 0,
    row: u16 = 0,
    in_viewport: bool = false,
    visible: bool = false,
    blinking: bool = false,
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
    graphics: kitty.Snapshot = .{},
    cell_width_px: u32 = 9,
    cell_height_px: u32 = 17,
    pub fn deinit(self: *TerminalFrame, allocator: std.mem.Allocator) void {
        self.cells.deinit(allocator);
        self.utf8.deinit(allocator);
        self.graphics.deinit(allocator);
        self.* = .{};
    }
    /// Bounds-checked UTF-8 text for one parsed cell, never a null-terminated C string.
    pub fn cellText(self: *const TerminalFrame, cell: TerminalCell) []const u8 {
        return self.utf8.items[cell.text_start..][0..cell.text_length];
    }
};
/// Terminal mouse tracking mode negotiated via DEC private modes 9, 1000, 1002, 1003.
pub const MouseTrackingMode = enum(c.GhosttyMouseTrackingMode) {
    none = c.GHOSTTY_MOUSE_TRACKING_NONE,
    x10 = c.GHOSTTY_MOUSE_TRACKING_X10,
    normal = c.GHOSTTY_MOUSE_TRACKING_NORMAL,
    button = c.GHOSTTY_MOUSE_TRACKING_BUTTON,
    any = c.GHOSTTY_MOUSE_TRACKING_ANY,
};
/// Terminal mouse output wire format negotiated via DEC private modes 1005, 1006, 1015, 1016.
pub const MouseFormat = enum(c.GhosttyMouseFormat) {
    x10 = c.GHOSTTY_MOUSE_FORMAT_X10,
    utf8 = c.GHOSTTY_MOUSE_FORMAT_UTF8,
    sgr = c.GHOSTTY_MOUSE_FORMAT_SGR,
    urxvt = c.GHOSTTY_MOUSE_FORMAT_URXVT,
    sgr_pixels = c.GHOSTTY_MOUSE_FORMAT_SGR_PIXELS,
};
/// Normalized pointer action for terminal mouse protocol encoding.
pub const MouseAction = enum(c.GhosttyMouseAction) {
    press = c.GHOSTTY_MOUSE_ACTION_PRESS,
    release = c.GHOSTTY_MOUSE_ACTION_RELEASE,
    motion = c.GHOSTTY_MOUSE_ACTION_MOTION,
};
/// Normalized mouse button identity for terminal mouse protocol encoding.
pub const MouseButton = enum(c.GhosttyMouseButton) {
    none = c.GHOSTTY_MOUSE_BUTTON_UNKNOWN,
    left = c.GHOSTTY_MOUSE_BUTTON_LEFT,
    right = c.GHOSTTY_MOUSE_BUTTON_RIGHT,
    middle = c.GHOSTTY_MOUSE_BUTTON_MIDDLE,
    wheel_up = c.GHOSTTY_MOUSE_BUTTON_FOUR,
    wheel_down = c.GHOSTTY_MOUSE_BUTTON_FIVE,
    wheel_left = c.GHOSTTY_MOUSE_BUTTON_SIX,
    wheel_right = c.GHOSTTY_MOUSE_BUTTON_SEVEN,
    back = c.GHOSTTY_MOUSE_BUTTON_EIGHT,
    forward = c.GHOSTTY_MOUSE_BUTTON_NINE,
};
/// Scrollback and viewport row counts for rendering and dragging the scrollbar thumb.
pub const ScrollbarInfo = struct {
    total: u64 = 0,
    offset: u64 = 0,
    len: u64 = 0,
    /// True when scrollback rows exist above the visible viewport.
    pub fn isVisible(self: ScrollbarInfo) bool {
        return self.total > self.len and self.len > 0;
    }
    /// Total scrollable row distance from top to bottom.
    pub fn scrollable(self: ScrollbarInfo) u64 {
        return self.total -| self.len;
    }
};
/// PTY replies are borrowed only during write; failures are returned by feedOutput.
pub const ResponseSink = struct { context: *anyopaque, write: *const fn (*anyopaque, []const u8) anyerror!void };

/// Owns one emulator, iterators, key/mouse encoders and frame buffers. Never copy a live owner.
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
    mouse_encoder: std.meta.Child(c.GhosttyMouseEncoder),
    mouse_event: std.meta.Child(c.GhosttyMouseEvent),
    cell_width_px: u32 = 9,
    cell_height_px: u32 = 17,
    kb_selection_active: bool = false,
    kb_anchor_col: u16 = 0,
    kb_anchor_row: u16 = 0,
    kb_focus_col: u16 = 0,
    kb_focus_row: u16 = 0,
    frame: TerminalFrame = .{},
    scroll_guard: TerminalFrame = .{},
    scroll_guard_generation: ?u64 = null,
    scroll_guard_graphics_generation: u64 = 0,
    graphics_iterator: c.GhosttyKittyGraphicsPlacementIterator = null,
    grapheme: std.ArrayList(u8) = .empty,
    search_row: std.ArrayList(u8) = .empty,
    frame_valid: bool = false,
    feed_filter: feed.TerminalFeedFilter = .{},
    program_status: status.ProgramStatusTracker,
    title: title_mod.TerminalTitle = .{},
    status_clock: std.time.Timer,
    response_sink: ?ResponseSink = null,
    color_scheme_dark: bool = true,
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
        const image_storage_limit: u64 = kitty.storage_limit;
        try check(api.terminal_set(handle, c.GHOSTTY_TERMINAL_OPT_KITTY_IMAGE_STORAGE_LIMIT, &image_storage_limit));
        const apc_limit: usize = 64 * 1024;
        try check(api.terminal_set(handle, c.GHOSTTY_TERMINAL_OPT_APC_MAX_BYTES_KITTY, &apc_limit));
        // Terminal output must not grant filesystem/shared-memory access to a sender.
        const disabled = false;
        try check(api.terminal_set(handle, c.GHOSTTY_TERMINAL_OPT_KITTY_IMAGE_MEDIUM_FILE, &disabled));
        try check(api.terminal_set(handle, c.GHOSTTY_TERMINAL_OPT_KITTY_IMAGE_MEDIUM_TEMP_FILE, null));
        try check(api.terminal_set(handle, c.GHOSTTY_TERMINAL_OPT_KITTY_IMAGE_MEDIUM_SHARED_MEM, &disabled));
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
        var m_encoder: c.GhosttyMouseEncoder = null;
        try check(api.mouse_encoder_new(null, &m_encoder));
        const mouse_encoder = m_encoder orelse return error.TerminalNullHandle;
        errdefer api.mouse_encoder_free(mouse_encoder);
        var m_event: c.GhosttyMouseEvent = null;
        try check(api.mouse_event_new(null, &m_event));
        const mouse_event = m_event orelse return error.TerminalNullHandle;
        errdefer api.mouse_event_free(mouse_event);
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
            .mouse_encoder = mouse_encoder,
            .mouse_event = mouse_event,
            .program_status = .{ .allocator = allocator },
            .status_clock = try std.time.Timer.start(),
        };
        try owner.configureDefaults();
        return owner;
    }
    /// Consumes this owner after all output callbacks have stopped; no worker owns these handles.
    pub fn deinit(self: *Terminal) void {
        self.api.mouse_event_free(self.mouse_event);
        self.api.mouse_encoder_free(self.mouse_encoder);
        self.api.key_event_free(self.key_event);
        self.api.key_encoder_free(self.key_encoder);
        self.api.selection_gesture_event_free(self.release_event);
        self.api.selection_gesture_event_free(self.drag_event);
        self.api.selection_gesture_event_free(self.press_event);
        self.api.selection_gesture_free(self.selection_gesture, self.handle);
        self.api.render_state_row_cells_free(self.row_cells);
        self.api.render_state_row_iterator_free(self.row_iterator);
        self.api.render_state_free(self.render_state);
        if (self.graphics_iterator != null) self.api.kitty_graphics_placement_iterator_free(self.graphics_iterator);
        self.api.terminal_free(self.handle);
        self.frame.deinit(self.allocator);
        self.scroll_guard.deinit(self.allocator);
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
        const scheme_callback: c.GhosttyTerminalColorSchemeFn = terminalColorScheme;
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_COLOR_SCHEME, @ptrCast(scheme_callback)));
    }
    /// Only subscribers receive unsolicited input; other programs may treat it as keystrokes.
    pub fn setColorScheme(self: *Terminal, dark: bool) !void {
        if (self.color_scheme_dark == dark) return;
        self.color_scheme_dark = dark;
        if (!try self.decMode(2031)) return;
        const sink = self.response_sink orelse return;
        var bytes: [32]u8 = undefined;
        var length: usize = 0;
        try check(self.api.color_scheme_report_encode(self.colorScheme(), &bytes, bytes.len, &length));
        if (length > bytes.len) return error.InvalidTerminalResponse;
        try sink.write(sink.context, bytes[0..length]);
    }
    fn colorScheme(self: *const Terminal) c.GhosttyColorScheme {
        return if (self.color_scheme_dark) c.GHOSTTY_COLOR_SCHEME_DARK else c.GHOSTTY_COLOR_SCHEME_LIGHT;
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
    /// RIS resets status, title and mouse motion history; screen switches/soft resets deliberately do not.
    pub fn onTerminalReset(self: *Terminal) void {
        self.program_status.resetProgramStatus();
        self.title.reset();
        self.resetMouseEncoder();
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
        self.updateMouseEncoderSize(columns, rows, width_px, height_px);
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
        const sb = try self.scrollbar();
        if (sb.offset +| sb.len < sb.total) return 0;
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
    /// Queries the active screen's scrollback total, viewport offset, and visible row count.
    pub fn scrollbar(self: *const Terminal) !ScrollbarInfo {
        var info: c.GhosttyTerminalScrollbar = .{ .total = 0, .offset = 0, .len = 0 };
        try check(self.api.terminal_get(self.handle, c.GHOSTTY_TERMINAL_DATA_SCROLLBAR, &info));
        return .{ .total = info.total, .offset = info.offset, .len = info.len };
    }
    /// Scrolls the viewport to an exact row offset from the top of scrollback.
    pub fn scrollToRow(self: *Terminal, target_offset: u64) !void {
        const info = try self.scrollbar();
        if (!info.isVisible()) return;
        const clamped = @min(target_offset, info.scrollable());
        if (clamped == info.offset) return;
        if (clamped == 0) {
            self.scrollToTop();
            return;
        }
        if (clamped >= info.scrollable()) {
            self.scrollToBottom();
            return;
        }
        const delta_i64: i64 = @as(i64, @intCast(@min(clamped, @as(u64, std.math.maxInt(i64))))) -
            @as(i64, @intCast(@min(info.offset, @as(u64, std.math.maxInt(i64)))));
        const clamped_delta: i32 = @intCast(std.math.clamp(delta_i64, std.math.minInt(i32), std.math.maxInt(i32)));
        self.scroll(clamped_delta);
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
    /// User input returns primary scrollback to the live cursor; output and mouse reports do not.
    pub fn revealInput(self: *Terminal) !bool {
        if (try self.isAlternateScreen()) return false;
        const info = try self.scrollbar();
        if (info.offset == info.scrollable()) return false;
        self.scrollToBottom();
        return true;
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
    /// True while DEC private mode 2026 (synchronized output) is active.
    pub fn synchronizedOutput(self: *const Terminal) !bool {
        return self.decMode(2026);
    }
    /// True while DEC private mode 2048 (in-band resize notifications) is active.
    pub fn inBandResize(self: *const Terminal) !bool {
        return self.decMode(2048);
    }
    /// Resolves the highest-priority active mouse tracking mode from live DEC mode bits (1003 > 1002 > 1000 > 9).
    pub fn mouseTrackingMode(self: *const Terminal) !MouseTrackingMode {
        if (try self.decMode(1003)) return .any;
        if (try self.decMode(1002)) return .button;
        if (try self.decMode(1000)) return .normal;
        if (try self.decMode(9)) return .x10;
        return .none;
    }
    /// Resolves the highest-priority active mouse wire format from live DEC mode bits (1016 > 1015 > 1005 > 1006 > X10).
    pub fn mouseFormat(self: *const Terminal) !MouseFormat {
        if (try self.decMode(1016)) return .sgr_pixels;
        if (try self.decMode(1015)) return .urxvt;
        if (try self.decMode(1005)) return .utf8;
        if (try self.decMode(1006)) return .sgr;
        return .x10;
    }
    /// True when any terminal mouse tracking mode (9, 1000, 1002, 1003) is enabled.
    pub fn isMouseTracking(self: *const Terminal) !bool {
        return (try self.mouseTrackingMode()) != .none;
    }
    /// Resets the mouse encoder's motion cell deduplication state.
    pub fn resetMouseEncoder(self: *Terminal) void {
        self.api.mouse_encoder_reset(self.mouse_encoder);
    }
    /// Encodes a pointer event into the active mouse protocol format; returns an empty slice when filtered out.
    pub fn encodeMouse(
        self: *Terminal,
        action: MouseAction,
        button: MouseButton,
        mods: u32,
        x_px: f32,
        y_px: f32,
        any_button_pressed: bool,
        output: []u8,
    ) ![]const u8 {
        if (mods > 7) return error.InvalidKeyModifiers;
        const tracking_mode: c.GhosttyMouseTrackingMode = @intFromEnum(try self.mouseTrackingMode());
        if (tracking_mode == c.GHOSTTY_MOUSE_TRACKING_NONE) return output[0..0];
        const format: c.GhosttyMouseFormat = @intFromEnum(try self.mouseFormat());
        self.api.mouse_encoder_setopt(self.mouse_encoder, c.GHOSTTY_MOUSE_ENCODER_OPT_EVENT, &tracking_mode);
        self.api.mouse_encoder_setopt(self.mouse_encoder, c.GHOSTTY_MOUSE_ENCODER_OPT_FORMAT, &format);
        self.api.mouse_encoder_setopt(self.mouse_encoder, c.GHOSTTY_MOUSE_ENCODER_OPT_ANY_BUTTON_PRESSED, &any_button_pressed);
        self.api.mouse_event_set_action(self.mouse_event, @intFromEnum(action));
        if (button == .none) {
            self.api.mouse_event_clear_button(self.mouse_event);
        } else {
            self.api.mouse_event_set_button(self.mouse_event, @intFromEnum(button));
        }
        self.api.mouse_event_set_mods(self.mouse_event, @intCast(mods));
        self.api.mouse_event_set_position(self.mouse_event, .{ .x = @max(0.0, x_px), .y = @max(0.0, y_px) });
        var length: usize = 0;
        try check(self.api.mouse_encoder_encode(self.mouse_encoder, self.mouse_event, output.ptr, output.len, &length));
        if (length > output.len) return error.InvalidTerminalBufferLength;
        return output[0..length];
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
        if (virtual_key == 0x08 and mods <= 2) {
            if (output.len == 0) return error.InvalidTerminalBufferLength;
            // Consume Backspace before WM_CHAR emits BS, which ConPTY treats as Ctrl+H.
            // DEL represents a plain Backspace for both Windows shells and Unix line editors.
            output[0] = if (mods == 2) 0x17 else 0x7f;
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
        try self.frame.graphics.update(self.allocator, self.api, self.handle, &self.graphics_iterator, dirty != c.GHOSTTY_RENDER_STATE_DIRTY_FALSE or !self.frame_valid);
        self.frame.cell_width_px = self.cell_width_px;
        self.frame.cell_height_px = self.cell_height_px;
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
        // Unstyled, non-grapheme cells have no page-local style/grapheme references.
        // Share only these across rows, and discard the cache before the next snapshot.
        var plain_cache: CellCache = @splat(null);
        var row: usize = 0;
        while (self.api.render_state_row_iterator_next(self.row_iterator)) : (row += 1) {
            if (row >= rows) return error.InvalidTerminalRowCount;
            var row_selection: c.GhosttyRenderStateRowSelection = .{
                .size = @sizeOf(c.GhosttyRenderStateRowSelection),
                .start_x = 0,
                .end_x = 0,
            };
            var cells: c.GhosttyRenderStateRowCells = self.row_cells;
            var raw_row: c.GhosttyRow = undefined;
            const fields = [_]c.GhosttyRenderStateRowData{
                c.GHOSTTY_RENDER_STATE_ROW_DATA_RAW,
                c.GHOSTTY_RENDER_STATE_ROW_DATA_CELLS,
                c.GHOSTTY_RENDER_STATE_ROW_DATA_SELECTION,
            };
            var values = [_]?*anyopaque{ &raw_row, @ptrCast(&cells), &row_selection };
            var written: usize = 0;
            // Optional selection is last: NO_VALUE must not hide an unread row or cell iterator.
            const result = self.api.render_state_row_get_multi(self.row_iterator, fields.len, &fields, &values, &written);
            const has_row_selection = if (result == c.GHOSTTY_NO_VALUE and written == 2) false else blk: {
                try check(result);
                if (written != fields.len) return error.InvalidTerminalRowData;
                break :blk true;
            };
            var styled = true;
            try check(self.api.row_get(raw_row, c.GHOSTTY_ROW_DATA_STYLED, &styled));
            // Styled cells remain page-local, even when the opaque raw values match.
            var cell_cache: CellCache = if (styled) @splat(null) else undefined;
            const cache = if (styled) &cell_cache else &plain_cache;
            var style_cache: StyleCache = if (styled) @splat(null) else undefined;
            var column: usize = 0;
            while (self.api.render_state_row_cells_next(self.row_cells)) : (column += 1) {
                if (column >= columns) return error.InvalidTerminalColumnCount;
                var cell = try self.readCachedCell(foreground, background, cache, if (styled) &style_cache else null);
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
    /// One extra row fills the exposed bottom edge of a fractional scroll. Cache it
    /// until the viewport snapshot changes, not once per animation frame.
    pub fn readScrollGuard(self: *Terminal) !?*const TerminalFrame {
        const base = try self.readFrame();
        const info = try self.scrollbar();
        if (info.offset >= info.scrollable()) return null;
        if (self.scroll_guard_generation == base.generation and self.scroll_guard_graphics_generation == base.graphics.generation) return &self.scroll_guard;
        self.scroll_guard_generation = null;
        const keyboard_selection = self.kb_selection_active;
        defer self.kb_selection_active = keyboard_selection;
        errdefer self.scrollToRow(info.offset) catch {};
        try self.scrollToRow(info.offset + 1);
        const next = try self.readFrame();
        const columns = next.columns;
        const cells = next.cells.items[(@as(usize, next.rows) - 1) * columns ..][0..columns];
        try self.scroll_guard.cells.ensureTotalCapacity(self.allocator, columns);
        self.scroll_guard.cells.clearRetainingCapacity();
        self.scroll_guard.utf8.clearRetainingCapacity();
        for (cells) |cell| {
            var copy = cell;
            copy.text_start = @intCast(self.scroll_guard.utf8.items.len);
            try self.scroll_guard.utf8.appendSlice(self.allocator, next.cellText(cell));
            self.scroll_guard.cells.appendAssumeCapacity(copy);
        }
        try self.scroll_guard.graphics.updateRow(self.allocator, self.api, self.handle, &self.graphics_iterator, @as(i32, next.rows) - 1, self.cell_height_px);
        self.scroll_guard.columns = columns;
        self.scroll_guard.rows = 1;
        self.scroll_guard.cell_width_px = next.cell_width_px;
        self.scroll_guard.cell_height_px = next.cell_height_px;
        self.scroll_guard.generation +|= 1;
        try self.scrollToRow(info.offset);
        const restored = try self.readFrame();
        self.scroll_guard_generation = restored.generation;
        self.scroll_guard_graphics_generation = restored.graphics.generation;
        return &self.scroll_guard;
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
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_BLINKING, &cursor.blinking);
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_HAS_VALUE, &in_viewport);
        try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VISUAL_STYLE, &shape);
        cursor.shape = switch (shape) {
            0 => .bar,
            1 => .block,
            2 => .underline,
            3 => .hollow,
            else => return error.InvalidTerminalCursorShape,
        };
        cursor.in_viewport = in_viewport;
        cursor.visible = cursor.visible and in_viewport;
        if (in_viewport) {
            try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_X, &cursor.column);
            try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_Y, &cursor.row);
            try self.getRender(c.GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_WIDE_TAIL, &cursor.wide_tail);
        }
        return cursor;
    }
    const cell_cache_bits = 6;
    const CellCache = [1 << cell_cache_bits]?CachedRenderCell;
    const CachedRenderCell = struct { raw: c.GhosttyCell, cell: TerminalCell };
    const CachedStyle = struct { id: u16, style: TextStyle, inverse: bool, faint: bool };
    const StyleCache = [32]?CachedStyle;

    fn readCachedCell(self: *Terminal, default_fg: c.GhosttyColorRgb, default_bg: c.GhosttyColorRgb, cache: *CellCache, style_cache: ?*StyleCache) !TerminalCell {
        var raw: c.GhosttyCell = undefined;
        try check(self.api.render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_RAW, &raw));
        // This direct-mapped cache needs only a slot, not full avalanche hashing.
        const slot = &cache[(raw *% 0x9e3779b97f4a7c15) >> (@bitSizeOf(c.GhosttyCell) - cell_cache_bits)];
        if (slot.*) |cached| {
            if (cached.raw == raw) return cached.cell;
        }
        const cell = try self.readCell(raw, default_fg, default_bg, style_cache);
        var tag: c.GhosttyCellContentTag = undefined;
        try check(self.api.cell_get(raw, c.GHOSTTY_CELL_DATA_CONTENT_TAG, &tag));
        // Grapheme extras live outside the opaque raw value and may differ for equal raw cells.
        if (tag != c.GHOSTTY_CELL_CONTENT_CODEPOINT_GRAPHEME) slot.* = .{ .raw = raw, .cell = cell };
        return cell;
    }
    fn readCell(self: *Terminal, raw: c.GhosttyCell, default_fg: c.GhosttyColorRgb, default_bg: c.GhosttyColorRgb, style_cache: ?*StyleCache) !TerminalCell {
        var wide: c.GhosttyCellWide = undefined;
        try check(self.api.cell_get(raw, c.GHOSTTY_CELL_DATA_WIDE, &wide));
        const width: CellWidth = switch (wide) {
            0 => .narrow,
            1 => .wide,
            2 => .tail,
            3 => .head,
            else => return error.InvalidTerminalCellWidth,
        };
        const style = if (style_cache) |cache|
            try self.readCachedStyle(raw, default_fg, default_bg, cache)
        else
            try self.readUnstyledCellStyle(default_fg, default_bg);
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
        return .{ .width = width, .style = style, .text_start = @intCast(start), .text_length = @intCast(bytes.len) };
    }
    fn readCachedStyle(self: *Terminal, raw: c.GhosttyCell, default_fg: c.GhosttyColorRgb, default_bg: c.GhosttyColorRgb, cache: *StyleCache) !TextStyle {
        var id: u16 = 0;
        try check(self.api.cell_get(raw, c.GHOSTTY_CELL_DATA_STYLE_ID, &id));
        const slot = &cache[id % cache.len];
        if (slot.* == null or slot.*.?.id != id) {
            var foreground = default_fg;
            const result = self.api.render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_FG_COLOR, &foreground);
            if (result == c.GHOSTTY_INVALID_VALUE) foreground = default_fg else try check(result);
            var style = std.mem.zeroes(c.GhosttyStyle);
            style.size = @sizeOf(c.GhosttyStyle);
            try check(self.api.render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_STYLE, &style));
            slot.* = .{
                .id = id,
                .style = .{ .foreground = packedRgb(foreground), .background = 0, .bold = style.bold, .italic = style.italic, .underline = style.underline != 0, .strikethrough = style.strikethrough, .invisible = style.invisible },
                .inverse = style.inverse,
                .faint = style.faint,
            };
        }
        const cached = slot.*.?;
        var foreground = colorRgb(cached.style.foreground);
        // Content backgrounds can differ for equal style IDs. Resolve them per cell,
        // and apply inverse/faint only afterwards; the cache never crosses a row.
        var background = default_bg;
        const result = self.api.render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR, &background);
        if (result == c.GHOSTTY_INVALID_VALUE) background = default_bg else try check(result);
        if (cached.inverse) std.mem.swap(c.GhosttyColorRgb, &foreground, &background);
        if (cached.faint) {
            foreground.r = @intCast((@as(u16, foreground.r) + background.r) / 2);
            foreground.g = @intCast((@as(u16, foreground.g) + background.g) / 2);
            foreground.b = @intCast((@as(u16, foreground.b) + background.b) / 2);
        }
        var style = cached.style;
        style.foreground = packedRgb(foreground);
        style.background = packedRgb(background);
        return style;
    }
    fn readUnstyledCellStyle(self: *Terminal, default_fg: c.GhosttyColorRgb, default_bg: c.GhosttyColorRgb) !TextStyle {
        // Erased cells can carry a background in their content even without a style.
        // Only foreground and decorations are implied by the public unstyled flag.
        var background = default_bg;
        const result = self.api.render_state_row_cells_get(self.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR, &background);
        if (result == c.GHOSTTY_INVALID_VALUE) background = default_bg else try check(result);
        return .{ .foreground = packedRgb(default_fg), .background = packedRgb(background) };
    }
    pub fn applyPalette(self: *Terminal, foreground_rgb: u32, background_rgb: u32, cursor_rgb: u32, ansi: *const [16]u32) !void {
        const foreground = colorRgb(foreground_rgb);
        const background = colorRgb(background_rgb);
        const cursor = colorRgb(cursor_rgb);
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND, &foreground));
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_COLOR_BACKGROUND, &background));
        try check(self.api.terminal_set(self.handle, c.GHOSTTY_TERMINAL_OPT_COLOR_CURSOR, &cursor));
        var palette: [256]c.GhosttyColorRgb = undefined;
        for (ansi.*, 0..) |color, index| palette[index] = colorRgb(color);
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
        // ConPTY and TUIs can install OSC overrides. An explicit theme selection
        // must replace those too, otherwise the startup colors mask the new defaults.
        const reset_colors = "\x1b]104\x1b\\\x1b]110\x1b\\\x1b]111\x1b\\\x1b]112\x1b\\";
        self.api.terminal_vt_write(self.handle, reset_colors.ptr, reset_colors.len);
        // Palette changes must refresh resolved colors even when the PTY is idle.
        const dirty: c.GhosttyRenderStateDirty = c.GHOSTTY_RENDER_STATE_DIRTY_FULL;
        try check(self.api.render_state_set(self.render_state, c.GHOSTTY_RENDER_STATE_OPTION_DIRTY, &dirty));
        self.frame_valid = false;
    }
    fn configureDefaults(self: *Terminal) !void {
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
        const ansi = [_]u32{ 0x282c34, 0xff5a5a, 0x5af05a, 0xf0d25a, 0x5a5aff, 0xf05af0, 0x5af0f0, 0xe4e4e7, 0x3d4354, 0xff8c8c, 0x8cff8c, 0xffe68c, 0x8c8cff, 0xff8cff, 0x8cffff, 0xffffff };
        try self.applyPalette(0xe4e4e7, 0x282c34, 0xe4e4e7, &ansi);
        const track_last_cell = true;
        self.api.mouse_encoder_setopt(self.mouse_encoder, c.GHOSTTY_MOUSE_ENCODER_OPT_TRACK_LAST_CELL, &track_last_cell);
        self.updateMouseEncoderSize(80, 24, self.cell_width_px, self.cell_height_px);
    }
    fn updateMouseEncoderSize(self: *Terminal, columns: u16, rows: u16, cell_width_px: u32, cell_height_px: u32) void {
        const cw = @max(1, cell_width_px);
        const ch = @max(1, cell_height_px);
        var size = std.mem.zeroes(c.GhosttyMouseEncoderSize);
        size.size = @sizeOf(c.GhosttyMouseEncoderSize);
        size.screen_width = @max(1, @as(u32, columns) * cw);
        size.screen_height = @max(1, @as(u32, rows) * ch);
        size.cell_width = cw;
        size.cell_height = ch;
        self.api.mouse_encoder_setopt(self.mouse_encoder, c.GHOSTTY_MOUSE_ENCODER_OPT_SIZE, &size);
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

fn terminalColorScheme(_: c.GhosttyTerminal, userdata: ?*anyopaque, out_scheme: [*c]c.GhosttyColorScheme) callconv(.c) bool {
    const context = userdata orelse return false;
    const self: *const Terminal = @ptrCast(@alignCast(context));
    if (out_scheme == null) return false;
    out_scheme.* = self.colorScheme();
    return true;
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
            if (err != error.ClipboardWriteFailed) self.callback_error = err;
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
            if (err != error.ClipboardWriteFailed) self.callback_error = err;
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

test "cell caches match full public API decoding across pages, graphemes, colors and snapshots" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(100, 70, 900, 1190);
    const Compare = struct {
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
        fn snapshot(owner: *Terminal) !void {
            const frame = try owner.readFrame();
            var foreground: c.GhosttyColorRgb = undefined;
            var background: c.GhosttyColorRgb = undefined;
            try owner.getRender(c.GHOSTTY_RENDER_STATE_DATA_COLOR_FOREGROUND, &foreground);
            try owner.getRender(c.GHOSTTY_RENDER_STATE_DATA_COLOR_BACKGROUND, &background);
            var iterator: c.GhosttyRenderStateRowIterator = owner.row_iterator;
            try owner.getRender(c.GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR, @ptrCast(&iterator));
            var index: usize = 0;
            while (owner.api.render_state_row_iterator_next(owner.row_iterator)) {
                var cells: c.GhosttyRenderStateRowCells = owner.row_cells;
                try check(owner.api.render_state_row_get(owner.row_iterator, c.GHOSTTY_RENDER_STATE_ROW_DATA_CELLS, @ptrCast(&cells)));
                while (owner.api.render_state_row_cells_next(owner.row_cells)) : (index += 1) {
                    var raw: c.GhosttyCell = undefined;
                    try check(owner.api.render_state_row_cells_get(owner.row_cells, c.GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_RAW, &raw));
                    const cached = frame.cells.items[index];
                    // Force the original full-style decoder as an independent oracle.
                    var uncached = try owner.readCell(raw, foreground, background, null);
                    uncached.style = try readStyle(owner, foreground, background);
                    try std.testing.expectEqual(cached.width, uncached.width);
                    try std.testing.expectEqualDeep(cached.style, uncached.style);
                    try std.testing.expectEqualStrings(frame.cellText(cached), frame.cellText(uncached));
                }
            }
            try std.testing.expectEqual(frame.cells.items.len, index);
        }
    };
    try Compare.snapshot(&terminal);
    try terminal.feedOutput("plain text ż界\r\n\x1b[48;2;1;2;3m\x1b[2J\x1b[0m");
    try Compare.snapshot(&terminal);
    try terminal.feedOutput("\x1bc");
    for (0..80) |_| try terminal.feedOutput("plain AAAA é ë é 界界 👩‍💻\r\n");
    try Compare.snapshot(&terminal);
    terminal.scroll(-5);
    try Compare.snapshot(&terminal);
    terminal.scrollToBottom();
    try terminal.feedOutput("\x1b]10;rgb:12/34/56\x1b\\\x1b]11;rgb:65/43/21\x1b\\");
    try Compare.snapshot(&terminal);
    try terminal.feedOutput("\x1bc");
    for (0..80) |row| {
        try terminal.feedOutput(if (row % 2 == 0)
            "\x1b[31;1mAAAA é ë é 界界 👩‍💻\x1b[0m\x1b[44m\x1b[K\x1b[0m\r\n"
        else
            "\x1b[32;3;4;7;9mAAAA ë é ë 界界 👨‍💻\x1b[0m\x1b[48;2;1;2;3m\x1b[K\x1b[0m\r\n");
    }
    try Compare.snapshot(&terminal);
    try terminal.feedOutput("\x1b[H\x1b[35mBBBB\x1b[0m\x1b]4;1;rgb:00/ff/00\x1b\\");
    try Compare.snapshot(&terminal);
    try terminal.resize(60, 30, 540, 510);
    try Compare.snapshot(&terminal);
    var sequence: [128]u8 = undefined;
    for (0..96) |index| {
        try terminal.feedOutput(try std.fmt.bufPrint(&sequence, "\x1b[38;2;{d};{d};180;48;2;1;2;{d}m{c}", .{ index, 255 - index, index, @as(u8, @intCast('A' + index % 26)) }));
    }
    try Compare.snapshot(&terminal);
    try terminal.feedOutput("\x1b[H\x1b[2;7mforeground/background\x1b[27m\x1b[K\x1b[0m");
    try Compare.snapshot(&terminal);
}

test {
    _ = @import("terminal_tests.zig");
}
