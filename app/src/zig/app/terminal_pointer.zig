const application = @import("terminal_application.zig");
const Application = application.TerminalApplication;
const std = @import("std");
const platform = @import("../windows/windows_ffi.zig");
const panes = @import("../workspace/pane_tree.zig");
const terminal = @import("../terminal/terminal.zig");
const TerminalSession = @import("terminal_session.zig").TerminalSession;
const DividerDrag = @import("../workspace/divider_drag.zig").DividerDrag;
const TabDrag = @import("../workspace/tab_drag.zig").TabDrag;
const tab_strip = @import("../workspace/tab_strip.zig");
const url_detector = @import("../terminal/url_detector.zig");
const overlays = @import("terminal_overlays.zig");

pub const HoveredLink = struct {
    pane_id: panes.PaneId,
    row: u16,
    start_col: u16,
    end_col: u16,
};

const SelectionDrag = struct {
    pane_id: panes.PaneId,
    click_count: u8,
    dragged: bool = false,
    start_column: u16,
    start_row: u16,
    start_x_px: f64,
    start_y_px: f64,
};

const MouseDrag = struct {
    pane_id: panes.PaneId,
    button: terminal.MouseButton,
};

const ScrollbarDrag = struct {
    pane_id: panes.PaneId,
    grab_offset: f32,
    pointer_y: f32,
};

const PaneDrag = struct {
    pane_id: panes.PaneId,
    start_x: f32,
    start_y: f32,
    dragging: bool = false,
    zone: panes.DropZone = .none,
};

pub const PointerDrag = union(enum) { none, divider: DividerDrag, tab: TabDrag, pane: PaneDrag, selection: SelectionDrag, mouse: MouseDrag, scrollbar: ScrollbarDrag };

pub fn onScroll(self: *Application, delta: i32, x: f32, y: f32, modifiers: u32) void {
    if (self.health == .failed or delta == 0) return;
    if ((modifiers & 2) != 0) {
        const steps = self.zoom_wheel.take(delta, 1);
        if (steps != 0) self.adjustFontZoom(-@as(f32, @floatFromInt(steps))) catch |err| self.fail(err);
        return;
    }
    if (self.overlay == .path_completion) {
        const rows = if (self.pathCompletionPopup()) |layout| layout.visible_rows else 1;
        const steps = self.completion_wheel.take(delta, 1);
        if (steps == 0) return;
        self.path_completion.moveSelection(steps, rows);
        platform.invalidateWindow();
        return;
    }
    if (self.overlay != .none) return;
    handleScroll(self, delta, x, y, modifiers) catch |err| self.fail(err);
}

fn handleScroll(self: *Application, delta: i32, x: f32, y: f32, modifiers: u32) !void {
    const metrics = self.metrics orelse return;
    for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
        const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
        if ((modifiers & 1) == 0 and try session.terminal.isMouseTracking()) {
            if (try metrics.viewportPoint(placement.bounds, x, y, true)) |vp| {
                const ticks = session.wheel_scroll.take(delta, 1);
                const wheel_button: terminal.MouseButton = if (ticks < 0) .wheel_up else .wheel_down;
                const steps = @abs(ticks);
                const any_pressed = (self.pointer_drag == .mouse and self.pointer_drag.mouse.pane_id == placement.id) or
                    (self.reported_mouse_pane == placement.id and self.reported_mouse_buttons != 0);
                for (0..steps) |_| {
                    try reportMouseEvent(self, session, .press, wheel_button, modifiers, vp, any_pressed);
                    try reportMouseEvent(self, session, .release, wheel_button, modifiers, vp, any_pressed);
                }
                return;
            }
        }
        const units = platform.wheelScrollLines() orelse if (session.size) |size| size.rows else 24;
        const rows = session.wheel_scroll.take(delta, units);
        if (rows == 0) return;
        session.terminal.scroll(rows);
        session.scrollbar_visibility.reveal(self.cursorNowMs());
        platform.invalidateWindow();
        return;
    };
    const session = self.activeSession() orelse return;
    const units = platform.wheelScrollLines() orelse if (session.size) |size| size.rows else 24;
    const rows = session.wheel_scroll.take(delta, units);
    if (rows == 0) return;
    session.terminal.scroll(rows);
    session.scrollbar_visibility.reveal(self.cursorNowMs());
    platform.invalidateWindow();
}

