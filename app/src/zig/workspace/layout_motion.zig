const std = @import("std");
const panes = @import("pane_tree.zig");

/// Paint-only layout interpolation. The PTY is resized once to its final grid, never once per frame.
/// Timing matches native ChromeMotion; stable IDs prevent a removed pane animating a replacement.
pub const LayoutMotion = struct {
    previous: std.ArrayList(panes.PanePlacement) = .empty,
    scratch: std.ArrayList(panes.PanePlacement) = .empty,
    started_ms: u64 = 0,
    pub const duration_ms = 160;

    pub fn deinit(self: *LayoutMotion, allocator: std.mem.Allocator) void {
        self.previous.deinit(allocator);
        self.scratch.deinit(allocator);
    }
    pub fn clear(self: *LayoutMotion) void {
        self.previous.clearRetainingCapacity();
    }
    pub fn active(self: *const LayoutMotion, now: u64) bool {
        return self.previous.items.len > 0 and now -| self.started_ms < duration_ms;
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
            self.scratch.appendAssumeCapacity(.{ .id = placement.id, .bounds = self.bounds(placement, index, now, true) });
        }
        std.mem.swap(std.ArrayList(panes.PanePlacement), &self.previous, &self.scratch);
        self.started_ms = now;
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
            changed = changed or !std.meta.eql(bounds_before, placement.bounds);
            self.scratch.appendAssumeCapacity(.{ .id = placement.id, .bounds = bounds_before });
        }
        std.mem.swap(std.ArrayList(panes.PanePlacement), &self.previous, &self.scratch);
        // No-op drops and transitions with only new/removed panes need no animation timer.
        if (!changed) self.clear();
    }
    pub fn bounds(self: *const LayoutMotion, placement: panes.PanePlacement, index: usize, now: u64, enabled: bool) panes.PaneRect {
        if (!enabled or !self.active(now) or index >= self.previous.items.len) return placement.bounds;
        const old = self.previous.items[index];
        if (old.id != placement.id) return placement.bounds;
        const remaining = 1 - @as(f32, @floatFromInt(now -| self.started_ms)) / duration_ms;
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
