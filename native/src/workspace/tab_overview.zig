const std = @import("std");
const PaneRect = @import("pane_tree.zig").PaneRect;

/// The workspace caps tabs at eight, so every tab fits in one bounded overview.
pub const Layout = struct {
    bounds: PaneRect,
    columns: usize,
    count: usize,

    pub fn init(width: f32, height: f32, total: usize) Layout {
        const available_width = @max(1, width - 56);
        const available_height = @max(1, height - 154);
        const preferred_width = available_width * 0.6;
        const preferred_height = available_height * 0.6;
        var columns: usize = 1;
        var best_scale: f32 = -std.math.inf(f32);
        // Maximize the readable terminal area, rather than picking columns from a width breakpoint.
        for (1..@min(4, @max(1, total)) + 1) |candidate| {
            const rows = @max(1, (total + candidate - 1) / candidate);
            const w = (preferred_width - 16 * @as(f32, @floatFromInt(candidate - 1))) / @as(f32, @floatFromInt(candidate));
            const h = (preferred_height - 16 * @as(f32, @floatFromInt(rows - 1))) / @as(f32, @floatFromInt(rows));
            const scale = @min((w - 12) / 240, (h - 36) / 135);
            if (scale > best_scale) {
                best_scale = scale;
                columns = candidate;
            }
        }
        const rows = @max(1, (total + columns - 1) / columns);
        // Prefer the compact 3/5 presentation, but grow before sacrificing controls on small windows.
        const grid_width = @min(available_width, @max(preferred_width, @as(f32, @floatFromInt(columns)) * 144 + @as(f32, @floatFromInt(columns - 1)) * 16));
        const grid_height = @min(available_height, @max(preferred_height, @as(f32, @floatFromInt(rows)) * 88 + @as(f32, @floatFromInt(rows - 1)) * 16));
        return .{
            .bounds = .{ .x = 28 + (available_width - grid_width) * 0.5, .y = 98 + (available_height - grid_height) * 0.5, .width = grid_width, .height = grid_height },
            .columns = columns,
            .count = total,
        };
    }
    pub fn card(self: Layout, index: usize) PaneRect {
        const rows = @max(1, (self.count + self.columns - 1) / self.columns);
        const w = @max(1, (self.bounds.width - 16 * @as(f32, @floatFromInt(self.columns - 1))) / @as(f32, @floatFromInt(self.columns)));
        const h = @max(1, (self.bounds.height - 16 * @as(f32, @floatFromInt(rows - 1))) / @as(f32, @floatFromInt(rows)));
        const row = index / self.columns;
        const row_count = @min(self.columns, self.count - row * self.columns);
        const inset = @as(f32, @floatFromInt(self.columns - row_count)) * (w + 16) * 0.5;
        return .{ .x = self.bounds.x + inset + @as(f32, @floatFromInt(index % self.columns)) * (w + 16), .y = self.bounds.y + @as(f32, @floatFromInt(row)) * (h + 16), .width = w, .height = h };
    }
    pub fn verticalNeighbor(self: Layout, index: usize, down: bool) usize {
        const row = index / self.columns;
        const rows = (self.count + self.columns - 1) / self.columns;
        if ((!down and row == 0) or (down and row + 1 >= rows)) return index;
        const target_row = if (down) row + 1 else row - 1;
        const source = self.card(index);
        const center = source.x + source.width * 0.5;
        var nearest = target_row * self.columns;
        var distance: f32 = std.math.inf(f32);
        for (target_row * self.columns..@min(self.count, (target_row + 1) * self.columns)) |candidate| {
            const bounds = self.card(candidate);
            const delta = @abs(bounds.x + bounds.width * 0.5 - center);
            if (delta < distance) {
                distance = delta;
                nearest = candidate;
            }
        }
        return nearest;
    }
    pub fn hit(self: Layout, x: f32, y: f32) ?usize {
        for (0..self.count) |i| if (self.card(i).contains(x, y)) return i;
        return null;
    }
};

pub const CloseShortcut = enum { tab, active_pane };

pub fn closeShortcut(key: u32, modifiers: u32) ?CloseShortcut {
    if (key == 0x2E and modifiers == 0) return .tab;
    if (key == 'W' and modifiers == 3) return .active_pane;
    return null;
}

pub fn content(bounds: PaneRect) PaneRect {
    return .{ .x = bounds.x + 6, .y = bounds.y + 30, .width = @max(1, bounds.width - 12), .height = @max(1, bounds.height - 36) };
}

/// Identical DIP hit and paint bounds; compact split previews omit inaccessible controls.
pub fn closeButton(bounds: PaneRect) ?PaneRect {
    if (bounds.width < 48 or bounds.height < 28) return null;
    return .{ .x = bounds.x + bounds.width - 28, .y = bounds.y + 2, .width = 24, .height = 24 };
}

pub fn interpolate(from: PaneRect, to: PaneRect, progress: f32) PaneRect {
    const t = 1 - std.math.pow(f32, 1 - std.math.clamp(progress, 0, 1), 3);
    return .{ .x = from.x + (to.x - from.x) * t, .y = from.y + (to.y - from.y) * t, .width = from.width + (to.width - from.width) * t, .height = from.height + (to.height - from.height) * t };
}

