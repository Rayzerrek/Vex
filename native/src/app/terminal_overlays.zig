const std = @import("std");
const overview = @import("../workspace/tab_overview.zig");
const Application = @import("terminal_application.zig").TerminalApplication;
const platform = @import("../windows/windows_ffi.zig");
const panes = @import("../workspace/pane_tree.zig");
const workspace = @import("../workspace/workspace_model.zig");
const close_confirmation = @import("../workspace/close_confirmation.zig");
const palette = @import("../workspace/command_palette.zig");
const completion_popup = @import("../workspace/completion_popup.zig");

/// Shell picker intent is independent of the chosen shell preset.
pub const ShellPickerMode = enum(u8) { new_tab = 0, split_right = 1, split_down = 2, set_default = 3 };
/// Stable identities survive process exit and tab reordering while a close dialog is open.
pub const CloseTarget = union(enum) { pane: panes.PaneId, tab: workspace.TabId };
/// Inline terminal previews retain identities and timing, never borrowed frame pointers.
pub const OverlayState = union(enum) {
    none,
    theme_picker: struct { initial_theme_index: usize },
    command_palette: palette.CommandPalette,
    tab_peek: struct { selected_index: usize = 0, started_ms: u64 = 0, closing: bool = false, accept: bool = false, hovered_close: ?CloseTarget = null },
    path_completion: struct { pane_id: panes.PaneId, anchor: ?completion_popup.Anchor = null },
    close_confirm: struct { target: CloseTarget, confirmation: close_confirmation.CloseConfirmation, return_to_peek: ?usize = null },
    shell_picker: struct { mode: ShellPickerMode = .new_tab, selected_index: usize = 0 },
    settings,
    custom_shell,
};

const peek_duration_ms = 180;

pub fn peekContainsPane(self: *Application, id: panes.PaneId) bool {
    if (self.overlay != .tab_peek) return false;
    const model = if (self.model) |*model| model else return false;
    for (model.tabs.items) |tab| if (tab.tree.containsPane(id)) return true;
    return false;
}

pub fn peekAnimating(self: *Application) bool {
    return self.overlay == .tab_peek and platform.animationsEnabled() and self.cursorNowMs() -| self.overlay.tab_peek.started_ms < peek_duration_ms;
}

fn closePeek(self: *Application, accept: bool) void {
    if (!accept) {
        if (self.model) |model| self.overlay.tab_peek.selected_index = model.selected_tab;
    }
    self.overlay.tab_peek.closing = true;
    self.overlay.tab_peek.accept = accept;
    self.overlay.tab_peek.started_ms = self.cursorNowMs();
    self.resetCursorOnInput() catch |err| self.fail(err);
    platform.invalidateWindow();
}

pub fn finishPeek(self: *Application) !void {
    if (self.overlay != .tab_peek or !self.overlay.tab_peek.closing) return;
    if (peekAnimating(self)) return;
    const peek = self.overlay.tab_peek;
    self.overlay = .none;
    if (peek.accept) try self.applyAction(.select_tab, @intCast(peek.selected_index));
}

fn drawTabOverview(self: *Application) !void {
    const model = if (self.model) |*model| model else return;
    const metrics = self.metrics orelse return;
    const peek = &self.overlay.tab_peek;
    peek.selected_index = @min(peek.selected_index, model.tabs.items.len - 1);
    const elapsed = self.cursorNowMs() -| peek.started_ms;
    const progress: f32 = if (platform.animationsEnabled()) @min(1, @as(f32, @floatFromInt(elapsed)) / peek_duration_ms) else 1;
    const layout = overview.Layout.init(metrics.width, metrics.height, model.tabs.items.len);
    platform.c.vex_draw_tab_overview(progress < 1);
    // The selected card is last so its expansion occludes neighbours without extra layers/textures.
    for (0..layout.count) |index| {
        if (index != peek.selected_index) {
            const card = layout.card(index);
            const recessed: panes.PaneRect = .{
                .x = card.x + card.width * 0.04,
                .y = card.y + card.height * 0.04 + 6,
                .width = card.width * 0.92,
                .height = card.height * 0.92,
            };
            const card_bounds = if (peek.closing) overview.interpolate(card, recessed, progress) else overview.interpolate(recessed, card, progress);
            try drawTabCard(self, index, card_bounds, false);
        }
    }
    const full = @import("terminal_application.zig").windowContentBounds(metrics);
    const destination = layout.card(peek.selected_index);
    const bounds = if (peek.closing) overview.interpolate(destination, full, progress) else overview.interpolate(full, destination, progress);
    try drawTabCard(self, peek.selected_index, bounds, true);
}

