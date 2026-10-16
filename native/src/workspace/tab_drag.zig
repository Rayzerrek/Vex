const std = @import("std");
const workspace = @import("workspace_model.zig");
const strip = @import("tab_strip.zig");

/// A tab drag retains only an ID; collection order changes once, on a valid drop after the 6-DIP threshold.
pub const TabDrag = union(enum) {
    none,
    armed: struct { id: workspace.TabId, start: strip.TabPointer },
    dragging: struct { id: workspace.TabId, destination: ?usize },

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
                self.* = .{ .dragging = .{ .id = id, .destination = try layout.insertionSlot(source, point) } };
                return true;
            },
            .dragging => |*dragging| {
                const destination = try layout.insertionSlot(source, point);
                if (destination == dragging.destination) return false;
                dragging.destination = destination;
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
    fn tabId(self: *const TabDrag) ?workspace.TabId {
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