test "overview shows every tab with usable targets, no overlaps and centered rows" {
    for ([_][2]f32{ .{ 640, 400 }, .{ 800, 1200 }, .{ 1280, 800 }, .{ 2560, 1440 } }) |size| {
        for (1..9) |total| {
            const layout = Layout.init(size[0], size[1], total);
            try std.testing.expectEqual(total, layout.count);
            try std.testing.expect(layout.columns <= total);
            try std.testing.expect(layout.bounds.width <= size[0] - 56);
            try std.testing.expect(layout.bounds.height <= size[1] - 154);
            try std.testing.expectApproxEqAbs(size[0] * 0.5, layout.bounds.x + layout.bounds.width * 0.5, 0.001);
            try std.testing.expectApproxEqAbs(98 + (size[1] - 154) * 0.5, layout.bounds.y + layout.bounds.height * 0.5, 0.001);
            for (0..total) |i| {
                const card = layout.card(i);
                try std.testing.expect(card.width >= 100 and card.height >= 64);
                try std.testing.expect(card.x >= layout.bounds.x and card.y >= layout.bounds.y);
                try std.testing.expect(card.x + card.width <= layout.bounds.x + layout.bounds.width + 0.001);
                try std.testing.expect(card.y + card.height <= layout.bounds.y + layout.bounds.height + 0.001);
                try std.testing.expectEqual(@as(?usize, i), layout.hit(card.x + card.width * 0.5, card.y + card.height * 0.5));
                const button = closeButton(card).?;
                try std.testing.expectEqual(@as(?usize, i), layout.hit(button.x + 12, button.y + 12));
                for (i + 1..total) |j| {
                    const other = layout.card(j);
                    try std.testing.expect(card.x + card.width <= other.x or other.x + other.width <= card.x or card.y + card.height <= other.y or other.y + other.height <= card.y);
                }
            }
            const last_row = (total - 1) / layout.columns * layout.columns;
            const first_card = layout.card(last_row);
            const last_card = layout.card(total - 1);
            try std.testing.expectApproxEqAbs(size[0] * 0.5, (first_card.x + last_card.x + last_card.width) * 0.5, 0.001);
            try std.testing.expectEqual(@as(?usize, null), layout.hit(0, 0));
            for (0..total) |i| {
                const up = layout.verticalNeighbor(i, false);
                const down = layout.verticalNeighbor(i, true);
                try std.testing.expect(up < total and down < total);
                if (i / layout.columns > 0) try std.testing.expectEqual(i / layout.columns - 1, up / layout.columns) else try std.testing.expectEqual(i, up);
                if (i / layout.columns + 1 < (total + layout.columns - 1) / layout.columns) try std.testing.expectEqual(i / layout.columns + 1, down / layout.columns) else try std.testing.expectEqual(i, down);
            }
        }
    }
}

test "seven tabs remain together at equal size and compact layouts prefer three-fifths" {
    const layout = Layout.init(1280, 800, 7);
    try std.testing.expectEqual(@as(usize, 7), layout.count);
    for (1..7) |i| {
        try std.testing.expectEqual(layout.card(0).width, layout.card(i).width);
        try std.testing.expectEqual(layout.card(0).height, layout.card(i).height);
    }
    const compact = Layout.init(1280, 800, 2);
    try std.testing.expectApproxEqAbs((1280.0 - 56) * 0.6, compact.bounds.width, 0.001);
    try std.testing.expectApproxEqAbs((800.0 - 154) * 0.6, compact.bounds.height, 0.001);
    const a = compact.card(0);
    const b = compact.card(1);
    try std.testing.expectEqual(a, interpolate(a, b, 0));
    try std.testing.expectEqual(b, interpolate(a, b, 1));
}

test "overview close shortcuts require exact modifiers" {
    try std.testing.expectEqual(@as(?CloseShortcut, .tab), closeShortcut(0x2E, 0));
    try std.testing.expectEqual(@as(?CloseShortcut, .active_pane), closeShortcut('W', 3));
    for ([_]u32{ 1, 2, 3, 4, 7 }) |modifiers| {
        try std.testing.expectEqual(@as(?CloseShortcut, null), closeShortcut(0x2E, modifiers));
    }
    for ([_]u32{ 0, 1, 2, 4, 7 }) |modifiers| {
        try std.testing.expectEqual(@as(?CloseShortcut, null), closeShortcut('W', modifiers));
    }
    try std.testing.expectEqual(@as(?CloseShortcut, null), closeShortcut(0x0D, 0));
}

test "close controls reserve a separate header target and omit undersized splits" {
    const bounds: PaneRect = .{ .x = 120, .y = 80, .width = 300, .height = 160 };
    const button = closeButton(bounds).?;
    try std.testing.expect(button.contains(404, 94));
    try std.testing.expect(!button.contains(200, 94));
    try std.testing.expect(!content(bounds).contains(button.x + 12, button.y + 12));
    try std.testing.expect(closeButton(.{ .x = 0, .y = 0, .width = 47, .height = 100 }) == null);
    try std.testing.expect(closeButton(.{ .x = 0, .y = 0, .width = 100, .height = 27 }) == null);
}
