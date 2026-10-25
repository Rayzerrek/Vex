const std = @import("std");
const workspace = @import("workspace_model.zig");
const strip = @import("tab_strip.zig");

/// A tab drag retains only an ID; collection order changes once, on a valid drop after the 6-DIP threshold.
pub const TabDrag = union(enum) {
    none,
    armed: struct { id: workspace.TabId, start: strip.TabPointer },
    dragging: struct { id: workspace.TabId, destination: ?usize, point: strip.TabPointer, grab_fraction: f32, grab_y: f32 },

    /// Arm only an existing tab in a multi-tab strip; the caller selects it before arming, as in WPF.
    pub fn begin(self: *TabDrag, model: *const workspace.WorkspaceModel, layout: strip.TabStripLayout, id: workspace.TabId, point: strip.TabPointer) !bool {
        try point.validate();
        try validateTabCount(model, layout);
        if (self.* != .none or model.tabIndex(id) == null or layout.count < 2) return false;
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
                const source_x = strip.TabStripLayout.left + @as(f32, @floatFromInt(source)) * layout.slotWidth();
                self.* = .{ .dragging = .{
                    .id = id,
                    .destination = try layout.insertionSlot(source, point),
                    .point = point,
                    .grab_fraction = std.math.clamp((armed.start.x - source_x) / layout.tab_width, 0, 1),
                    .grab_y = armed.start.y - 5,
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
    pub fn markerX(self: *const TabDrag, model: *const workspace.WorkspaceModel, layout: strip.TabStripLayout) !?f32 {
        try validateTabCount(model, layout);
        const dragging = switch (self.*) {
            .dragging => |dragging| dragging,
            else => return null,
        };
        if (model.tabIndex(dragging.id) == null) return null;
        const destination = dragging.destination orelse return null;
        return try layout.markerX(destination);
    }
    /// Only lightweight header geometry follows the pointer; terminal surfaces stay put.
    pub fn ghostBounds(self: *const TabDrag, layout: strip.TabStripLayout, width: f32, height: f32) ?@import("pane_tree.zig").PaneRect {
        if (self.* != .dragging) return null;
        const drag = self.dragging;
        return .{
            .x = std.math.clamp(drag.point.x - drag.grab_fraction * layout.tab_width, 4, @max(4, width - layout.tab_width - 4)),
            .y = std.math.clamp(drag.point.y - drag.grab_y - 2, 3, @max(3, height - 38)),
            .width = layout.tab_width,
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
    const layout = try strip.TabStripLayout.init(1120, 3);
    var drag: TabDrag = .none;
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 160, .y = 20 }));
    try std.testing.expect(!try drag.move(&model, layout, .{ .x = 162, .y = 23 }));
    try std.testing.expect(drag == .armed);
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 163, .y = 23 }));
    try std.testing.expect(drag == .dragging);
    try std.testing.expectEqual(@as(?f32, null), try drag.markerX(&model, layout));
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 600, .y = 20 }));
    try std.testing.expectEqual(@as(?f32, 416), try drag.markerX(&model, layout));
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(1)), model.tabs.items[0].id);
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 600, .y = 53 }));
    try std.testing.expectEqual(@as(?f32, null), try drag.markerX(&model, layout));
    try std.testing.expect(!try drag.finish(&model, layout, .{ .x = 600, .y = 53 }));
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 160, .y = 20 }));
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 600, .y = 20 }));
    drag.cancel();
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(1)), model.tabs.items[0].id);
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 160, .y = 20 }));
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 600, .y = 20 }));
    try std.testing.expect(try drag.finish(&model, layout, .{ .x = 600, .y = 20 }));
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(1)), model.tabs.items[2].id);
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(3)), model.activeTab().id);
    try std.testing.expect(drag == .none);
}

test "drag ghost follows within a slot, preserves the grab anchor and stays inside resized clients" {
    var model = try workspace.WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try model.newTab(@enumFromInt(2));
    const layout = try strip.TabStripLayout.init(1120, 2);
    var drag: TabDrag = .none;
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 58, .y = 20 }));
    try std.testing.expect(drag.ghostBounds(layout, 1120, 700) == null);
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 70, .y = 20 }));
    const first = drag.ghostBounds(layout, 1120, 700).?;
    try std.testing.expectEqual(@as(f32, 20), first.x);
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 80, .y = 22 }));
    try std.testing.expectEqual(first.x + 10, drag.ghostBounds(layout, 1120, 700).?.x);
    try std.testing.expect(!try drag.move(&model, layout, .{ .x = 80, .y = 22 }));
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 2000, .y = 2000 }));
    const small = try strip.TabStripLayout.init(320, 2);
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
    var layout = try strip.TabStripLayout.init(640, 2);
    var drag: TabDrag = .none;
    try std.testing.expect(!try drag.begin(&model, layout, @enumFromInt(99), .{ .x = 160, .y = 20 }));
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 160, .y = 20 }));
    try std.testing.expect(!try drag.finish(&model, layout, .{ .x = 1000, .y = 20 }));
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(1)), model.tabs.items[0].id);
    try std.testing.expect(try drag.begin(&model, layout, @enumFromInt(1), .{ .x = 160, .y = 20 }));
    try std.testing.expectError(error.InvalidTabPointer, drag.move(&model, layout, .{ .x = std.math.nan(f32), .y = 20 }));
    try std.testing.expectError(error.TabStripCountMismatch, drag.move(&model, try strip.TabStripLayout.init(640, 3), .{ .x = 400, .y = 20 }));
    try std.testing.expect(drag == .armed);
    try std.testing.expect(model.closeTab(0));
    try model.newTab(@enumFromInt(3));
    try std.testing.expect(try drag.move(&model, layout, .{ .x = 600, .y = 20 }));
    try std.testing.expect(drag == .none);
    try std.testing.expectEqual(@as(workspace.TabId, @enumFromInt(2)), model.tabs.items[0].id);
    try std.testing.expect(model.closeTab(1));
    layout = try strip.TabStripLayout.init(640, 1);
    try std.testing.expect(!try drag.begin(&model, layout, @enumFromInt(2), .{ .x = 160, .y = 20 }));
}
