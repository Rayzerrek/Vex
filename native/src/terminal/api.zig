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
