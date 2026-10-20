const std = @import("std");
const panes = @import("pane_tree.zig");

/// Owns a divider drag without retaining tree pointers; cancel restores the pre-drag ratio of the same live ID.
pub const DividerDrag = struct {
    active: ?struct { id: panes.DividerId, axis: panes.SplitAxis, initial_ratio: f32, grab_offset: f32 } = null,

    /// Returns a resize cursor only where two 100-DIP neighbors fit, or for the currently captured divider.
    pub fn cursorAxis(self: *const DividerDrag, tree: *const panes.PaneTree, bounds: panes.PaneRect, x: f32, y: f32) !?panes.SplitAxis {
        try validateDividerPoint(x, y);
        if (self.active) |active| return active.axis;
        const placement = try tree.dividerAtPoint(bounds, x, y) orelse return null;
        return if (dragLimits(placement) != null) placement.axis else null;
    }
    /// Keeps the initial grab offset inside the handle so the first move does not jump to its center.
    pub fn begin(self: *DividerDrag, tree: *const panes.PaneTree, bounds: panes.PaneRect, x: f32, y: f32) !bool {
        try validateDividerPoint(x, y);
        if (self.active != null) return false;
        const placement = try tree.dividerAtPoint(bounds, x, y) orelse return false;
        if (dragLimits(placement) == null) return false;
        self.active = .{
            .id = placement.id,
            .axis = placement.axis,
            .initial_ratio = placement.ratio,
            .grab_offset = switch (placement.axis) {
                .right => x - placement.handle.x,
                .down => y - placement.handle.y,
            },
        };
        return true;
    }
    /// Re-resolves current container geometry on each move; stale targets clear the drag instead of retargeting.
    pub fn move(self: *DividerDrag, tree: *panes.PaneTree, bounds: panes.PaneRect, x: f32, y: f32) !bool {
        try validateDividerPoint(x, y);
        const active = self.active orelse return false;
        const placement = try tree.dividerPlacement(bounds, active.id) orelse {
            self.active = null;
            return false;
        };
        const limits = dragLimits(placement) orelse return false;
        const origin, const extent, const pointer = switch (placement.axis) {
            .right => .{ placement.container.x, placement.container.width, x },
            .down => .{ placement.container.y, placement.container.height, y },
        };
        const gap = switch (placement.axis) {
            .right => placement.handle.width,
            .down => placement.handle.height,
        };
        const position = std.math.clamp(pointer, origin, origin + extent);
        const ratio = (position - origin - active.grab_offset) / (extent - gap);
        if (!std.math.isFinite(ratio)) return error.InvalidSplitRatio;
        return tree.setDividerRatio(active.id, std.math.clamp(ratio, limits.minimum, limits.maximum));
    }
    /// Commits the last applied ratio; ending an inactive drag is harmless.
    pub fn finish(self: *DividerDrag) void {
        self.active = null;
    }
    /// Clears state before rollback so capture-loss reentrancy cannot restore a different divider.
    pub fn cancel(self: *DividerDrag, tree: *panes.PaneTree) !bool {
        const active = self.active orelse return false;
        self.active = null;
        return tree.setDividerRatio(active.id, active.initial_ratio);
    }
};

const DragLimits = struct { minimum: f32, maximum: f32 };
fn dragLimits(placement: panes.DividerPlacement) ?DragLimits {
    const available = switch (placement.axis) {
        .right => placement.container.width - placement.handle.width,
        .down => placement.container.height - placement.handle.height,
    };
    // Existing tiny layouts remain valid; a drag must not make either neighbor smaller than WPF's minimum.
    if (available < 200) return null;
    const minimum = @max(@as(f32, 0.1), 100 / available);
    return .{ .minimum = minimum, .maximum = 1 - minimum };
}
fn validateDividerPoint(x: f32, y: f32) !void {
    if (!std.math.isFinite(x) or !std.math.isFinite(y)) return error.InvalidDividerPoint;
}

const test_bounds: panes.PaneRect = .{ .x = 10, .y = 20, .width = 1006, .height = 606 };

