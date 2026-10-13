const platform = @import("windows_ffi.zig");

/// Pinned libghostty C declarations; C pointers must not escape terminal/FFI modules.
pub const c = @cImport({
    @cInclude("ghostty/vt.h");
});

fn Function(comptime name: []const u8) type {
    return @TypeOf(&@field(c, name));
}

/// All exports are resolved from the DLL next to the executable, never PATH/CWD.
pub const Api = struct {
    ghostty_terminal_new: Function("ghostty_terminal_new"),
    ghostty_terminal_free: Function("ghostty_terminal_free"),
    ghostty_terminal_set: Function("ghostty_terminal_set"),
    ghostty_terminal_vt_write: Function("ghostty_terminal_vt_write"),
    ghostty_terminal_resize: Function("ghostty_terminal_resize"),
    ghostty_terminal_scroll_viewport: Function("ghostty_terminal_scroll_viewport"),
    ghostty_render_state_new: Function("ghostty_render_state_new"),
    ghostty_render_state_free: Function("ghostty_render_state_free"),
    ghostty_render_state_update: Function("ghostty_render_state_update"),
    ghostty_render_state_get: Function("ghostty_render_state_get"),
    ghostty_render_state_set: Function("ghostty_render_state_set"),
    ghostty_render_state_row_iterator_new: Function("ghostty_render_state_row_iterator_new"),
    ghostty_render_state_row_iterator_free: Function("ghostty_render_state_row_iterator_free"),
    ghostty_render_state_row_iterator_next: Function("ghostty_render_state_row_iterator_next"),
    ghostty_render_state_row_get: Function("ghostty_render_state_row_get"),
    ghostty_render_state_row_set: Function("ghostty_render_state_row_set"),
    ghostty_render_state_row_cells_new: Function("ghostty_render_state_row_cells_new"),
    ghostty_render_state_row_cells_free: Function("ghostty_render_state_row_cells_free"),
    ghostty_render_state_row_cells_next: Function("ghostty_render_state_row_cells_next"),
    ghostty_render_state_row_cells_get: Function("ghostty_render_state_row_cells_get"),
    ghostty_cell_get: Function("ghostty_cell_get"),
    ghostty_key_encoder_new: Function("ghostty_key_encoder_new"),
    ghostty_key_encoder_free: Function("ghostty_key_encoder_free"),
    ghostty_key_encoder_setopt_from_terminal: Function("ghostty_key_encoder_setopt_from_terminal"),
    ghostty_key_encoder_encode: Function("ghostty_key_encoder_encode"),
    ghostty_key_event_new: Function("ghostty_key_event_new"),
    ghostty_key_event_free: Function("ghostty_key_event_free"),
    ghostty_key_event_set_action: Function("ghostty_key_event_set_action"),
    ghostty_key_event_set_key: Function("ghostty_key_event_set_key"),
    ghostty_key_event_set_mods: Function("ghostty_key_event_set_mods"),

    /// Returns an initialized table or an error; no partially populated table is exposed.
    pub fn load() !Api {
        var loaded: Api = undefined;
        inline for (@typeInfo(Api).@"struct".fields) |field| {
            const pointer = platform.c.vex_terminal_symbol(field.name) orelse return error.MissingTerminalSymbol;
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
