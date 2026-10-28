const std = @import("std");
const panes = @import("pane_tree.zig");

/// Paint-only layout interpolation. The PTY is resized once to its final grid, never once per frame.
/// Default timing matches native ChromeMotion; focus has a longer spatial transition.
/// Stable IDs prevent a removed pane animating a replacement.
pub const LayoutMotion = struct {
    previous: std.ArrayList(panes.PanePlacement) = .empty,
    scratch: std.ArrayList(panes.PanePlacement) = .empty,
    opening: ?struct { id: panes.PaneId, axis: panes.SplitAxis, source: panes.PaneRect } = null,
    started_ms: u64 = 0,
    painted_ms: ?u64 = null,
    awaiting_paint: bool = false,
    transition_ms: u64 = duration_ms,
    pub const duration_ms = 160;
    pub const focus_duration_ms = 250;
    pub const split_duration_ms = 200;

    pub fn deinit(self: *LayoutMotion, allocator: std.mem.Allocator) void {
        self.previous.deinit(allocator);
        self.scratch.deinit(allocator);
    }
    pub fn clear(self: *LayoutMotion) void {
        self.previous.clearRetainingCapacity();
        self.opening = null;
        self.awaiting_paint = false;
        self.painted_ms = null;
    }
    pub fn notePaint(self: *LayoutMotion, now: u64) void {
        // Shell startup and the final ConPTY resize must not consume the reveal.
        if (self.awaiting_paint) self.started_ms = now;
        self.awaiting_paint = false;
        self.painted_ms = now;
    }
    pub fn active(self: *const LayoutMotion, now: u64) bool {
        return self.previous.items.len > 0 and now -| self.started_ms < self.transition_ms;
    }
    pub fn capture(self: *LayoutMotion, allocator: std.mem.Allocator, placements: []const panes.PanePlacement, now: u64, enabled: bool) !void {
        if (!enabled) {
            self.clear();
            return;
        }
        // Reserve first so allocation failure leaves both the old transition and the workspace intact.
        try self.scratch.ensureTotalCapacity(allocator, placements.len);
        self.scratch.clearRetainingCapacity();
        for (placements, 0..) |placement, index| {
            self.scratch.appendAssumeCapacity(.{ .id = placement.id, .bounds = self.bounds(placement, index, self.painted_ms orelse now, true) });
        }
        std.mem.swap(std.ArrayList(panes.PanePlacement), &self.previous, &self.scratch);
        self.started_ms = now;
        self.awaiting_paint = true;
        self.transition_ms = duration_ms;
        self.opening = null;
    }
    pub fn captureSplit(self: *LayoutMotion, allocator: std.mem.Allocator, placements: []const panes.PanePlacement, source: panes.PaneId, added: panes.PaneId, axis: panes.SplitAxis, now: u64, enabled: bool) !void {
        try self.capture(allocator, placements, now, enabled);
        if (!enabled) return;
        for (self.previous.items) |old| {
            if (old.id != source) continue;
            self.opening = .{ .id = added, .axis = axis, .source = old.bounds };
            self.transition_ms = split_duration_ms;
            break;
        }
    }
    pub fn captureFocus(self: *LayoutMotion, allocator: std.mem.Allocator, placements: []const panes.PanePlacement, now: u64, enabled: bool) !void {
        try self.capture(allocator, placements, now, enabled);
        self.transition_ms = focus_duration_ms;
    }
    /// Resolve IDs once at a layout change, not once per pane on every animation frame.
    /// This visual-only cache must never make an otherwise successful workspace edit fail.
    pub fn alignToLayout(self: *LayoutMotion, allocator: std.mem.Allocator, placements: []const panes.PanePlacement) void {
        if (self.previous.items.len == 0) return;
        self.scratch.ensureTotalCapacity(allocator, placements.len) catch {
            self.clear();
            return;
        };
        self.scratch.clearRetainingCapacity();
        var changed = false;
        for (placements) |placement| {
            var bounds_before = placement.bounds;
            for (self.previous.items) |old| {
                if (old.id == placement.id) {
                    bounds_before = old.bounds;
                    break;
                }
            }
            if (self.opening) |opening| {
                if (opening.id == placement.id) {
                    // Reveal from the source's outer edge; text keeps its final grid and font size.
                    bounds_before = opening.source;
                    switch (opening.axis) {
                        .right => {
                            bounds_before.x += bounds_before.width;
                            bounds_before.width = 0;
                        },
                        .down => {
                            bounds_before.y += bounds_before.height;
                            bounds_before.height = 0;
                        },
                    }
                }
            }
            changed = changed or !std.meta.eql(bounds_before, placement.bounds);
            self.scratch.appendAssumeCapacity(.{ .id = placement.id, .bounds = bounds_before });
        }
        std.mem.swap(std.ArrayList(panes.PanePlacement), &self.previous, &self.scratch);
        self.opening = null;
        // No-op drops and transitions with only new/removed panes need no animation timer.
        if (!changed) self.clear();
    }
    pub fn bounds(self: *const LayoutMotion, placement: panes.PanePlacement, index: usize, now: u64, enabled: bool) panes.PaneRect {
        if (!enabled or !self.active(now) or index >= self.previous.items.len) return placement.bounds;
        const old = self.previous.items[index];
        if (old.id != placement.id) return placement.bounds;
        const remaining = 1 - @as(f32, @floatFromInt(now -| self.started_ms)) / @as(f32, @floatFromInt(self.transition_ms));
        const weight = remaining * remaining * remaining;
        const final = placement.bounds;
        return .{
            .x = final.x + (old.bounds.x - final.x) * weight,
            .y = final.y + (old.bounds.y - final.y) * weight,
            .width = final.width + (old.bounds.width - final.width) * weight,
            .height = final.height + (old.bounds.height - final.height) * weight,
        };
    }
};

