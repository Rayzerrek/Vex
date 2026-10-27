const std = @import("std");
const Rect = @import("pane_tree.zig").PaneRect;

pub const Anchor = struct { column: u16, row: u16 };
pub const Layout = struct { bounds: Rect, visible_rows: usize };
pub const max_rows = 8;
pub const list_top: f32 = 56;
pub const row_height: f32 = 26;
const footer: f32 = 34;

/// An open picker owns its anchor. Search results and background TUI redraws must not move it.
pub fn compute(viewport: Rect, pane: Rect, cell_width: f32, cell_height: f32, anchor: ?Anchor) ?Layout {
    viewport.validate() catch return null;
    pane.validate() catch return null;
    if (!std.math.isFinite(cell_width) or !std.math.isFinite(cell_height) or cell_width <= 0 or cell_height <= 0) return null;
    const left = @max(viewport.x + 8, pane.x + 6);
    const top = @max(viewport.y + 8, pane.y + 30);
    const right = @min(viewport.x + viewport.width - 8, pane.x + pane.width - 6);
    const bottom = @min(viewport.y + viewport.height - 8, pane.y + pane.height - 6);
    const minimum_height = list_top + row_height + footer;
    if (right - left < 180 or bottom - top < minimum_height) return null;
    const width = @min(@as(f32, 420), right - left);
    const desired_height = list_top + row_height * max_rows + footer;
    var available = bottom - top;
    var x = left + (right - left - width) / 2;
    var y = top;
    var above = false;
    var cursor_top: f32 = top;
    if (anchor) |cell| {
        const cursor_x = pane.x + 6 + @as(f32, @floatFromInt(cell.column)) * cell_width;
        cursor_top = pane.y + 30 + @as(f32, @floatFromInt(cell.row)) * cell_height;
        const cursor_bottom = cursor_top + cell_height;
        // The popup's safety margin can clip the last grid row by a few pixels;
        // that does not make the terminal cursor invalid or justify centering.
        if (cursor_x < pane.x + 6 or cursor_x > pane.x + pane.width - 6 or
            cursor_top < pane.y + 30 or cursor_top >= pane.y + pane.height - 6) return null;
        x = std.math.clamp(cursor_x, left, right - width);
        const below_space = bottom - cursor_bottom - 4;
        const above_space = cursor_top - top - 4;
        above = below_space < desired_height and above_space > below_space;
        available = if (above) above_space else below_space;
        // Very small panes cannot fit a picker beside the input; do not cover the caret.
        if (available < minimum_height) return null;
        y = cursor_bottom + 4;
    }
    const rows: usize = @intFromFloat(@floor((@min(available, desired_height) - list_top - footer) / row_height));
    const height = list_top + @as(f32, @floatFromInt(rows)) * row_height + footer;
    if (above) y = cursor_top - height - 4;
    return .{ .bounds = .{ .x = x, .y = y, .width = width, .height = height }, .visible_rows = rows };
}

const screen: Rect = .{ .x = 0, .y = 0, .width = 1200, .height = 800 };
const test_pane: Rect = .{ .x = 400, .y = 200, .width = 600, .height = 560 };

test "completion anchors to a TUI input in an offset pane, below or above without covering it" {
    const below = compute(screen, test_pane, 8, 18, .{ .column = 20, .row = 2 }).?;
    try std.testing.expectEqual(@as(f32, 566), below.bounds.x);
    try std.testing.expectEqual(@as(f32, 288), below.bounds.y);
    try std.testing.expectEqual(@as(usize, 8), below.visible_rows);
    const above = compute(screen, test_pane, 8, 18, .{ .column = 60, .row = 27 }).?;
    try std.testing.expect(above.bounds.y + above.bounds.height <= 200 + 30 + 27 * 18 - 4);
    try std.testing.expect(above.bounds.x + above.bounds.width <= test_pane.x + test_pane.width - 6);
}

test "last grid row anchors above even when it crosses the popup safety margin" {
    const viewport: Rect = .{ .x = 0, .y = 0, .width = 800, .height = 596 };
    const pane: Rect = .{ .x = 0, .y = 0, .width = 800, .height = 596 };
    const popup = compute(viewport, pane, 8, 16, .{ .column = 4, .row = 34 }).?;
    try std.testing.expectEqual(@as(f32, 38), popup.bounds.x);
    try std.testing.expect(popup.bounds.y + popup.bounds.height <= 574 - 4);
}

test "unavailable cursor uses a pane-local picker, not a fabricated top-left input" {
    const popup = compute(screen, test_pane, 8, 18, null).?;
    try std.testing.expectEqual(@as(f32, 490), popup.bounds.x);
    try std.testing.expectEqual(@as(f32, 230), popup.bounds.y);
    try std.testing.expectEqual(@as(usize, 8), popup.visible_rows);
}

test "completion reduces visible rows in short splits and rejects unusable geometry" {
    const short: Rect = .{ .x = 300, .y = 400, .width = 320, .height = 280 };
    const popup = compute(screen, short, 8, 18, .{ .column = 4, .row = 1 }).?;
    try std.testing.expect(popup.visible_rows > 0 and popup.visible_rows < max_rows);
    try std.testing.expect(popup.bounds.y + popup.bounds.height <= short.y + short.height - 6);
    try std.testing.expect(compute(screen, .{ .x = 0, .y = 44, .width = 160, .height = 400 }, 8, 18, null) == null);
    try std.testing.expect(compute(screen, test_pane, std.math.nan(f32), 18, null) == null);
    try std.testing.expect(compute(screen, .{ .x = 1400, .y = 200, .width = 600, .height = 560 }, 8, 18, null) == null);
}

test "completion uses DIP metrics without scaling offsets twice" {
    const popup = compute(screen, test_pane, 9.5, 21.5, .{ .column = 10, .row = 3 }).?;
    try std.testing.expectEqual(@as(f32, 501), popup.bounds.x);
    try std.testing.expectEqual(@as(f32, 320), popup.bounds.y);
}
