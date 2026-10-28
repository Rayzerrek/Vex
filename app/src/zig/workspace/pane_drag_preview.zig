const std = @import("std");
const panes = @import("pane_tree.zig");
const strip = @import("tab_strip.zig");

/// The grab anchor stays under the pointer; pointer tracking itself is never eased.
pub fn bounds(source: panes.PaneRect, start: strip.TabPointer, point: strip.TabPointer, width: f32, height: f32) panes.PaneRect {
    const w = @min(200, @max(1, width - 8));
    const h = @min(34, @max(1, height - 8));
    const anchor = std.math.clamp((start.x - source.x) / @max(1, source.width), 0, 1);
    // Caption buttons are painted above workspace overlays. Keep the card below
    // them while the pointer is on the tab bar, including the new-tab drop target.
    const top = @min(strip.TabStripLayout.height + 4, @max(4, height - h - 4));
    return .{
        .x = std.math.clamp(point.x - anchor * w, 4, @max(4, width - w - 4)),
        .y = std.math.clamp(point.y - (start.y - source.y), top, @max(top, height - h - 4)),
        .width = w,
        .height = h,
    };
}

/// Empty caption space and the plus button share one explicit new-tab drop target.
pub fn detachTarget(layout: strip.TabStripLayout, point: strip.TabPointer, width: f32) ?panes.PaneRect {
    if (!std.math.isFinite(point.x) or !std.math.isFinite(point.y)) return null;
    const right = width - 120;
    if (point.y < 0 or point.y >= strip.TabStripLayout.height or point.x < layout.plusLeft() or point.x >= right) return null;
    return .{ .x = layout.plusLeft(), .y = 5, .width = @min(200, right - layout.plusLeft()), .height = 34 };
}

test "pane ghost preserves its grab anchor, tracks directly and fits resized clients" {
    const source: panes.PaneRect = .{ .x = 500, .y = 50, .width = 500, .height = 600 };
    const start: strip.TabPointer = .{ .x = 750, .y = 62 };
    const first = bounds(source, start, .{ .x = 750, .y = 90 }, 1120, 700);
    try std.testing.expectEqual(@as(f32, 750), first.x + first.width / 2);
    const moved = bounds(source, start, .{ .x = 760, .y = 95 }, 1120, 700);
    try std.testing.expectEqual(first.x + 10, moved.x);
    try std.testing.expectEqual(first.y + 5, moved.y);
    const caption = bounds(source, start, .{ .x = 1100, .y = 20 }, 1120, 700);
    try std.testing.expect(caption.y >= strip.TabStripLayout.height + 4);
    const small = bounds(source, start, .{ .x = 2000, .y = -500 }, 180, 120);
    try std.testing.expect(small.x >= 4 and small.x + small.width <= 176);
    try std.testing.expect(small.y >= 4 and small.y + small.height <= 116);
}

test "new tab target accepts empty caption space but excludes tabs and window controls" {
    const layout = try strip.TabStripLayout.init(1120, 2);
    for ([_]f32{ layout.plusLeft(), layout.plusLeft() + 10, 950 }) |x| {
        try std.testing.expect(detachTarget(layout, .{ .x = x, .y = 20 }, 1120) != null);
    }
    for ([_]strip.TabPointer{ .{ .x = 100, .y = 20 }, .{ .x = 1000, .y = 20 }, .{ .x = 800, .y = 44 }, .{ .x = 800, .y = -1 }, .{ .x = std.math.nan(f32), .y = 20 } }) |point| {
        try std.testing.expect(detachTarget(layout, point, 1120) == null);
    }
}