pub fn onMouseButton(self: *Application, button: terminal.MouseButton, pressed: bool, x: f32, y: f32, modifiers: u32) void {
    if (self.health == .failed or self.overlay != .none) return;
    handleMouseButton(self, button, pressed, x, y, modifiers) catch |err| self.fail(err);
}

fn handleMouseButton(self: *Application, button: terminal.MouseButton, pressed: bool, x: f32, y: f32, modifiers: u32) !void {
    const model = if (self.model) |*model| model else return;
    const metrics = self.metrics orelse return;
    const can_focus = model.activeTab().tree.paneCount() > 1;
    const bit: u8 = if (button == .middle) 2 else if (button == .right) 4 else 1;
    if (pressed) {
        self.commitTabRename();
        for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
            if (placement.bounds.hitHeaderAction(can_focus, x, y) != null) return;
            const vp = (try metrics.viewportPoint(placement.bounds, x, y, false)) orelse return;
            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
            _ = model.focusPane(placement.id);
            try self.clearOtherSelections(placement.id);
            platform.invalidateWindow();
            if ((modifiers & 1) == 0 and try session.terminal.isMouseTracking()) {
                self.reported_mouse_pane = placement.id;
                self.reported_mouse_buttons |= bit;
                try reportMouseEvent(self, session, .press, button, modifiers, vp, true);
            } else if (button == .right) {
                platform.pasteClipboard();
            }
            return;
        };
    } else {
        const pane_id = self.reported_mouse_pane orelse return;
        if ((self.reported_mouse_buttons & bit) == 0) return;
        self.reported_mouse_buttons &= ~bit;
        const any_remaining = self.reported_mouse_buttons != 0;
        if (!any_remaining) self.reported_mouse_pane = null;
        const placement = self.findPlacement(pane_id) orelse return;
        const session = self.findSession(pane_id) orelse return;
        if (try session.terminal.isMouseTracking()) {
            if (try metrics.viewportPoint(placement.bounds, x, y, true)) |vp| {
                try reportMouseEvent(self, session, .release, button, modifiers, vp, any_remaining);
            }
        }
    }
}

pub fn onClick(self: *Application, x: f32, y: f32) void {
    if (self.health == .failed) return;
    handleClick(self, x, y) catch |err| self.fail(err);
}

fn handleClick(self: *Application, x: f32, y: f32) !void {
    if (self.overlay != .none) return;
    const model = if (self.model) |*model| model else return;
    const can_focus = model.activeTab().tree.paneCount() > 1;
    for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
        if (placement.bounds.hitHeaderAction(can_focus, x, y)) |header_action| {
            switch (header_action) {
                .focus_toggle => {
                    _ = model.focusPane(placement.id);
                    try self.clearOtherSelections(placement.id);
                    if (model.toggleFocusMode()) try self.updateLayout() else platform.invalidateWindow();
                },
                .split_right, .split_down => {
                    const target_session = self.findSession(placement.id) orelse return error.MissingPaneSession;
                    const session = try self.addSession(target_session.directory.current());
                    errdefer self.removeSession(session.id);
                    try model.splitPane(placement.id, session.id, if (header_action == .split_right) .right else .down);
                    try self.updateLayout();
                },
                .close => try self.requestClosePane(placement.id),
            }
            return;
        }
        _ = model.focusPane(placement.id);
        try self.clearOtherSelections(placement.id);
        platform.invalidateWindow();
        return;
    };
}