test "layout motion aligns reordered IDs once and retargets without warm allocations" {
    var motion: LayoutMotion = .{};
    defer motion.deinit(std.testing.allocator);
    const a: panes.PanePlacement = .{ .id = @enumFromInt(1), .bounds = .{ .x = 0, .y = 44, .width = 497, .height = 600 } };
    const b: panes.PanePlacement = .{ .id = @enumFromInt(2), .bounds = .{ .x = 503, .y = 44, .width = 497, .height = 600 } };
    const final = [_]panes.PanePlacement{ .{ .id = b.id, .bounds = a.bounds }, .{ .id = a.id, .bounds = b.bounds } };
    try motion.capture(std.testing.allocator, &.{ a, b }, 100, true);
    motion.alignToLayout(std.testing.allocator, &final);
    try std.testing.expectEqualDeep(b.bounds, motion.bounds(final[0], 0, 100, true));
    try std.testing.expectEqualDeep(a.bounds, motion.bounds(final[1], 1, 100, true));
    const middle = motion.bounds(final[0], 0, 180, true);
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{ .fail_index = 0 });
    try motion.capture(failing.allocator(), &final, 180, true);
    motion.alignToLayout(failing.allocator(), &final);
    try std.testing.expectEqualDeep(middle, motion.bounds(final[0], 0, 180, true));
    try std.testing.expectEqual(@as(usize, 0), failing.allocations);
    try std.testing.expectEqualDeep(final[0].bounds, motion.bounds(final[0], 1, 180, true));
}

test "focus expands the selected pane and reverses from its painted bounds" {
    var motion: LayoutMotion = .{};
    defer motion.deinit(std.testing.allocator);
    const left: panes.PanePlacement = .{ .id = @enumFromInt(1), .bounds = .{ .x = 0, .y = 44, .width = 497, .height = 600 } };
    const right: panes.PanePlacement = .{ .id = @enumFromInt(2), .bounds = .{ .x = 503, .y = 44, .width = 497, .height = 600 } };
    const zoomed: panes.PanePlacement = .{ .id = right.id, .bounds = .{ .x = 0, .y = 44, .width = 1000, .height = 600 } };
    try motion.captureFocus(std.testing.allocator, &.{ left, right }, 100, true);
    motion.alignToLayout(std.testing.allocator, &.{zoomed});
    try std.testing.expectEqualDeep(right.bounds, motion.bounds(zoomed, 0, 100, true));
    const middle = motion.bounds(zoomed, 0, 180, true);
    try std.testing.expect(middle.x < right.bounds.x and middle.x > zoomed.bounds.x);
    try std.testing.expect(middle.width > right.bounds.width and middle.width < zoomed.bounds.width);
    try motion.captureFocus(std.testing.allocator, &.{zoomed}, 180, true);
    motion.alignToLayout(std.testing.allocator, &.{ left, right });
    try std.testing.expectEqualDeep(left.bounds, motion.bounds(left, 0, 180, true));
    try std.testing.expectEqualDeep(middle, motion.bounds(right, 1, 180, true));
    try std.testing.expectEqual(@as(u64, 250), motion.transition_ms);
    var previous_width = middle.width;
    for (1..32) |step| {
        const width = motion.bounds(right, 1, 180 + step * 8, true).width;
        try std.testing.expect(width <= previous_width and width >= right.bounds.width);
        previous_width = width;
    }
    try std.testing.expectEqualDeep(right.bounds, motion.bounds(right, 1, 430, true));
    try std.testing.expect(!motion.active(430));
}

