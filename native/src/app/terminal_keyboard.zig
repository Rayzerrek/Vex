const Application = @import("terminal_application.zig").TerminalApplication;
const std = @import("std");
const platform = @import("../windows/windows_ffi.zig");
const workspace = @import("../workspace/workspace_model.zig");
const TerminalSession = @import("terminal_session.zig").TerminalSession;
const overlays = @import("terminal_overlays.zig");

pub const TabRename = struct {
    tab_id: workspace.TabId,
    buf: [64]u8 = undefined,
    len: u8 = 0,
    select_all: bool = true,
};

pub fn onKey(self: *Application, key: u32, modifiers: u32) bool {
    if (self.health == .failed) return true;
    if (isModifierKey(key)) return false;
    self.resetCursorOnInput() catch |err| {
        self.fail(err);
        return true;
    };
    if (self.overlay != .none) {
        return overlays.handleOverlayKey(self, key, modifiers) catch |err| {
            self.fail(err);
            return true;
        };
    }
    if (self.editing_tab) |*edit| {
        if (platform.workspaceShortcut(key, modifiers)) |action| {
            self.commitTabRename();
            if (action == .close_active) {
                self.requestCloseActive() catch |err| self.fail(err);
            } else {
                self.onAction(action, 0);
            }
            return true;
        }
        if (key == 0x0D) {
            self.commitTabRename();
            return true;
        }
        if (key == 0x1B) {
            self.cancelTabRename();
            return true;
        }
        if (key == 0x08 or key == 0x2E) {
            if (edit.select_all or (modifiers & 2) != 0) {
                edit.len = 0;
                edit.select_all = false;
            } else if (edit.len > 0) {
                var idx: u8 = edit.len - 1;
                while (idx > 0 and (edit.buf[idx] & 0xC0) == 0x80) : (idx -= 1) {}
                edit.len = idx;
            }
            platform.invalidateWindow();
            return true;
        }
        if ((modifiers & 6) == 0 and key >= 0x20 and !(key >= 0x21 and key <= 0x28) and !(key >= 0x70 and key <= 0x87)) {
            return false;
        }
        edit.select_all = false;
        platform.invalidateWindow();
        return true;
    }
    if (platform.workspaceShortcut(key, modifiers)) |action| {
        if (action == .close_active) {
            self.requestCloseActive() catch |err| self.fail(err);
        } else {
            self.onAction(action, 0);
        }
        return true;
    }
    const session = self.activeSession() orelse return false;
    return handleSessionKey(self, session, key, modifiers) catch |err| {
        self.fail(err);
        return true;
    };
}

fn handleSessionKey(self: *Application, session: *TerminalSession, key: u32, modifiers: u32) !bool {
    if (try session.terminal.scrollViewportShortcut(key, modifiers)) {
        session.scrollbar_visibility.reveal(self.cursorNowMs());
        platform.invalidateWindow();
        return true;
    }
    if ((modifiers == 3 and key == 'C') or (modifiers == 2 and key == 0x2D)) {
        if (try session.copySelectionToClipboard(self.allocator)) {
            session.recordCopyFeedback(self.cursorNowMs());
            try session.terminal.clearSelection(true);
            platform.invalidateWindow();
        }
        return true;
    }
    if (modifiers == 2 and key == 'C' and try session.terminal.hasSelection()) {
        if (try session.copySelectionToClipboard(self.allocator)) {
            session.recordCopyFeedback(self.cursorNowMs());
        }
        try session.terminal.clearSelection(true);
        platform.invalidateWindow();
        return true;
    }
    if (modifiers == 3 and key == 'X') {
        if (try session.terminal.hasSelection()) {
            if (try session.copySelectionToClipboard(self.allocator)) {
                session.recordCopyFeedback(self.cursorNowMs());
            }
            if (!try session.deleteKeyboardSelection()) try session.terminal.clearSelection(true);
            platform.invalidateWindow();
        }
        return true;
    }
    if (session.state == .exited) return true;
    if (key == 0x08 and (modifiers == 0 or modifiers == 1)) {
        if (try session.deleteKeyboardSelection()) {
            platform.invalidateWindow();
            return true;
        }
    }
    if (try session.terminal.extendKeyboardSelection(key, modifiers)) {
        platform.invalidateWindow();
        return true;
    }
    var buffer: [256]u8 = undefined;
    const encoded = try session.terminal.encodeKey(key, modifiers, &buffer);
    if (encoded) |bytes| {
        if (try session.terminal.hasSelection()) {
            try session.terminal.clearSelection(true);
            platform.invalidateWindow();
        }
        try session.writeUserInput(bytes);
        return true;
    }
    return false;
}

