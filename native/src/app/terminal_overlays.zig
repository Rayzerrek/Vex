const Application = @import("terminal_application.zig").TerminalApplication;
const platform = @import("../windows/windows_ffi.zig");
const panes = @import("../workspace/pane_tree.zig");
const workspace = @import("../workspace/workspace_model.zig");
const close_confirmation = @import("../workspace/close_confirmation.zig");
const palette = @import("../workspace/command_palette.zig");

/// Shell picker intent is independent of the chosen shell preset.
pub const ShellPickerMode = enum(u8) { new_tab = 0, split_right = 1, split_down = 2, set_default = 3 };
/// Stable identities survive process exit and tab reordering while a close dialog is open.
pub const CloseTarget = union(enum) { pane: panes.PaneId, tab: workspace.TabId };
/// Dialog state belongs to the terminal owner; only path completion is painted inside the terminal.
pub const OverlayState = union(enum) {
    none,
    theme_picker: struct { initial_theme_index: usize },
    command_palette: palette.CommandPalette,
    tab_peek: struct { selected_index: usize = 0 },
    path_completion: struct { pane_id: panes.PaneId },
    close_confirm: struct { target: CloseTarget, confirmation: close_confirmation.CloseConfirmation },
    shell_picker: struct { mode: ShellPickerMode = .new_tab, selected_index: usize = 0 },
    settings,
};

/// Paints only the cursor-anchored path popup; owned dialogs have their own real controls and focus.
pub fn drawActiveOverlay(self: *Application) !void {
    if (self.overlay != .path_completion) return;
    const popup = (try self.pathCompletionPopupBounds(self.overlay.path_completion.pane_id)) orelse return;
    var items: [8]platform.c.VexPathCompletionItem = undefined;
    const total = self.path_completion.matches.items.len;
    const start = @min(self.path_completion.first_visible, total);
    const visible = @min(total - start, items.len);
    for (0..visible) |i| {
        const match = self.path_completion.matches.items[start + i];
        const entry = self.path_completion.entries.items[match.entry_index];
        items[i] = .{
            .name = if (entry.name.len == 0) null else entry.name.ptr,
            .name_length = entry.name.len,
            .is_directory = entry.is_directory,
            .selected = start + i == self.path_completion.selected_index,
            .hovered = if (self.path_completion.hovered_index) |hover| hover == start + i else false,
        };
    }
    platform.drawPathCompletion(popup, self.path_completion.directory, self.path_completion.query(), items[0..visible], self.path_completion.scan_error, self.path_completion.index == .deferred and self.path_completion.index.deferred.loading, self.path_completion.is_truncated);
}

/// Native dialogs own keyboard navigation; the inline path popup still routes terminal-window keys.
pub fn handleOverlayKey(self: *Application, key: u32, modifiers: u32) !bool {
    if (self.overlay != .path_completion) return true;
    const pane_id = self.overlay.path_completion.pane_id;
    switch (key) {
        0x1B => {
            self.overlay = .none;
            platform.invalidateWindow();
        },
        0x0D => try self.insertSelectedPathCompletion(pane_id, (modifiers & 1) != 0),
        0x09, 0x27 => {
            if (!try self.path_completion.enterSelectedDirectory() and key == 0x09) try self.insertSelectedPathCompletion(pane_id, (modifiers & 1) != 0);
            try self.syncPathIndex();
            platform.invalidateWindow();
        },
        0x25 => {
            _ = try self.path_completion.navigateParent();
            try self.syncPathIndex();
            platform.invalidateWindow();
        },
        0x26, 0x28, 0x21, 0x22 => {
            self.path_completion.moveSelection(switch (key) {
                0x26 => -1,
                0x28 => 1,
                0x21 => -8,
                else => 8,
            }, 8);
            platform.invalidateWindow();
        },
        0x08, 0x2E => {
            if ((modifiers & 2) != 0) try self.path_completion.clearQuery() else _ = try self.path_completion.backspaceQuery();
            try self.syncPathIndex();
            platform.invalidateWindow();
        },
        else => if ((modifiers & 6) == 0 and key >= 0x20 and !(key >= 0x21 and key <= 0x28) and !(key >= 0x70 and key <= 0x87)) return false,
    }
    return true;
}

/// The popup reuses identical row hit regions for selection and cursor feedback; other dialogs own their HWND input.
pub fn handleOverlayPointer(self: *Application, event: platform.PointerEvent, x: f32, y: f32) !platform.PointerResponse {
    if (self.overlay != .path_completion) return .normal;
    const pane_id = self.overlay.path_completion.pane_id;
    const popup = (try self.pathCompletionPopupBounds(pane_id)) orelse return .normal;
    const total = self.path_completion.matches.items.len;
    const start = @min(self.path_completion.first_visible, total);
    const visible = @min(total - start, 8);
    var hit: ?usize = null;
    if (x >= popup.x + 6 and x < popup.x + popup.width - 6 and y >= popup.y + 56) {
        const row = @floor((y - popup.y - 56) / 26);
        if (row >= 0 and row < @as(f32, @floatFromInt(visible))) hit = start + @as(usize, @intFromFloat(row));
    }
    if (event == .hover or event == .move) {
        if (self.path_completion.hovered_index != hit) {
            self.path_completion.hovered_index = hit;
            if (hit) |index| self.path_completion.selected_index = index;
            platform.invalidateWindow();
        }
        if (hit != null) return .link;
        const query = x >= popup.x + 8 and x < popup.x + popup.width - 8 and y >= popup.y + 24 and y < popup.y + 50;
        return if (query) .text else .normal;
    }
    if (event == .begin) {
        if (hit) |index| {
            self.path_completion.selected_index = index;
            try self.insertSelectedPathCompletion(pane_id, false);
        } else if (!popup.contains(x, y)) {
            self.overlay = .none;
            platform.invalidateWindow();
        }
    }
    return .normal;
}