fn updateHoverState(self: *Application, can_focus: bool, x: f32, y: f32, modifiers: u32) !void {
    var next_pane: ?panes.PaneId = null;
    var next_action: ?panes.PaneHeaderAction = null;
    var next_scrollbar: ?panes.PaneId = null;
    var next_link: ?HoveredLink = null;
    if (self.pointer_drag == .none) {
        for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
            next_pane = placement.id;
            next_action = placement.bounds.hitHeaderAction(can_focus, x, y);
            if (next_action == null) {
                if (self.findSession(placement.id)) |session| {
                    if ((modifiers & 1) != 0 or !try session.terminal.isMouseTracking()) {
                        if (platform.ScrollbarGeometry.compute(placement.bounds, try session.terminal.scrollbar())) |sb| {
                            if (sb.containsPoint(placement.bounds, x, y)) next_scrollbar = placement.id;
                        }
                    }
                    if (next_scrollbar == null) {
                        if (self.metrics) |metrics| {
                            if (try metrics.viewportPoint(placement.bounds, x, y, false)) |vp| {
                                const frame = try session.terminal.readFrame();
                                if (url_detector.findLinkInFrameRow(frame, vp.row, vp.column, null)) |match| {
                                    next_link = .{
                                        .pane_id = placement.id,
                                        .row = vp.row,
                                        .start_col = match.start_col,
                                        .end_col = match.end_col,
                                    };
                                }
                            }
                        }
                    }
                }
            }
            break;
        };
    }
    const link_changed = !std.meta.eql(self.hovered_link, next_link);
    if (self.hovered_pane != next_pane or self.hovered_header_action != next_action or self.hovered_scrollbar != next_scrollbar or link_changed) {
        self.hovered_pane = next_pane;
        self.hovered_header_action = next_action;
        self.hovered_scrollbar = next_scrollbar;
        self.hovered_link = next_link;
        platform.invalidateWindow();
    }
}

pub fn cancelPointerDrag(self: *Application) !bool {
    const model = if (self.model) |*model| model else return false;
    const clear_preview = self.pointer_drag == .tab or self.pointer_drag == .pane or self.pointer_drag == .selection or self.pointer_drag == .scrollbar;
    const changed = switch (self.pointer_drag) {
        .none, .tab, .pane, .mouse, .scrollbar => false,
        .selection => |sel| blk: {
            if (self.findSession(sel.pane_id)) |session| {
                try session.terminal.selectionRelease(sel.start_column, sel.start_row);
                if (!sel.dragged and sel.click_count <= 1) {
                    try session.terminal.clearSelection(false);
                }
            }
            break :blk false;
        },
        .divider => |*drag| try drag.cancel(&model.activeTab().tree),
    };
    self.pointer_drag = .none;
    self.reported_mouse_pane = null;
    self.reported_mouse_buttons = 0;
    try platform.cancelPointerCapture();
    if (clear_preview) platform.invalidateWindow();
    return changed;
}

pub fn onPointer(self: *Application, event: platform.PointerEvent, x: f32, y: f32, modifiers: u32) platform.PointerResponse {
    if (self.health == .failed) return .normal;
    if (event == .begin) self.resetCursorOnInput() catch |err| {
        self.fail(err);
        return .normal;
    };
    return handlePointer(self, event, x, y, modifiers) catch |err| {
        self.fail(err);
        return .normal;
    };
}

