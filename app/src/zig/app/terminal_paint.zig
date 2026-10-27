const TerminalApplication = @import("terminal_application.zig").TerminalApplication;
const platform = @import("../windows/windows_ffi.zig");
const app_icon = @import("../terminal/app_icon.zig");
const status_presentation = @import("../terminal/status_presentation.zig");
const tab_strip = @import("../workspace/tab_strip.zig");
const cursor_blink = @import("../workspace/cursor_blink.zig");
const overlays = @import("terminal_overlays.zig");
const pointer = @import("terminal_pointer.zig");
const panes = @import("../workspace/pane_tree.zig");

pub fn paintWorkspace(self: *TerminalApplication) !void {
    const model = if (self.model) |*model| model else return;
    const active_theme = self.currentTheme();
    platform.drawTabStripBackground(model.selected_tab);
    for (model.tabs.items, 0..) |tab, index| {
        const presentation = try self.tabProgramStatus(tab.tree.root);
        const active_session = self.findSession(tab.active_pane) orelse return error.MissingPaneSession;
        var tab_icons: [8]app_icon.AppIcon = undefined;
        var icon_count: usize = 0;
        try collectTabIcons(self, tab.tree.root, &tab_icons, &icon_count);
        if (icon_count == 0) {
            tab_icons[0] = active_session.icon;
            icon_count = 1;
        }
        const reported_title = status_presentation.statusTitle(presentation.summary, active_session.displayTitle());
        const default_title = tab.customTitle() orelse reported_title;
        const tab_edit: ?platform.TabEdit = if (self.editing_tab) |*edit|
            (if (edit.tab_id == tab.id) .{
                .text = edit.slice(),
                .placeholder = reported_title,
                .cursor = edit.cursor,
                .select_all = edit.select_all,
            } else null)
        else
            null;
        platform.drawTab(index, model.tabs.items.len, index == model.selected_tab, tab_edit, default_title, tab_icons[0..icon_count], presentation.summary, self.status_pulse.phaseIndex(), presentation.attention);
    }
    if (self.pointer_drag == .tab) {
        const metrics = self.metrics orelse return;
        if (try self.pointer_drag.tab.markerX(model, try tab_strip.TabStripLayout.init(metrics.width, model.tabs.items.len))) |x| try platform.drawTabInsertionMarker(x);
    }
    const active_tab = model.activeTab();
    const can_focus = active_tab.tree.paneCount() > 1;
    var saw_active_pane = false;
    const paint_now = self.cursorNowMs();
    const motion_enabled = platform.animationsEnabled();
    for (self.placements.items, 0..) |final_placement, placement_index| {
        const placement: panes.PanePlacement = .{ .id = final_placement.id, .bounds = self.layout_motion.bounds(final_placement, placement_index, paint_now, motion_enabled) };
        if (self.overlay == .tab_peek) break;
        if (placement.bounds.width < 14 or placement.bounds.height < 38) continue;
        const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
        const focused = active_tab.active_pane == placement.id and self.overlay == .none and self.editing_tab == null;
        const hovered = self.hovered_pane == placement.id and self.overlay == .none;
        const hovered_action = if (hovered) self.hovered_header_action else null;
        const frame = try session.terminal.readFrame();
        if (active_tab.active_pane == placement.id) {
            saw_active_pane = true;
            const key = if (focused and self.cursor_blink_enabled and self.cursor_window_active and session.state == .running)
                cursor_blink.CursorKey.fromFrame(placement.id, frame)
            else
                null;
            self.cursor_blink.synchronize(key, self.cursorNowMs());
        }
        const pane_link: ?platform.HoveredLinkSpan = if (self.overlay == .none) (if (self.hovered_link) |hl| (if (hl.pane_id == placement.id) .{ .row = hl.row, .start_col = hl.start_col, .end_col = hl.end_col } else null) else null) else null;
        {
            const summary = session.terminal.program_status.programStatus();
            platform.beginPane(placement.bounds, active_tab.active_pane == placement.id, hovered, can_focus, active_tab.is_focus_mode, hovered_action, status_presentation.statusTitle(summary, session.displayTitle()), session.icon, summary, self.status_pulse.phaseIndex());
            defer platform.endPane();
            try platform.drawTerminalFrame(placement.id, frame, focused and session.state == .running, self.cursor_blink.isShown(), pane_link, active_theme);
        }
        const scrollbar = try session.terminal.scrollbar();
        if (self.metrics) |metrics| {
            if (session.copy_feedback.progress(self.cursorNowMs(), scrollbar.offset, platform.animationsEnabled())) |copy_progress| {
                platform.drawCopyFeedback(placement.bounds, metrics.cell_width, metrics.cell_height, session.copy_feedback.spans.items, copy_progress);
            }
        }
        var geometry = platform.ScrollbarGeometry.compute(placement.bounds, scrollbar);
        if (geometry) |*sb| {
            if (self.pointer_drag == .scrollbar and self.pointer_drag.scrollbar.pane_id == placement.id) {
                const drag = self.pointer_drag.scrollbar;
                sb.thumb_y = sb.thumbTopForY(drag.pointer_y, drag.grab_offset);
            }
        }
        const sb_active = self.scrollbarActive(placement.id);
        const opacity = session.scrollbar_visibility.opacity(self.cursorNowMs(), geometry != null, sb_active, platform.animationsEnabled());
        if (geometry) |sb| {
            if (opacity > 0) platform.drawScrollbar(placement.bounds, sb, sb_active, opacity);
        }
    }
    var preview_bounds: panes.PaneRect = .{ .x = 0, .y = 0, .width = 0, .height = 0 };
    const drop: pointer.PaneDrop = blk: {
        if (self.pointer_drag == .pane and self.pointer_drag.pane.dragging) {
            const drag = self.pointer_drag.pane;
            break :blk pointer.paneDropAt(self, drag.pane_id, drag.point.x, drag.point.y);
        }
        if (self.pointer_drag == .tab and self.pointer_drag.tab == .dragging) {
            const drag = self.pointer_drag.tab.dragging;
            const index = model.tabIndex(drag.id) orelse break :blk .{};
            break :blk pointer.tabDropAt(self, drag.id, model.tabs.items[index].active_pane, drag.point.x, drag.point.y);
        }
        break :blk .{};
    };
    if (drop.target) |target| {
        if (self.findPlacement(target)) |placement| {
            if (placement.bounds.splitPreviewRect(drop.zone)) |preview| preview_bounds = preview;
        }
    }
    // An empty target lets the native chrome finish its interruptible fade-out.
    platform.drawSplitPreview(preview_bounds);
    if (self.pointer_drag == .pane and self.pointer_drag.pane.dragging) {
        const drag = self.pointer_drag.pane;
        const session = self.findSession(drag.pane_id) orelse return;
        const metrics = self.metrics orelse return;
        const width = @min(200, @max(40, metrics.width - 8));
        const ghost: panes.PaneRect = .{
            .x = @max(4, @min(drag.point.x - 24, metrics.width - width - 4)),
            .y = @max(3, @min(drag.point.y - 14, metrics.height - 38)),
            .width = width,
            .height = 34,
        };
        const icons = [_]app_icon.AppIcon{session.icon};
        platform.drawTabDrag(ghost, model.tabs.items.len, session.displayTitle(), &icons, drop.target != null);
    }
    if (!saw_active_pane) self.cursor_blink.synchronize(null, self.cursorNowMs());
    try self.syncAnimationWake();
    try overlays.drawActiveOverlay(self);
    if (self.pointer_drag == .tab and self.pointer_drag.tab == .dragging) {
        const metrics = self.metrics orelse return;
        const drag = &self.pointer_drag.tab;
        const index = model.tabIndex(drag.tabId().?) orelse return;
        const tab = &model.tabs.items[index];
        const session = self.findSession(tab.active_pane) orelse return;
        const layout = try tab_strip.TabStripLayout.init(metrics.width, model.tabs.items.len);
        if (drag.ghostBounds(layout, metrics.width, metrics.height)) |bounds| {
            var icons: [8]app_icon.AppIcon = undefined;
            var count: usize = 0;
            try collectTabIcons(self, tab.tree.root, &icons, &count);
            if (count == 0) {
                icons[0] = session.icon;
                count = 1;
            }
            const summary = try self.tabProgramStatus(tab.tree.root);
            platform.drawTabDrag(bounds, index, tab.customTitle() orelse status_presentation.statusTitle(summary.summary, session.displayTitle()), icons[0..count], drag.dragging.destination != null or drop.target != null);
        }
    }
}

fn collectTabIcons(self: *TerminalApplication, node: anytype, out: *[8]app_icon.AppIcon, count: *usize) anyerror!void {
    switch (node.*) {
        .leaf => |id| {
            const session = self.findSession(id) orelse return error.MissingPaneSession;
            if (session.icon.is_shell) return;
            for (out[0..count.*]) |existing| {
                if (existing.eql(session.icon)) return;
            }
            if (count.* < out.len) {
                out[count.*] = session.icon;
                count.* += 1;
            }
        },
        .split => |split| {
            try collectTabIcons(self, split.first, out, count);
            try collectTabIcons(self, split.second, out, count);
        },
    }
}