fn drawTabCard(self: *Application, index: usize, bounds: panes.PaneRect, selected: bool) !void {
    const model = if (self.model) |*model| model else return;
    const tab = &model.tabs.items[index];
    const active = self.findSession(tab.active_pane) orelse return error.MissingPaneSession;
    platform.drawTabPreviewCard(bounds, tab.customTitle() orelse active.displayTitle(), selected);
    if (model.tabs.items.len > 1) {
        if (overview.closeButton(bounds)) |button| platform.drawPreviewClose(button, std.meta.eql(self.overlay.tab_peek.hovered_close, @as(?CloseTarget, .{ .tab = tab.id })));
    }
    const content = overview.content(bounds);
    try tab.tree.layout(self.allocator, &self.preview_placements, content);
    for (self.preview_placements.items) |placement| {
        const session = self.findSession(placement.id) orelse continue;
        if (placement.bounds.width <= 0 or placement.bounds.height <= 0) continue;
        const frame = try session.terminal.readFrame();
        try platform.drawTerminalPreview(placement.id, placement.bounds, frame, self.currentTheme());
        if (tab.tree.paneCount() > 1) {
            if (overview.closeButton(placement.bounds)) |button| platform.drawPreviewClose(button, std.meta.eql(self.overlay.tab_peek.hovered_close, @as(?CloseTarget, .{ .pane = placement.id })));
        }
    }
}

pub fn restorePeek(self: *Application, index: usize) void {
    const model = if (self.model) |*model| model else return;
    self.overlay = .{ .tab_peek = .{ .selected_index = @min(index, model.tabs.items.len - 1), .started_ms = self.cursorNowMs() -| peek_duration_ms } };
    platform.invalidateWindow();
}

fn requestPeekClose(self: *Application, target: CloseTarget) !void {
    const selected = self.overlay.tab_peek.selected_index;
    switch (target) {
        .tab => |id| {
            const model = if (self.model) |*model| model else return;
            if (model.tabIndex(id)) |index| try self.applyAction(.request_close_tab, @intCast(index));
        },
        .pane => |id| try self.requestClosePane(id),
    }
    if (self.overlay == .close_confirm) self.overlay.close_confirm.return_to_peek = selected else restorePeek(self, selected);
}

fn peekCloseHit(self: *Application, index: usize, bounds: panes.PaneRect, x: f32, y: f32) !?CloseTarget {
    const model = if (self.model) |*model| model else return null;
    const tab = &model.tabs.items[index];
    if (model.tabs.items.len > 1) {
        if (overview.closeButton(bounds)) |button| if (button.contains(x, y)) return .{ .tab = tab.id };
    }
    if (tab.tree.paneCount() > 1) {
        try tab.tree.layout(self.allocator, &self.preview_placements, overview.content(bounds));
        for (self.preview_placements.items) |placement| {
            if (overview.closeButton(placement.bounds)) |button| if (button.contains(x, y)) return .{ .pane = placement.id };
        }
    }
    return null;
}

/// Inline overlays share the terminal renderer; ordinary dialogs retain native focus controls.
pub fn drawActiveOverlay(self: *Application) !void {
    if (self.overlay == .tab_peek) return drawTabOverview(self);
    if (self.overlay != .path_completion) return;
    const layout = self.pathCompletionPopup() orelse return;
    self.path_completion.ensureVisible(layout.visible_rows);
    var items: [8]platform.c.VexPathCompletionItem = undefined;
    const total = self.path_completion.matches.items.len;
    const start = @min(self.path_completion.first_visible, total);
    const visible = @min(total - start, layout.visible_rows);
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
    platform.drawPathCompletion(layout.bounds, self.path_completion.directory, self.path_completion.query(), items[0..visible], self.path_completion.scan_error, self.path_completion.index == .deferred and self.path_completion.index.deferred.loading, self.path_completion.is_truncated);
}