fn handlePointer(self: *Application, event: platform.PointerEvent, x: f32, y: f32, modifiers: u32) !platform.PointerResponse {
    if (self.overlay != .none) {
        return try overlays.handleOverlayPointer(self, event, x, y);
    }
    const model = if (self.model) |*model| model else return .normal;
    const metrics = self.metrics orelse return .normal;
    const layout = try tab_strip.TabStripLayout.init(metrics.width, model.tabs.items.len);
    const point: tab_strip.TabPointer = .{ .x = x, .y = y };
    const bounds = application.windowContentBounds(metrics);
    const can_focus = model.activeTab().tree.paneCount() > 1;
    switch (event) {
        .hover => {
            try updateHoverState(self, can_focus, x, y, modifiers);
            if (self.pointer_drag == .none and (modifiers & 1) == 0) {
                for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
                    const session = self.findSession(placement.id) orelse break;
                    if (try session.terminal.isMouseTracking()) {
                        if (try metrics.viewportPoint(placement.bounds, x, y, false)) |vp| {
                            const any_pressed = self.reported_mouse_pane == placement.id and self.reported_mouse_buttons != 0;
                            const button: terminal.MouseButton = if ((self.reported_mouse_buttons & 2) != 0) .middle else if ((self.reported_mouse_buttons & 4) != 0) .right else .none;
                            try reportMouseEvent(self, session, .motion, button, modifiers, vp, any_pressed);
                        }
                    }
                    break;
                };
            }
        },
        .begin => {
            if (try layout.hitTab(point)) |hit| {
                if (hit.part == .close) {
                    self.commitTabRename();
                    return .normal;
                }
                if (self.editing_tab) |*edit| {
                    if (edit.tab_id == model.tabs.items[hit.index].id) {
                        if (edit.moveEnd()) platform.invalidateWindow();
                        return .normal;
                    }
                    self.commitTabRename();
                }
                try self.applyAction(.select_tab, @intCast(hit.index));
                var drag: TabDrag = .none;
                if (try drag.begin(model, layout, model.activeTab().id, point)) self.pointer_drag = .{ .tab = drag };
            } else {
                self.commitTabRename();
                if (!model.activeTab().is_focus_mode) {
                    var drag: DividerDrag = .{};
                    if (try drag.begin(&model.activeTab().tree, bounds, x, y)) self.pointer_drag = .{ .divider = drag };
                }
                if (self.pointer_drag == .none) {
                    for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
                        if (placement.bounds.hitHeaderAction(can_focus, x, y) != null) break;
                        if (placement.bounds.isHeaderDragArea(can_focus, x, y)) {
                            _ = model.focusPane(placement.id);
                            try self.clearOtherSelections(placement.id);
                            self.pointer_drag = .{ .pane = .{
                                .pane_id = placement.id,
                                .start_x = x,
                                .start_y = y,
                            } };
                            platform.invalidateWindow();
                            break;
                        }
                        const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
                        if ((modifiers & 1) != 0 or !try session.terminal.isMouseTracking()) {
                            if (platform.ScrollbarGeometry.compute(placement.bounds, try session.terminal.scrollbar())) |sb| {
                                if (sb.containsPoint(placement.bounds, x, y)) {
                                    session.scrollbar_visibility.reveal(self.cursorNowMs());
                                    _ = model.focusPane(placement.id);
                                    try self.clearOtherSelections(placement.id);
                                    if (sb.containsThumb(y)) {
                                        self.pointer_drag = .{ .scrollbar = .{
                                            .pane_id = placement.id,
                                            .grab_offset = y - sb.thumb_y,
                                            .pointer_y = y,
                                        } };
                                    } else {
                                        const page_rows: i32 = @intCast(if (session.size) |sz| sz.rows else 24);
                                        const delta: i32 = if (y < sb.thumb_y) -page_rows else page_rows;
                                        session.terminal.scroll(delta);
                                    }
                                    platform.invalidateWindow();
                                    break;
                                }
                            }
                        }
                        if (try metrics.viewportPoint(placement.bounds, x, y, false)) |vp| {
                            if ((modifiers & 2) != 0) {
                                const frame = try session.terminal.readFrame();
                                var url_buf: [2048]u8 = undefined;
                                if (url_detector.findLinkInFrameRow(frame, vp.row, vp.column, &url_buf)) |match| {
                                    if (match.url_len > 0) {
                                        _ = model.focusPane(placement.id);
                                        try self.clearOtherSelections(placement.id);
                                        platform.openUrl(url_buf[0..match.url_len]) catch {};
                                        platform.invalidateWindow();
                                        break;
                                    }
                                }
                            }
                            _ = model.focusPane(placement.id);
                            try self.clearOtherSelections(placement.id);
                            if ((modifiers & 1) == 0 and try session.terminal.isMouseTracking()) {
                                if (try session.terminal.hasSelection()) {
                                    try session.terminal.clearSelection(true);
                                }
                                try reportMouseEvent(self, session, .press, .left, modifiers, vp, true);
                                self.pointer_drag = .{ .mouse = .{ .pane_id = placement.id, .button = .left } };
                            } else {
                                session.copy_feedback.clear();
                                const click_count = try session.terminal.selectionPress(vp.column, vp.row, vp.x_px, vp.y_px);
                                self.pointer_drag = .{ .selection = .{
                                    .pane_id = placement.id,
                                    .click_count = click_count,
                                    .start_column = vp.column,
                                    .start_row = vp.row,
                                    .start_x_px = vp.x_px,
                                    .start_y_px = vp.y_px,
                                } };
                            }
                            platform.invalidateWindow();
                        }
                        break;
                    };
                }
            }
        },
        .move, .end => {
            switch (self.pointer_drag) {
                .none => try updateHoverState(self, can_focus, x, y, modifiers),
                .divider => |*drag| if (try drag.move(&model.activeTab().tree, bounds, x, y)) try self.updateLayout(),
                .tab => |*drag| {
                    if (event == .end) {
                        _ = try drag.finish(model, layout, point);
                        platform.invalidateWindow();
                    } else if (try drag.move(model, layout, point)) platform.invalidateWindow();
                },
                .pane => |*pdrag| {
                    if (!pdrag.dragging and (@abs(x - pdrag.start_x) + @abs(y - pdrag.start_y)) >= 8.0) {
                        pdrag.dragging = true;
                    }
                    if (pdrag.dragging) {
                        const next_zone: panes.DropZone = if (self.findPlacement(pdrag.pane_id)) |pl| pl.bounds.dropZoneAt(x, y) else .none;
                        if (event == .end) {
                            const zone = next_zone;
                            pdrag.zone = .none;
                            if (zone != .none) {
                                const target_session = self.findSession(pdrag.pane_id) orelse return error.MissingPaneSession;
                                const session = try self.addSession(target_session.directory.current());
                                errdefer self.removeSession(session.id);
                                const axis: panes.SplitAxis = if (zone == .left or zone == .right) .right else .down;
                                try model.splitPane(pdrag.pane_id, session.id, axis);
                                try self.updateLayout();
                            } else {
                                platform.invalidateWindow();
                            }
                        } else if (pdrag.zone != next_zone) {
                            pdrag.zone = next_zone;
                            platform.invalidateWindow();
                        }
                    }
                },
                .selection => |*sel| {
                    if (self.findPlacement(sel.pane_id)) |placement| {
                        if (self.findSession(sel.pane_id)) |session| {
                            if (try metrics.viewportPoint(placement.bounds, x, y, true)) |vp| {
                                if (event == .end) {
                                    try session.terminal.selectionRelease(vp.column, vp.row);
                                    if (!sel.dragged and sel.click_count <= 1) {
                                        // A single click without drag clears the 1-cell anchor while keeping multi-click history.
                                        try session.terminal.clearSelection(false);
                                    } else if (self.settings.copy_on_select) {
                                        _ = try session.copyAndClearSelection(self.allocator, self.cursorNowMs(), false);
                                    }
                                    platform.invalidateWindow();
                                } else {
                                    const moved_cell = vp.column != sel.start_column or vp.row != sel.start_row;
                                    const moved_px = @abs(vp.x_px - sel.start_x_px) >= 3.0 or @abs(vp.y_px - sel.start_y_px) >= 3.0;
                                    if (sel.dragged or moved_cell or moved_px or sel.click_count > 1) {
                                        if (moved_cell or moved_px) sel.dragged = true;
                                        try session.terminal.selectionDrag(vp.column, vp.row, vp.x_px, vp.y_px);
                                        platform.invalidateWindow();
                                    }
                                }
                            }
                        }
                    }
                },
                .mouse => |mouse_drag| {
                    if (self.findPlacement(mouse_drag.pane_id)) |placement| {
                        if (self.findSession(mouse_drag.pane_id)) |session| {
                            if (try metrics.viewportPoint(placement.bounds, x, y, true)) |vp| {
                                if (event == .end) {
                                    try reportMouseEvent(self, session, .release, mouse_drag.button, modifiers, vp, false);
                                } else {
                                    try reportMouseEvent(self, session, .motion, mouse_drag.button, modifiers, vp, true);
                                }
                            }
                        }
                    }
                },
                .scrollbar => |*sb_drag| {
                    sb_drag.pointer_y = y;
                    if (self.findPlacement(sb_drag.pane_id)) |placement| {
                        if (self.findSession(sb_drag.pane_id)) |session| {
                            if (platform.ScrollbarGeometry.compute(placement.bounds, try session.terminal.scrollbar())) |sb| {
                                try session.terminal.scrollToRow(sb.targetOffsetForY(y, sb_drag.grab_offset));
                                session.scrollbar_visibility.reveal(self.cursorNowMs());
                                platform.invalidateWindow();
                            }
                        }
                    }
                },
            }
            if (event == .end) {
                self.pointer_drag = .none;
                try updateHoverState(self, model.activeTab().tree.paneCount() > 1, x, y, modifiers);
                return .normal;
            }
        },
        .cancel => {
            if (try cancelPointerDrag(self)) try self.updateLayout();
            return .normal;
        },
    }
    if (self.pointer_drag == .tab) return switch (self.pointer_drag.tab) {
        .none => .normal,
        .armed => .tab_armed,
        .dragging => .tab_dragging,
    };
    if (self.pointer_drag == .pane) return if (self.pointer_drag.pane.dragging) .tab_dragging else .tab_armed;
    if (self.pointer_drag == .scrollbar) return .tab_dragging;
    if (self.pointer_drag == .selection or self.pointer_drag == .mouse) return .selection;
    if (event == .hover and self.pointer_drag == .none and self.hovered_header_action != null) return .link;
    if (event == .hover and self.pointer_drag == .none and self.hovered_scrollbar != null) return .link;
    if (event == .hover and self.pointer_drag == .none and self.hovered_link != null) return .link;
    if (!model.activeTab().is_focus_mode) {
        const drag: DividerDrag = if (self.pointer_drag == .divider) self.pointer_drag.divider else .{};
        if (try drag.cursorAxis(&model.activeTab().tree, bounds, x, y)) |axis| {
            return switch (axis) {
                .right => .right,
                .down => .down,
            };
        }
    }
    if (event == .hover and self.pointer_drag == .none) {
        for (self.placements.items) |placement| {
            if (placement.bounds.contains(x, y) and try metrics.viewportPoint(placement.bounds, x, y, false) != null) return .text;
        }
    }
    return .normal;
}

fn reportMouseEvent(
    self: *Application,
    session: *TerminalSession,
    action: terminal.MouseAction,
    button: terminal.MouseButton,
    modifiers: u32,
    vp: platform.ViewportPoint,
    any_button_pressed: bool,
) !void {
    _ = self;
    if (session.state != .running) return;
    var buffer: [128]u8 = undefined;
    const bytes = try session.terminal.encodeMouse(
        action,
        button,
        modifiers,
        @floatCast(vp.x_px),
        @floatCast(vp.y_px),
        any_button_pressed,
        &buffer,
    );
    if (bytes.len > 0) {
        // Some TUIs copy on press, others on release. Do not discard a press's
        // pending clipboard write merely because the same click was released.
        if (action != .motion and !(action == .release and session.pending_copy_text != null)) session.recordApplicationInput();
        try session.writePty(bytes);
    }
}