pub fn onText(self: *Application, bytes: []const u8) void {
    if (self.health == .failed) return;
    self.resetCursorOnInput() catch |err| {
        self.fail(err);
        return;
    };
    if (self.overlay == .command_palette) {
        if (bytes.len == 1 and (bytes[0] < 0x20 or bytes[0] == 0x7F)) return;
        var cp = &self.overlay.command_palette;
        if (cp.appendQuery(bytes)) platform.invalidateWindow();
        return;
    }
    if (self.overlay == .path_completion) {
        if (bytes.len == 1 and (bytes[0] < 0x20 or bytes[0] == 0x7F)) return;
        self.path_completion.appendQuery(bytes) catch |err| {
            self.fail(err);
            return;
        };
        self.syncPathIndex() catch |err| self.fail(err);
        platform.invalidateWindow();
        return;
    }
    if (self.overlay != .none) return;
    if (self.editing_tab) |*edit| {
        if (bytes.len == 1 and (bytes[0] < 0x20 or bytes[0] == 0x7F)) return;
        if (edit.select_all) {
            edit.len = 0;
            edit.select_all = false;
        }
        if (@as(usize, edit.len) + bytes.len <= edit.buf.len) {
            @memcpy(edit.buf[edit.len .. edit.len + bytes.len], bytes);
            edit.len += @intCast(bytes.len);
            platform.invalidateWindow();
        }
        return;
    }
    const session = self.activeSession() orelse return;
    if (session.state == .running) {
        if (session.terminal.hasSelection() catch |err| {
            self.fail(err);
            return;
        }) {
            session.terminal.clearSelection(true) catch |err| {
                self.fail(err);
                return;
            };
            platform.invalidateWindow();
        }
        session.writeUserInput(bytes) catch |err| self.fail(err);
    }
}

pub fn onPaste(self: *Application, bytes: []const u8) void {
    if (self.health == .failed) return;
    self.resetCursorOnInput() catch |err| {
        self.fail(err);
        return;
    };
    if (self.overlay == .command_palette) {
        const trimmed = std.mem.trim(u8, bytes, " \t\r\n");
        if (trimmed.len > 0) {
            var cp = &self.overlay.command_palette;
            if (cp.appendQuery(trimmed)) platform.invalidateWindow();
        }
        return;
    }
    if (self.overlay == .path_completion) {
        const trimmed = std.mem.trim(u8, bytes, " \t\r\n");
        if (trimmed.len > 0) {
            self.path_completion.appendQuery(trimmed) catch |err| {
                self.fail(err);
                return;
            };
            self.syncPathIndex() catch |err| self.fail(err);
            platform.invalidateWindow();
        }
        return;
    }
    if (self.overlay != .none) return;
    if (self.editing_tab) |*edit| {
        const trimmed = std.mem.trim(u8, bytes, " \t\r\n");
        if (trimmed.len > 0) {
            if (edit.select_all) {
                edit.len = 0;
                edit.select_all = false;
            }
            const space = edit.buf.len - edit.len;
            const n = @min(trimmed.len, space);
            @memcpy(edit.buf[edit.len .. edit.len + n], trimmed[0..n]);
            edit.len += @intCast(n);
            platform.invalidateWindow();
        }
        return;
    }
    const session = self.activeSession() orelse return;
    if (session.state == .running) {
        if (session.terminal.hasSelection() catch |err| {
            self.fail(err);
            return;
        }) {
            session.terminal.clearSelection(true) catch |err| {
                self.fail(err);
                return;
            };
            platform.invalidateWindow();
        }
        session.paste(self.allocator, bytes) catch |err| self.fail(err);
    }
}

fn isModifierKey(key: u32) bool {
    return switch (key) {
        0x10, 0x11, 0x12, 0x14, 0x5B, 0x5C, 0x90, 0xA0...0xA5 => true,
        else => false,
    };
}
