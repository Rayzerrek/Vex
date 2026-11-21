const std = @import("std");
const workspace = @import("workspace_model.zig");
const Rect = @import("pane_tree.zig").PaneRect;
const Density = @import("ui_density.zig").UiDensity;

/// Tab pointer coordinates are finite client DIPs, including captured positions outside the strip.
pub const TabPointer = struct {
    x: f32,
    y: f32,
    /// Reject invalid IO/test points before hit testing or a drag transition.
    pub fn validate(self: TabPointer) !void {
        if (!std.math.isFinite(self.x) or !std.math.isFinite(self.y)) return error.InvalidTabPointer;
    }
};
/// Shared geometry for the sidebar rows and the window's workspace controls.
pub const TabStripLayout = struct {
    count: usize,
    selected: usize,
    sidebar_width: f32,
    horizontal: bool = false,
    first_row: Rect,
    row_pitch: f32,
    plus_x: f32,

    pub const height: f32 = 44;
    pub const toggle: Rect = .{ .x = 8, .y = 7, .width = 28, .height = 30 };
    const close_width: f32 = 22;
    pub const TabHit = struct { index: usize, part: enum { body, close } };

    pub fn init(width: f32, count: usize, selected: usize, density: Density, collapsed: bool) !TabStripLayout {
        if (!std.math.isFinite(width) or width <= 0 or width > 65536 or count > workspace.WorkspaceModel.max_tabs or selected >= count) return error.InvalidTabStripLayout;
        const sidebar = if (collapsed) 0 else sidebarWidth(width, density);
        const row_height = height - 2 * density.metrics().tab_inset - 4;
        const plus_x = if (sidebar == 0) @max(42, width - 166) else sidebar - 50;
        return .{
            .count = count,
            .selected = selected,
            .sidebar_width = sidebar,
            .first_row = .{ .x = 8, .y = if (density == .compact) height + 7 else 69, .width = @max(0, sidebar - 16), .height = row_height },
            .row_pitch = row_height + 3,
            .plus_x = plus_x,
        };
    }

    pub fn initHorizontal(width: f32, count: usize, selected: usize, density: Density) !TabStripLayout {
        var layout = try init(width, count, selected, density, true);
        layout.horizontal = true;
        const inset = density.metrics().tab_inset;
        // Reserve both caption split buttons without shifting existing tabs when a split opens.
        const split_controls: f32 = if (density == .compact) 56 else 0;
        const pitch = @min(density.metrics().tab_max_width + 4, @max(4, (width - 170 - split_controls) / @as(f32, @floatFromInt(count))));
        layout.first_row = .{ .x = 8, .y = inset, .width = pitch - 4, .height = height - 2 * inset };
        layout.row_pitch = pitch;
        layout.plus_x = @max(layout.plus_x, 8 + pitch * @as(f32, @floatFromInt(count)) + split_controls);
        return layout;
    }

    pub fn sidebarWidth(width: f32, density: Density) f32 {
        if (width < 320) return 0;
        const max = density.metrics().tab_max_width + @as(f32, if (density == .compact) 0 else 16);
        return @min(max, @floor(width * 0.36));
    }

    pub fn tabBounds(self: TabStripLayout, index: usize) ?Rect {
        if (index >= self.count or (!self.horizontal and self.sidebar_width == 0)) return null;
        var row = self.first_row;
        const offset = @as(f32, @floatFromInt(index)) * self.row_pitch;
        if (self.horizontal) row.x += offset else row.y += offset;
        return row;
    }

    pub fn hitTab(self: TabStripLayout, point: TabPointer) !?TabHit {
        try point.validate();
        for (0..self.count) |index| {
            const row = self.tabBounds(index) orelse continue;
            if (row.contains(point.x, point.y)) {
                return .{ .index = index, .part = if (point.x >= row.x + row.width - close_width) .close else .body };
            }
        }
        return null;
    }

    pub fn insertionSlot(self: TabStripLayout, source: usize, point: TabPointer) !?usize {
        try point.validate();
        if (source >= self.count) return error.InvalidTabSource;
        const row = self.first_row;
        if (self.count < 2 or (!self.horizontal and self.sidebar_width == 0)) return null;
        const along = if (self.horizontal) point.x else point.y;
        const start = if (self.horizontal) row.x else row.y;
        if (self.horizontal) {
            if (point.y < 0 or point.y >= height or point.x < 0 or point.x >= self.plus_x) return null;
        } else if (point.x < 0 or point.x >= self.sidebar_width or along < start - 8 or
            along >= start + self.row_pitch * @as(f32, @floatFromInt(self.count)) + 8) return null;
        const center = start + (if (self.horizontal) row.width else row.height) / 2;
        var slot: usize = 0;
        for (0..self.count) |index| {
            if (index == source) continue;
            if (along < center + @as(f32, @floatFromInt(index)) * self.row_pitch) break;
            slot += 1;
        }
        return if (slot == source) null else slot;
    }

    pub fn marker(self: TabStripLayout, source: usize, slot: usize) Rect {
        const edge = if (slot > source) slot + 1 else slot;
        if (self.horizontal) return .{ .x = self.first_row.x + self.row_pitch * @as(f32, @floatFromInt(edge)) - 2, .y = self.first_row.y + 3, .width = 2, .height = self.first_row.height - 6 };
        return .{ .x = self.first_row.x + 4, .y = self.first_row.y + self.row_pitch * @as(f32, @floatFromInt(edge)) - 2, .width = self.first_row.width - 8, .height = 2 };
    }
};

