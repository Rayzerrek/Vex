/// Pinned libghostty C declarations; C pointers must not escape terminal/FFI modules.
pub const c = @cImport({
    @cInclude("ghostty/vt.h");
    @cInclude("windows_platform.h");
});

fn Function(comptime name: []const u8) type {
    return @TypeOf(&@field(c, "ghostty_" ++ name));
}

/// All exports are resolved from the DLL next to the executable, never PATH/CWD.
pub const Api = struct {
    terminal_new: Function("terminal_new"),
    terminal_free: Function("terminal_free"),
    terminal_set: Function("terminal_set"),
    terminal_get: Function("terminal_get"),
    terminal_vt_write: Function("terminal_vt_write"),
    terminal_resize: Function("terminal_resize"),
    terminal_scroll_viewport: Function("terminal_scroll_viewport"),
    terminal_grid_ref: Function("terminal_grid_ref"),
    terminal_selection_format_buf: Function("terminal_selection_format_buf"),
    selection_gesture_new: Function("selection_gesture_new"),
    selection_gesture_free: Function("selection_gesture_free"),
    selection_gesture_reset: Function("selection_gesture_reset"),
    selection_gesture_get: Function("selection_gesture_get"),
    selection_gesture_event: Function("selection_gesture_event"),
    selection_gesture_event_new: Function("selection_gesture_event_new"),
    selection_gesture_event_free: Function("selection_gesture_event_free"),
    selection_gesture_event_set: Function("selection_gesture_event_set"),
    render_state_new: Function("render_state_new"),
    render_state_free: Function("render_state_free"),
    render_state_update: Function("render_state_update"),
    render_state_get: Function("render_state_get"),
    render_state_set: Function("render_state_set"),
    render_state_row_iterator_new: Function("render_state_row_iterator_new"),
    render_state_row_iterator_free: Function("render_state_row_iterator_free"),
    render_state_row_iterator_next: Function("render_state_row_iterator_next"),
    render_state_row_get: Function("render_state_row_get"),
    render_state_row_set: Function("render_state_row_set"),
    render_state_row_cells_new: Function("render_state_row_cells_new"),
    render_state_row_cells_free: Function("render_state_row_cells_free"),
    render_state_row_cells_next: Function("render_state_row_cells_next"),
    render_state_row_cells_get: Function("render_state_row_cells_get"),
    cell_get: Function("cell_get"),
    key_encoder_new: Function("key_encoder_new"),
    key_encoder_free: Function("key_encoder_free"),
    key_encoder_setopt: Function("key_encoder_setopt"),
    key_encoder_setopt_from_terminal: Function("key_encoder_setopt_from_terminal"),
    key_encoder_encode: Function("key_encoder_encode"),
    key_event_new: Function("key_event_new"),
    key_event_free: Function("key_event_free"),
    key_event_set_action: Function("key_event_set_action"),
    key_event_set_key: Function("key_event_set_key"),
    key_event_set_mods: Function("key_event_set_mods"),
    mouse_encoder_new: Function("mouse_encoder_new"),
    mouse_encoder_free: Function("mouse_encoder_free"),
    mouse_encoder_setopt: Function("mouse_encoder_setopt"),
    mouse_encoder_reset: Function("mouse_encoder_reset"),
    mouse_encoder_encode: Function("mouse_encoder_encode"),
    mouse_event_new: Function("mouse_event_new"),
    mouse_event_free: Function("mouse_event_free"),
    mouse_event_set_action: Function("mouse_event_set_action"),
    mouse_event_set_button: Function("mouse_event_set_button"),
    mouse_event_clear_button: Function("mouse_event_clear_button"),
    mouse_event_set_mods: Function("mouse_event_set_mods"),
    mouse_event_set_position: Function("mouse_event_set_position"),

    /// Returns an initialized table or an error; no partially populated table is exposed.
    pub fn load() !Api {
        var loaded: Api = undefined;
        inline for (@typeInfo(Api).@"struct".fields) |field| {
            const pointer = c.vex_terminal_symbol("ghostty_" ++ field.name) orelse return error.MissingTerminalSymbol;
            // GetProcAddress has no typed result; pinned headers plus ABI tests are the contract.
            @field(loaded, field.name) = @ptrCast(pointer);
        }
        return loaded;
    }
};

/// Decode all known Ghostty results at the IO seam, preserving failure categories.
pub fn checkResult(result: c.GhosttyResult) !void {
    return switch (result) {
        c.GHOSTTY_SUCCESS => {},
        c.GHOSTTY_OUT_OF_MEMORY => error.TerminalOutOfMemory,
        c.GHOSTTY_INVALID_VALUE => error.TerminalInvalidValue,
        c.GHOSTTY_OUT_OF_SPACE => error.TerminalOutOfSpace,
        c.GHOSTTY_NO_VALUE => error.TerminalNoValue,
        c.GHOSTTY_IO_ERROR => error.TerminalIoError,
        c.GHOSTTY_LIMIT_EXCEEDED => error.TerminalLimitExceeded,
        else => error.TerminalUnknownResult,
    };
}