test "divider hit testing matches nested leaf geometry and excludes pane and outer edges" {
    var tree = try panes.PaneTree.init(std.testing.allocator, @enumFromInt(1));
    defer tree.deinit();
    try tree.splitPane(@enumFromInt(1), @enumFromInt(2), .right);
    try tree.splitPane(@enumFromInt(2), @enumFromInt(3), .down);
    const root = try tree.dividerAtPoint(test_bounds, 510, 20) orelse return error.TestExpectedDivider;
    try std.testing.expectEqual(@as(panes.DividerId, @enumFromInt(2)), root.id);
    try std.testing.expectEqual(panes.SplitAxis.right, root.axis);
    try std.testing.expectEqual(panes.PaneRect{ .x = 510, .y = 20, .width = 6, .height = 606 }, root.handle);
    const nested = try tree.dividerAtPoint(test_bounds, 516, 320) orelse return error.TestExpectedDivider;
    try std.testing.expectEqual(@as(panes.DividerId, @enumFromInt(3)), nested.id);
    try std.testing.expectEqual(panes.SplitAxis.down, nested.axis);
    try std.testing.expectEqual(panes.PaneRect{ .x = 516, .y = 20, .width = 500, .height = 606 }, nested.container);
    for ([_][2]f32{ .{ 509, 319 }, .{ 516, 319 }, .{ 516, 326 }, .{ 1016, 320 }, .{ 510, 626 }, .{ 0, 0 } }) |point| {
        try std.testing.expectEqual(@as(?panes.DividerPlacement, null), try tree.dividerAtPoint(test_bounds, point[0], point[1]));
    }
    var placements: std.ArrayList(panes.PanePlacement) = .empty;
    defer placements.deinit(std.testing.allocator);
    try tree.layout(std.testing.allocator, &placements, test_bounds);
    try std.testing.expectEqual(placements.items[0].bounds.x + placements.items[0].bounds.width, root.handle.x);
    try std.testing.expectEqual(placements.items[1].bounds.y + placements.items[1].bounds.height, nested.handle.y);
}

test "divider drag preserves grab offset, clamps both neighbors, commits and rolls back without changing focus identities" {
    var tree = try panes.PaneTree.init(std.testing.allocator, @enumFromInt(1));
    defer tree.deinit();
    try tree.splitPane(@enumFromInt(1), @enumFromInt(2), .right);
    var drag: DividerDrag = .{};
    try std.testing.expect(try drag.begin(&tree, test_bounds, 515, 100));
    try std.testing.expect(!try drag.move(&tree, test_bounds, 515, 100));
    try std.testing.expect(try drag.move(&tree, test_bounds, 615, 100));
    const moved = try tree.dividerPlacement(test_bounds, @enumFromInt(2)) orelse return error.TestExpectedDivider;
    try std.testing.expectApproxEqAbs(@as(f32, 0.6), moved.ratio, 0.0001);
    try std.testing.expect(try drag.move(&tree, test_bounds, -1000, 100));
    const minimum = try tree.dividerPlacement(test_bounds, @enumFromInt(2)) orelse return error.TestExpectedDivider;
    try std.testing.expectApproxEqAbs(@as(f32, 0.1), minimum.ratio, 0.0001);
    try std.testing.expect(try drag.move(&tree, test_bounds, 3000, 100));
    const maximum = try tree.dividerPlacement(test_bounds, @enumFromInt(2)) orelse return error.TestExpectedDivider;
    try std.testing.expectApproxEqAbs(@as(f32, 0.9), maximum.ratio, 0.0001);
    try std.testing.expect(try drag.cancel(&tree));
    try std.testing.expect(!try drag.cancel(&tree));
    try std.testing.expect(try drag.begin(&tree, test_bounds, 512, 100));
    try std.testing.expect(try drag.move(&tree, test_bounds, 712, 100));
    drag.finish();
    try std.testing.expect(!try drag.cancel(&tree));
    const committed = try tree.dividerPlacement(test_bounds, @enumFromInt(2)) orelse return error.TestExpectedDivider;
    try std.testing.expectApproxEqAbs(@as(f32, 0.7), committed.ratio, 0.0001);
    try std.testing.expectEqual(@as(usize, 2), tree.paneCount());
    try std.testing.expect(tree.containsPane(@enumFromInt(1)) and tree.containsPane(@enumFromInt(2)));
}