/// Native dialogs own keyboard navigation; the inline path popup still routes terminal-window keys.
pub fn handleOverlayKey(self: *Application, key: u32, modifiers: u32) !bool {
    if (self.overlay == .tab_peek) {
        if (self.overlay.tab_peek.closing) return true;
        const model = if (self.model) |*model| model else return true;
        if (overview.closeShortcut(key, modifiers)) |shortcut| {
            const index = @min(self.overlay.tab_peek.selected_index, model.tabs.items.len - 1);
            const tab = &model.tabs.items[index];
            const target: CloseTarget = switch (shortcut) {
                .tab => .{ .tab = tab.id },
                .active_pane => if (tab.tree.paneCount() > 1) .{ .pane = tab.active_pane } else .{ .tab = tab.id },
            };
            try requestPeekClose(self, target);
            return true;
        }
        const metrics = self.metrics orelse return true;
        const layout = overview.Layout.init(metrics.width, metrics.height, model.tabs.items.len);
        const selected = &self.overlay.tab_peek.selected_index;
        switch (key) {
            0x1B => closePeek(self, false),
            0x0D => closePeek(self, true),
            0x25 => selected.* -|= 1,
            0x26, 0x28 => selected.* = layout.verticalNeighbor(selected.*, key == 0x28),
            0x21, 0x24 => selected.* = 0,
            0x22, 0x23 => selected.* = model.tabs.items.len - 1,
            0x27, 0x09 => {
                if (key == 0x09 and (modifiers & 1) != 0) selected.* -|= 1 else selected.* = @min(model.tabs.items.len - 1, selected.* + 1);
            },
            else => {},
        }
        platform.invalidateWindow();
        return true;
    }
    if (self.overlay != .path_completion) return true;
    const pane_id = self.overlay.path_completion.pane_id;
    const rows = if (self.pathCompletionPopup()) |layout| layout.visible_rows else 1;
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
                0x21 => -@as(isize, @intCast(rows)),
                else => @intCast(rows),
            }, rows);
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
    if (self.overlay == .tab_peek) {
        if (self.overlay.tab_peek.closing or peekAnimating(self)) return .normal;
        const model = if (self.model) |*model| model else return .normal;
        const metrics = self.metrics orelse return .normal;
        const layout = overview.Layout.init(metrics.width, metrics.height, model.tabs.items.len);
        const hit = layout.hit(x, y);
        const close_target = if (hit) |index| try peekCloseHit(self, index, layout.card(index), x, y) else null;
        if (event == .hover or event == .move or event == .cancel) {
            const hovered = if (event == .cancel) null else close_target;
            if (!std.meta.eql(self.overlay.tab_peek.hovered_close, hovered)) {
                self.overlay.tab_peek.hovered_close = hovered;
                platform.invalidateWindow();
            }
        }
        if (event == .begin) {
            if (close_target) |target| {
                try requestPeekClose(self, target);
                return .link;
            }
        }
        if (hit) |index| {
            if (event == .hover or event == .move or event == .begin) {
                if (self.overlay.tab_peek.selected_index != index) {
                    self.overlay.tab_peek.selected_index = index;
                    platform.invalidateWindow();
                }
            }
            if (event == .begin) closePeek(self, true);
        } else if (event == .begin) closePeek(self, false);
        return if (hit != null) .link else .normal;
    }
    if (self.overlay != .path_completion) return .normal;
    const layout = self.pathCompletionPopup() orelse return .normal;
    const pane_id = self.overlay.path_completion.pane_id;
    const popup = layout.bounds;
    const total = self.path_completion.matches.items.len;
    const start = @min(self.path_completion.first_visible, total);
    const visible = @min(total - start, layout.visible_rows);
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
