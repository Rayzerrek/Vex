const std = @import("std");
const workspace = @import("workspace_model.zig");

/// Tab pointer coordinates are finite client DIPs, including captured positions outside the strip.
pub const TabPointer = struct {
    x: f32,
    y: f32,
    /// Reject invalid IO/test points before hit testing or a drag transition.
    pub fn validate(self: TabPointer) !void {
        if (!std.math.isFinite(self.x) or !std.math.isFinite(self.y)) return error.InvalidTabPointer;
    }
};
/// One tab-strip partition drives drawing, hit testing and insertion slots; count is bounded to the workspace limit.
pub const TabStripLayout = struct {
    count: usize,
    tab_width: f32,
    /// Logo/project chrome reserves the first 140 client DIPs before the tab strip.
    pub const left: f32 = 140;
    /// Drop tolerance is measured against the existing 44-DIP caption/tab band.
    pub const height: f32 = 44;
    const gap: f32 = 4;
    /// A parsed tab hit always has a live strip index; body excludes the close region.
    pub const TabHit = struct { index: usize, part: enum { body, close } };

    /// Allows two-line activity labels when space permits; compact slots still preserve close/drag hit regions.
    pub fn init(width: f32, count: usize) !TabStripLayout {
        if (!std.math.isFinite(width) or width <= 0 or width > 65536 or count == 0 or count > workspace.WorkspaceModel.max_tabs) return error.InvalidTabStripLayout;
        return .{ .count = count, .tab_width = @min(200, @max(24, (width - left - 120 - 38) / @as(f32, @floatFromInt(count)) - gap)) };
    }
    /// Tab slots include their trailing gap for insertion, but gap clicks never select or close a tab.
    pub fn slotWidth(self: TabStripLayout) f32 {
        return self.tab_width + gap;
    }
    /// Plus-button position is also the right edge of the tab-strip drag region.
    pub fn plusLeft(self: TabStripLayout) f32 {
        return left + @as(f32, @floatFromInt(self.count)) * self.slotWidth();
    }
    /// Excludes × and strip gaps from the tab body; outer bounds are half-open.
    pub fn hitTab(self: TabStripLayout, point: TabPointer) !?TabHit {
        try point.validate();
        if (point.y < 0 or point.y >= height or point.x < left or point.x >= self.plusLeft()) return null;
        const index: usize = @intFromFloat(@floor((point.x - left) / self.slotWidth()));
        if (index >= self.count) return null;
        const local = point.x - left - @as(f32, @floatFromInt(index)) * self.slotWidth();
        if (local >= self.tab_width) return null;
        return .{ .index = index, .part = if (local > self.tab_width - 22) .close else .body };
    }
    /// Matches WPF's compacted remaining-tab slots, home-slot no-op and ±8-DIP vertical drop tolerance.
    pub fn insertionSlot(self: TabStripLayout, source: usize, point: TabPointer) !?usize {
        try point.validate();
        if (source >= self.count) return error.InvalidTabSource;
        if (self.count < 2 or point.y < -8 or point.y > height + 8) return null;
        var slot: usize = 0;
        while (slot < self.count - 1) : (slot += 1) {
            if (point.x <= left + @as(f32, @floatFromInt(slot + 1)) * self.slotWidth()) break;
        }
        return if (slot == source) null else slot;
    }
    /// Marker identifies the final slot; this checkpoint does not animate or collapse the source header.
    pub fn markerX(self: TabStripLayout, slot: usize) !f32 {
        if (slot >= self.count) return error.InvalidTabDestination;
        return left + @as(f32, @floatFromInt(slot)) * self.slotWidth();
    }
};

test "tab strip shares bounded geometry with body/close/gap and insertion hit testing" {
    const layout = try TabStripLayout.init(1120, 3);
    try std.testing.expectEqual(@as(f32, 200), layout.tab_width);
    try std.testing.expectEqual(@as(f32, 752), layout.plusLeft());
    const body = try layout.hitTab(.{ .x = 150, .y = 20 }) orelse return error.TestExpectedTab;
    try std.testing.expectEqual(@as(usize, 0), body.index);
    try std.testing.expect(body.part == .body);
    const close = try layout.hitTab(.{ .x = 339, .y = 20 }) orelse return error.TestExpectedTab;
    try std.testing.expect(close.part == .close);
    for ([_]TabPointer{ .{ .x = 340, .y = 20 }, .{ .x = 139, .y = 20 }, .{ .x = 752, .y = 20 }, .{ .x = 150, .y = 44 } }) |point| try std.testing.expectEqual(@as(?TabStripLayout.TabHit, null), try layout.hitTab(point));
    try std.testing.expectEqual(@as(?usize, null), try layout.insertionSlot(0, .{ .x = 344, .y = 20 }));
    try std.testing.expectEqual(@as(?usize, 1), try layout.insertionSlot(0, .{ .x = 345, .y = 20 }));
    try std.testing.expectEqual(@as(?usize, 2), try layout.insertionSlot(0, .{ .x = 3000, .y = 52 }));
    try std.testing.expectEqual(@as(?usize, 0), try layout.insertionSlot(2, .{ .x = -300, .y = -8 }));
    try std.testing.expectEqual(@as(?usize, null), try layout.insertionSlot(2, .{ .x = -300, .y = -9 }));
    try std.testing.expectEqual(@as(f32, 548), try layout.markerX(2));
    const single = try TabStripLayout.init(640, 1);
    try std.testing.expectEqual(@as(?usize, null), try single.insertionSlot(0, .{ .x = 3000, .y = 20 }));
    const compact = try TabStripLayout.init(640, 8);
    try std.testing.expect(compact.tab_width >= 24 and compact.plusLeft() + 32 <= 640 - 120);
    try std.testing.expectError(error.InvalidTabStripLayout, TabStripLayout.init(std.math.nan(f32), 3));
    try std.testing.expectError(error.InvalidTabStripLayout, TabStripLayout.init(1120, 0));
    try std.testing.expectError(error.InvalidTabStripLayout, TabStripLayout.init(1120, 9));
    try std.testing.expectError(error.InvalidTabPointer, layout.hitTab(.{ .x = std.math.inf(f32), .y = 20 }));
    try std.testing.expectError(error.InvalidTabSource, layout.insertionSlot(3, .{ .x = 150, .y = 20 }));
}