test "eight sidebar tabs fit the minimum window in every density, with separate body and close targets" {
    for (std.enums.values(Density)) |density| {
        const layout = try TabStripLayout.init(640, 8, 3, density, false);
        const last = layout.tabBounds(7).?;
        try std.testing.expect(last.y + last.height < 400);
        const body = (try layout.hitTab(.{ .x = last.x + 5, .y = last.y + 5 })).?;
        try std.testing.expect(body.index == 7 and body.part == .body);
        const close = (try layout.hitTab(.{ .x = last.x + last.width - 1, .y = last.y })).?;
        try std.testing.expect(close.index == 7 and close.part == .close);
        try std.testing.expect(try layout.hitTab(.{ .x = last.x, .y = last.y - 1 }) == null);
        try std.testing.expectEqual(@as(?usize, 7), try layout.insertionSlot(0, .{ .x = last.x, .y = last.y + last.height - 1 }));
        try std.testing.expect(try layout.insertionSlot(0, .{ .x = layout.sidebar_width, .y = last.y }) == null);
    }
}

test "a collapsed sidebar has no tab rows or tab hit targets in the caption" {
    const layout = try TabStripLayout.init(640, 8, 3, .comfortable, true);
    for (0..layout.count) |index| try std.testing.expect(layout.tabBounds(index) == null);
    for ([_]f32{ 50, 360, layout.plus_x }) |x| {
        try std.testing.expect(try layout.hitTab(.{ .x = x, .y = 22 }) == null);
    }
    try std.testing.expect(try layout.insertionSlot(3, .{ .x = 50, .y = 22 }) == null);
    try std.testing.expectError(error.InvalidTabPointer, layout.hitTab(.{ .x = std.math.inf(f32), .y = 20 }));
}

test "eight horizontal tabs leave the new-tab controls clear and reorder along x" {
    for (std.enums.values(Density)) |density| {
        const layout = try TabStripLayout.initHorizontal(640, 8, 0, density);
        const last = layout.tabBounds(7).?;
        const split_controls: f32 = if (density == .compact) 56 else 0;
        try std.testing.expect(last.x + last.width < layout.plus_x - split_controls);
        const point: TabPointer = .{ .x = last.x + last.width - 1, .y = 22 };
        try std.testing.expectEqual(@as(usize, 7), (try layout.hitTab(point)).?.index);
        try std.testing.expectEqual(@as(?usize, 7), try layout.insertionSlot(0, point));
        try std.testing.expect(try layout.hitTab(.{ .x = point.x, .y = 44 }) == null);
    }
}
