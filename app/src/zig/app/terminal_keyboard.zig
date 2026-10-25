const Application = @import("terminal_application.zig").TerminalApplication;
const std = @import("std");
const platform = @import("../windows/windows_ffi.zig");
pub const TabRename = @import("../workspace/tab_rename.zig").TabRename;
const TerminalSession = @import("terminal_session.zig").TerminalSession;
const overlays = @import("terminal_overlays.zig");

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
        if (key == 0x08) {
            if (edit.deleteBackward((modifiers & 2) != 0)) platform.invalidateWindow();
            return true;
        }
        if (key == 0x2E) {
            if (edit.deleteForward((modifiers & 2) != 0)) platform.invalidateWindow();
            return true;
        }
        if (key == 0x25) {
            if (edit.moveLeft((modifiers & 2) != 0)) platform.invalidateWindow();
            return true;
        }
        if (key == 0x27) {
            if (edit.moveRight((modifiers & 2) != 0)) platform.invalidateWindow();
            return true;
        }
        if (key == 0x24) {
            if (edit.moveHome()) platform.invalidateWindow();
            return true;
        }
        if (key == 0x23) {
            if (edit.moveEnd()) platform.invalidateWindow();
            return true;
        }
        if (modifiers == 2 and key == 'A') {
            if (edit.selectAll()) platform.invalidateWindow();
            return true;
        }
        if (modifiers == 2 and key == 'V') {
            platform.pasteClipboard();
            return true;
        }
        if (modifiers == 2 and (key == 'C' or key == 'X') and edit.select_all and edit.len > 0) {
            platform.writeClipboardText(edit.slice()) catch |err| {
                self.fail(err);
                return true;
            };
            if (key == 'X' and edit.deleteBackward(false)) platform.invalidateWindow();
            return true;
        }
        if ((modifiers & 6) == 0 and key >= 0x20 and !(key >= 0x21 and key <= 0x28) and !(key >= 0x70 and key <= 0x87)) {
            return false;
        }
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
        if (try session.copyAndClearSelection(self.allocator, self.cursorNowMs(), true)) {
            platform.invalidateWindow();
        }
        return true;
    }
    if (modifiers == 2 and key == 'C' and try session.terminal.hasSelection()) {
        _ = try session.copyAndClearSelection(self.allocator, self.cursorNowMs(), true);
        platform.invalidateWindow();
        return true;
    }
    if (modifiers == 3 and key == 'X') {
        if (try session.terminal.hasSelection()) {
            _ = try session.copySelectionToClipboard(self.allocator);
            session.copy_feedback.clear();
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
    if (self.overlay == .tab_peek) overlays.closePeek(self, true) catch |err| {
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
            self.fail(if (err == error.InvalidUtf8) error.InvalidTextUtf8 else err);
            return;
        };
        self.syncPathIndex() catch |err| self.fail(err);
        platform.invalidateWindow();
        return;
    }
    if (self.overlay != .none) return;
    if (self.editing_tab) |*edit| {
        if (bytes.len == 1 and (bytes[0] < 0x20 or bytes[0] == 0x7F)) return;
        if (edit.append(bytes)) platform.invalidateWindow();
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
    if (self.overlay == .tab_peek) overlays.closePeek(self, true) catch |err| {
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
                self.fail(if (err == error.InvalidUtf8) error.InvalidPasteUtf8 else err);
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
        if (edit.append(trimmed)) platform.invalidateWindow();
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