test "nested divider uses its own container and survives sibling promotion without holding a node pointer" {
    var tree = try panes.PaneTree.init(std.testing.allocator, @enumFromInt(1));
    defer tree.deinit();
    try tree.splitPane(@enumFromInt(1), @enumFromInt(2), .right);
    try tree.splitPane(@enumFromInt(2), @enumFromInt(3), .down);
    var drag: DividerDrag = .{};
    try std.testing.expect(try drag.begin(&tree, test_bounds, 700, 323));
    try std.testing.expect(try drag.move(&tree, test_bounds, 700, 383));
    const nested = try tree.dividerPlacement(test_bounds, @enumFromInt(3)) orelse return error.TestExpectedDivider;
    const root = try tree.dividerPlacement(test_bounds, @enumFromInt(2)) orelse return error.TestExpectedDivider;
    try std.testing.expectApproxEqAbs(@as(f32, 0.6), nested.ratio, 0.0001);
    try std.testing.expectEqual(@as(f32, 0.5), root.ratio);
    try std.testing.expect(tree.closePane(@enumFromInt(1)));
    try std.testing.expect(try drag.cancel(&tree));
    const promoted = try tree.dividerPlacement(test_bounds, @enumFromInt(3)) orelse return error.TestExpectedDivider;
    try std.testing.expectEqual(test_bounds, promoted.container);
    try std.testing.expectEqual(@as(f32, 0.5), promoted.ratio);
    try std.testing.expect(try drag.begin(&tree, test_bounds, 700, 323));
    try std.testing.expect(tree.closePane(@enumFromInt(3)));
    try tree.splitPane(@enumFromInt(2), @enumFromInt(4), .down);
    try std.testing.expect(!try drag.move(&tree, test_bounds, 700, 400));
    try std.testing.expectEqual(@as(?panes.SplitAxis, .down), try drag.cursorAxis(&tree, test_bounds, 700, 323));
    const replacement = try tree.dividerPlacement(test_bounds, @enumFromInt(4)) orelse return error.TestExpectedDivider;
    try std.testing.expectEqual(@as(f32, 0.5), replacement.ratio);
    try std.testing.expect(drag.active == null);
}

test "divider drag validates input, honors 100 DIP minimum and disables undersized and zero-area handles" {
    var tree = try panes.PaneTree.init(std.testing.allocator, @enumFromInt(1));
    defer tree.deinit();
    try tree.splitPane(@enumFromInt(1), @enumFromInt(2), .right);
    var drag: DividerDrag = .{};
    const small: panes.PaneRect = .{ .x = 0, .y = 0, .width = 306, .height = 300 };
    try std.testing.expect(try drag.begin(&tree, small, 153, 100));
    try std.testing.expectError(error.InvalidDividerPoint, drag.move(&tree, small, std.math.inf(f32), 100));
    try std.testing.expectError(error.InvalidPaneGeometry, drag.move(&tree, .{ .x = 0, .y = 0, .width = -1, .height = 300 }, 100, 100));
    try std.testing.expectError(error.InvalidSplitRatio, tree.setDividerRatio(@enumFromInt(2), std.math.nan(f32)));
    const unchanged = try tree.dividerPlacement(small, @enumFromInt(2)) orelse return error.TestExpectedDivider;
    try std.testing.expectEqual(@as(f32, 0.5), unchanged.ratio);
    try std.testing.expect(try drag.move(&tree, small, -300, 100));
    const clamped = try tree.dividerPlacement(small, @enumFromInt(2)) orelse return error.TestExpectedDivider;
    try std.testing.expectApproxEqAbs(@as(f32, 100), clamped.handle.x, 0.0001);
    _ = try drag.cancel(&tree);
    for ([_]f32{ 0, 5, 100, 205 }) |width| {
        const tiny: panes.PaneRect = .{ .x = 0, .y = 0, .width = width, .height = 300 };
        try std.testing.expectEqual(@as(?panes.SplitAxis, null), try drag.cursorAxis(&tree, tiny, width / 2, 100));
        try std.testing.expect(!try drag.begin(&tree, tiny, width / 2, 100));
    }
    try std.testing.expectError(error.InvalidDividerPoint, drag.begin(&tree, small, std.math.nan(f32), 100));
}