test "unchanged and reduced-motion layouts schedule no frames or allocations" {
    var motion: LayoutMotion = .{};
    defer motion.deinit(std.testing.allocator);
    const pane: panes.PanePlacement = .{ .id = @enumFromInt(1), .bounds = .{ .x = 0, .y = 44, .width = 800, .height = 600 } };
    try motion.capture(std.testing.allocator, &.{pane}, 100, true);
    motion.alignToLayout(std.testing.allocator, &.{pane});
    try std.testing.expect(!motion.active(100));
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{ .fail_index = 0 });
    try motion.capture(failing.allocator(), &.{pane}, 100, false);
    try std.testing.expect(!motion.active(100));
    try std.testing.expectEqual(@as(usize, 0), failing.allocations);
}

test "layout motion is bounded, identity-based and disabled by reduced motion" {
    var motion: LayoutMotion = .{};
    defer motion.deinit(std.testing.allocator);
    const old: panes.PanePlacement = .{ .id = @enumFromInt(1), .bounds = .{ .x = 0, .y = 44, .width = 1000, .height = 600 } };
    const final: panes.PanePlacement = .{ .id = old.id, .bounds = .{ .x = 503, .y = 44, .width = 497, .height = 600 } };
    try motion.capture(std.testing.allocator, &.{old}, 100, true);
    motion.alignToLayout(std.testing.allocator, &.{final});
    try std.testing.expectEqualDeep(old.bounds, motion.bounds(final, 0, 100, true));
    const middle = motion.bounds(final, 0, 180, true);
    try std.testing.expect(middle.x > old.bounds.x and middle.x < final.bounds.x);
    try std.testing.expectEqualDeep(final.bounds, motion.bounds(final, 0, 260, true));
    try std.testing.expectEqualDeep(final.bounds, motion.bounds(final, 0, 100, false));
    const other: panes.PanePlacement = .{ .id = @enumFromInt(2), .bounds = final.bounds };
    try std.testing.expectEqualDeep(final.bounds, motion.bounds(other, 0, 100, true));
    motion.clear();
    try std.testing.expect(!motion.active(100));
}

test "both split axes reveal continuously from the source edge and close from the painted layout" {
    for ([_]panes.SplitAxis{ .right, .down }) |axis| {
        var motion: LayoutMotion = .{};
        defer motion.deinit(std.testing.allocator);
        var tree = try panes.PaneTree.init(std.testing.allocator, @enumFromInt(1));
        defer tree.deinit();
        const container: panes.PaneRect = .{ .x = 20, .y = 60, .width = 1000, .height = 600 };
        const source: panes.PanePlacement = .{ .id = @enumFromInt(1), .bounds = container };
        var final: std.ArrayList(panes.PanePlacement) = .empty;
        defer final.deinit(std.testing.allocator);
        try tree.splitPane(source.id, @enumFromInt(2), axis);
        try tree.layout(std.testing.allocator, &final, container);
        try motion.captureSplit(std.testing.allocator, &.{source}, source.id, @enumFromInt(2), axis, 100, true);
        motion.alignToLayout(std.testing.allocator, final.items);
        // Even slow process startup/resize leaves the full animation for presentation.
        motion.notePaint(900);
        try std.testing.expectEqualDeep(container, motion.bounds(final.items[0], 0, 900, true));
        const edge = motion.bounds(final.items[1], 1, 900, true);
        try std.testing.expectEqual(@as(f32, 0), if (axis == .right) edge.width else edge.height);
        var previous_extent: f32 = 0;
        for (1..201) |step| {
            const first = motion.bounds(final.items[0], 0, 900 + step, true);
            const added = motion.bounds(final.items[1], 1, 900 + step, true);
            const extent = if (axis == .right) added.width else added.height;
            try std.testing.expect(extent >= previous_extent);
            try std.testing.expect(first.x + first.width <= added.x + 0.001 or first.y + first.height <= added.y + 0.001);
            previous_extent = extent;
        }
        motion.notePaint(940);
        const painted = motion.bounds(final.items[0], 0, 940, true);
        // Retarget to the last displayed frame even if handling the next action was slow.
        try motion.capture(std.testing.allocator, final.items, 980, true);
        motion.alignToLayout(std.testing.allocator, &.{source});
        motion.notePaint(1200);
        try std.testing.expectEqualDeep(painted, motion.bounds(source, 0, 1200, true));
        try std.testing.expectEqualDeep(container, motion.bounds(source, 0, 1360, true));
        try std.testing.expect(!motion.active(1360));
    }
}
