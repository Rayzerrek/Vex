const std = @import("std");
const panes = @import("pane_tree.zig");

pub const Direction = enum { left, right, up, down, next, previous };

/// Layout order is depth-first (left before right, top before bottom). Directional
/// movement only crosses an edge with overlapping projection, never jumps diagonally.
pub fn target(placements: []const panes.PanePlacement, active: panes.PaneId, direction: Direction) ?panes.PaneId {
    if (placements.len < 2) return null;
    var index: usize = 0;
    for (placements, 0..) |placement, i| {
        if (placement.id == active) {
            index = i;
            break;
        }
    } else return null;
    if (direction == .next) return placements[(index + 1) % placements.len].id;
    if (direction == .previous) return placements[(index + placements.len - 1) % placements.len].id;
    const source = placements[index].bounds;
    const horizontal = direction == .left or direction == .right;
    var best: ?panes.PaneId = null;
    var best_gap = std.math.inf(f32);
    var best_offset = std.math.inf(f32);
    for (placements) |placement| {
        if (placement.id == active) continue;
        const rect = placement.bounds;
        if (rect.width <= 0 or rect.height <= 0) continue;
        const overlap = if (horizontal)
            @min(source.y + source.height, rect.y + rect.height) - @max(source.y, rect.y)
        else
            @min(source.x + source.width, rect.x + rect.width) - @max(source.x, rect.x);
        if (overlap <= 0) continue;
        const gap = switch (direction) {
            .left => source.x - (rect.x + rect.width),
            .right => rect.x - (source.x + source.width),
            .up => source.y - (rect.y + rect.height),
            .down => rect.y - (source.y + source.height),
            else => unreachable,
        };
        if (gap < 0) continue;
        const offset = if (horizontal)
            @abs((rect.y + rect.height / 2) - (source.y + source.height / 2))
        else
            @abs((rect.x + rect.width / 2) - (source.x + source.width / 2));
        if (gap < best_gap or (gap == best_gap and offset < best_offset)) {
            best = placement.id;
            best_gap = gap;
            best_offset = offset;
        }
    }
    return best;
}

test "directional navigation follows nested geometry and stops at outer edges" {
    var tree = try panes.PaneTree.init(std.testing.allocator, @enumFromInt(1));
    defer tree.deinit();
    try tree.splitPane(@enumFromInt(1), @enumFromInt(2), .right);
    try tree.splitPane(@enumFromInt(2), @enumFromInt(3), .down);
    var placements: std.ArrayList(panes.PanePlacement) = .empty;
    defer placements.deinit(std.testing.allocator);
    try tree.layout(std.testing.allocator, &placements, .{ .x = 40, .y = 80, .width = 1006, .height = 606 });
    const cases = .{
        .{ 1, Direction.right, 2 },    .{ 2, Direction.down, 3 },
        .{ 3, Direction.up, 2 },       .{ 3, Direction.left, 1 },
        .{ 1, Direction.left, 0 },     .{ 2, Direction.right, 0 },
        .{ 1, Direction.up, 0 },       .{ 3, Direction.down, 0 },
        .{ 1, Direction.next, 2 },     .{ 3, Direction.next, 1 },
        .{ 1, Direction.previous, 3 }, .{ 3, Direction.previous, 2 },
    };
    inline for (cases) |case| {
        const expected: ?panes.PaneId = if (case[2] == 0) null else @enumFromInt(case[2]);
        try std.testing.expectEqual(expected, target(placements.items, @enumFromInt(case[0]), case[1]));
    }
    try std.testing.expectEqual(@as(?panes.PaneId, null), target(placements.items, @enumFromInt(99), .next));
    try std.testing.expectEqual(@as(?panes.PaneId, null), target(placements.items[0..1], @enumFromInt(1), .next));
    try std.testing.expectEqual(@as(?panes.PaneId, null), target(&.{}, @enumFromInt(1), .left));
}

test "directional navigation prefers the nearest aligned center and rejects diagonal panes" {
    const placements = [_]panes.PanePlacement{
        .{ .id = @enumFromInt(1), .bounds = .{ .x = 0, .y = 120, .width = 100, .height = 80 } },
        .{ .id = @enumFromInt(2), .bounds = .{ .x = 106, .y = 0, .width = 100, .height = 114 } },
        .{ .id = @enumFromInt(3), .bounds = .{ .x = 106, .y = 120, .width = 100, .height = 20 } },
        .{ .id = @enumFromInt(4), .bounds = .{ .x = 106, .y = 146, .width = 100, .height = 54 } },
    };
    try std.testing.expectEqual(@as(?panes.PaneId, @enumFromInt(4)), target(&placements, @enumFromInt(1), .right));
    try std.testing.expectEqual(@as(?panes.PaneId, null), target(placements[0..2], @enumFromInt(1), .right));
}
