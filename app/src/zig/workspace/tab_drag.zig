const std = @import("std");
const workspace = @import("workspace_model.zig");
const strip = @import("tab_strip.zig");
const Rect = @import("pane_tree.zig").PaneRect;

/// A tab drag retains only an ID; collection order changes once, on a valid drop after the 6-DIP threshold.
pub const TabDrag = union(enum) {
    none,
    armed: struct { id: workspace.TabId, start: strip.TabPointer },
    dragging: struct { id: workspace.TabId, destination: ?usize, point: strip.TabPointer, grab_fraction: f32, grab_y: f32 },

    /// Arm only an existing tab in a multi-tab strip; the caller selects it before arming, as in WPF.
    pub fn begin(self: *TabDrag, model: *const workspace.WorkspaceModel, layout: strip.TabStripLayout, id: workspace.TabId, point: strip.TabPointer) !bool {
        try point.validate();
        try validateTabCount(model, layout);
        if (self.* != .none or layout.count < 2) return false;
        const index = model.tabIndex(id) orelse return false;
        if (layout.tabBounds(index) == null) return false;
        self.* = .{ .armed = .{ .id = id, .start = point } };
        return true;
    }
    /// Return true only when preview state changes; hover below threshold schedules no frame.
    pub fn move(self: *TabDrag, model: *const workspace.WorkspaceModel, layout: strip.TabStripLayout, point: strip.TabPointer) !bool {
        try point.validate();
        try validateTabCount(model, layout);
        const id = self.tabId() orelse return false;
        const source = model.tabIndex(id) orelse {
            self.* = .none;
            return true;
        };
        switch (self.*) {
            .none => return false,
            .armed => |armed| {
                if (@abs(point.x - armed.start.x) + @abs(point.y - armed.start.y) < 6) return false;
                const row = layout.tabBounds(source) orelse return false;
                self.* = .{ .dragging = .{
                    .id = id,
                    .destination = try layout.insertionSlot(source, point),
                    .point = point,
                    .grab_fraction = std.math.clamp((armed.start.x - row.x) / row.width, 0, 1),
                    .grab_y = armed.start.y - row.y,
                } };
                return true;
            },
            .dragging => |*dragging| {
                const destination = try layout.insertionSlot(source, point);
                if (destination == dragging.destination and point.x == dragging.point.x and point.y == dragging.point.y) return false;
                dragging.destination = destination;
                dragging.point = point;
                return true;
            },
        }
    }
    /// Drop outside the strip, home-slot drops and armed clicks do not reorder; clearing precedes the model move.
    pub fn finish(self: *TabDrag, model: *workspace.WorkspaceModel, layout: strip.TabStripLayout, point: strip.TabPointer) !bool {
        try point.validate();
        try validateTabCount(model, layout);
        const dragging = switch (self.*) {
            .dragging => |dragging| dragging,
            else => {
                self.* = .none;
                return false;
            },
        };
        self.* = .none;
        const source = model.tabIndex(dragging.id) orelse return false;
        const destination = try layout.insertionSlot(source, point) orelse return false;
        return model.moveTab(dragging.id, destination);
    }
    /// Cancellation cannot require rollback because no preview mutates the tab collection.
    pub fn cancel(self: *TabDrag) void {
        self.* = .none;
    }
    /// Returns a final-slot marker only for a live source with a valid current preview destination.
    pub fn marker(self: *const TabDrag, model: *const workspace.WorkspaceModel, layout: strip.TabStripLayout) !?Rect {
        try validateTabCount(model, layout);
        const dragging = switch (self.*) {
            .dragging => |dragging| dragging,
            else => return null,
        };
        const source = model.tabIndex(dragging.id) orelse return null;
        const destination = dragging.destination orelse return null;
        return layout.marker(source, destination);
    }
    /// Only lightweight header geometry follows the pointer; terminal surfaces stay put.
    pub fn ghostBounds(self: *const TabDrag, layout: strip.TabStripLayout, width: f32, height: f32) ?Rect {
        if (self.* != .dragging) return null;
        const drag = self.dragging;
        const w = layout.first_row.width;
        return .{
            .x = std.math.clamp(drag.point.x - drag.grab_fraction * w, 4, @max(4, width - w - 4)),
            .y = std.math.clamp(drag.point.y - drag.grab_y, 3, @max(3, height - 38)),
            .width = w,
            .height = 34,
        };
    }
    pub fn tabId(self: *const TabDrag) ?workspace.TabId {
        return switch (self.*) {
            .none => null,
            .armed => |armed| armed.id,
            .dragging => |dragging| dragging.id,
        };
    }
};
fn validateTabCount(model: *const workspace.WorkspaceModel, layout: strip.TabStripLayout) !void {
    if (layout.count != model.tabs.items.len) return error.TabStripCountMismatch;
}

test "tab drag threshold, home/no-target preview, commit and cancellation preserve tab owners" {
    var model = try workspace.WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try model.newTab(@enumFromInt(2));
    try model.newTab(@enumFromInt(3));
    const layout = try strip.TabStripLayout.init(1120, 3, 0, .comfortable, false);
    var drag: TabDrag = .none;
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 60, .y = 80 }));
    try std.testing.expect(!try drag.move(&model, layout, .{ .x = 62, .y = 83 }));
    try std.testing.expect(drag == .armed);
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 63, .y = 83 }));
    try std.testing.expect(drag == .dragging);
    try std.testing.expect(try drag.marker(&model, layout) == null);
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 60, .y = 155 }));
    try std.testing.expect(drag.dragging.destination == 2);
    try std.testing.expect((try drag.marker(&model, layout)) != null);
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(1)), model.tabs.items[0].id);
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 600, .y = 155 }));
    try std.testing.expect(try drag.marker(&model, layout) == null);
    try std.testing.expect(!try drag.finish(&model, layout, .{ .x = 600, .y = 155 }));
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 60, .y = 80 }));
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 60, .y = 155 }));
    drag.cancel();
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(1)), model.tabs.items[0].id);
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 60, .y = 80 }));
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 60, .y = 155 }));
    try std.testing.expect(try drag.finish(&model, layout, .{ .x = 60, .y = 155 }));
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(1)), model.tabs.items[2].id);
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(3)), model.activeTab().id);
    try std.testing.expect(drag == .none);
}

test "drag ghost follows within a slot, preserves the grab anchor and stays inside resized clients" {
    var model = try workspace.WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try model.newTab(@enumFromInt(2));
    const layout = try strip.TabStripLayout.init(1120, 2, 0, .comfortable, false);
    var drag: TabDrag = .none;
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 58, .y = 80 }));
    try std.testing.expect(drag.ghostBounds(layout, 1120, 700) == null);
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 70, .y = 80 }));
    const first = drag.ghostBounds(layout, 1120, 700).?;
    try std.testing.expectEqual(@as(f32, 20), first.x);
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 80, .y = 82 }));
    try std.testing.expectEqual(first.x + 10, drag.ghostBounds(layout, 1120, 700).?.x);
    try std.testing.expect(!try drag.move(&model, layout, .{ .x = 80, .y = 82 }));
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 2000, .y = 2000 }));
    const small = try strip.TabStripLayout.init(320, 2, 0, .comfortable, false);
    const ghost = drag.ghostBounds(small, 320, 200).?;
    try std.testing.expect(ghost.x >= 4 and ghost.x + ghost.width <= 316);
    try std.testing.expect(ghost.y >= 3 and ghost.y + ghost.height <= 200);
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(1)), model.tabs.items[0].id);
    drag.cancel();
    try std.testing.expect(drag.ghostBounds(layout, 1120, 700) == null);
}

test "tab drag ignores stale IDs and plain releases and rejects invalid boundary data before mutation" {
    var model = try workspace.WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try model.newTab(@enumFromInt(2));
    var layout = try strip.TabStripLayout.init(640, 2, 0, .comfortable, false);
    var drag: TabDrag = .none;
    try std.testing.expect(!try drag.begin(&model, layout, @enumFromInt(99), .{ .x = 160, .y = 20 }));
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 160, .y = 20 }));
    try std.testing.expect(!try drag.finish(&model, layout, .{ .x = 1000, .y = 20 }));
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(1)), model.tabs.items[0].id);
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 160, .y = 20 }));
    try std.testing.expectError(error.InvalidTabPointer, drag.move(&model, layout, .{ .x = std.math.nan(f32), .y = 20 }));
    try std.testing.expectError(error.TabStripCountMismatch, drag.move(&model, try strip.TabStripLayout.init(640, 3, 0, .comfortable, false), .{ .x = 400, .y = 80 }));
    try std.testing.expect(drag == .armed);
    try std.testing.expect(model.closeTab(0));
    try model.newTab(@enumFromInt(3));
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 600, .y = 20 }));
    try std.testing.expect(drag == .none);
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(2)), model.tabs.items[0].id);
    try std.testing.expect(model.closeTab(1));
    layout = try strip.TabStripLayout.init(640, 1, 0, .comfortable, false);
    try std.testing.expect(!try drag.begin(&model, layout, @enumFromInt(2), .{ .x = 160, .y = 20 }));
}
