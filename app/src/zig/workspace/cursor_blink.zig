const std = @import("std");
const panes = @import("pane_tree.zig");
const terminal = @import("../terminal/terminal.zig");

/// Cursor identity contains values only, so a timer never retains a closed session or snapshot.
pub const CursorKey = struct {
    pane: panes.PaneId,
    column: u16,
    row: u16,
    shape: @FieldType(terminal.TerminalCursor, "shape"),
    wide_tail: bool,

    /// Hidden, steady and out-of-viewport cursors cannot arm the blink scheduler.
    pub fn fromFrame(pane: panes.PaneId, frame: *const terminal.TerminalFrame) ?CursorKey {
        const cursor = frame.cursor;
        if (!cursor.visible or !cursor.blinking or cursor.column >= frame.columns or cursor.row >= frame.rows) return null;
        return .{ .pane = pane, .column = cursor.column, .row = cursor.row, .shape = cursor.shape, .wide_tail = cursor.wide_tail };
    }
};

/// Allocation-free cursor blink scheduling; timestamps are monotonic milliseconds, never wall clock.
pub const CursorBlink = struct {
    state: union(enum) {
        stopped,
        blinking: struct { key: CursorKey, visible: bool, next_ms: u64 },
    } = .stopped,
    pub const period_ms: u64 = 530;

    /// Repainting the same cursor preserves its phase; focus/position/style changes reveal a fresh cursor.
    pub fn synchronize(self: *CursorBlink, key: ?CursorKey, now_ms: u64) void {
        const next_key = key orelse {
            self.state = .stopped;
            return;
        };
        if (self.state == .blinking and std.meta.eql(self.state.blinking.key, next_key)) return;
        self.state = .{ .blinking = .{ .key = next_key, .visible = true, .next_ms = now_ms + period_ms } };
    }

    /// User input reveals the cursor immediately and restarts its quiet interval.
    pub fn resetOnInput(self: *CursorBlink, now_ms: u64) bool {
        if (self.state != .blinking) return false;
        const changed = !self.state.blinking.visible;
        self.state.blinking.visible = true;
        self.state.blinking.next_ms = now_ms + period_ms;
        return changed;
    }

    /// Early/stale wakes do nothing; delayed wakes skip missed periods without a catch-up repaint loop.
    pub fn advance(self: *CursorBlink, now_ms: u64) bool {
        if (self.state != .blinking or now_ms < self.state.blinking.next_ms) return false;
        const elapsed = now_ms - self.state.blinking.next_ms;
        const changed = (elapsed / period_ms) % 2 == 0;
        if (changed) self.state.blinking.visible = !self.state.blinking.visible;
        self.state.blinking.next_ms = now_ms + period_ms - elapsed % period_ms;
        return changed;
    }

    /// A stopped scheduler leaves a steady cursor visible; terminal visibility is checked separately.
    pub fn isShown(self: *const CursorBlink) bool {
        return switch (self.state) {
            .stopped => true,
            .blinking => |state| state.visible,
        };
    }

    /// The window arms one wake only while a cursor is eligible, with a positive delay even when overdue.
    pub fn nextDelayMs(self: *const CursorBlink, now_ms: u64) ?u32 {
        return switch (self.state) {
            .stopped => null,
            .blinking => |state| @intCast(@max(1, state.next_ms -| now_ms)),
        };
    }
};

const test_key: CursorKey = .{ .pane = @enumFromInt(1), .column = 3, .row = 2, .shape = .bar, .wide_tail = false };

test "cursor blink preserves repaint phase and resets on input, pane, position and style changes" {
    var blink: CursorBlink = .{};
    try std.testing.expectEqual(@as(?u32, null), blink.nextDelayMs(0));
    blink.synchronize(test_key, 100);
    try std.testing.expectEqual(@as(?u32, 530), blink.nextDelayMs(100));
    try std.testing.expect(!blink.advance(629));
    try std.testing.expect(blink.advance(630));
    try std.testing.expect(!blink.isShown());
    blink.synchronize(test_key, 650);
    try std.testing.expect(!blink.isShown());
    try std.testing.expect(blink.resetOnInput(700));
    try std.testing.expect(blink.isShown());
    try std.testing.expect(!blink.advance(1160));
    try std.testing.expect(blink.advance(1230));
    var next = test_key;
    next.pane = @enumFromInt(2);
    blink.synchronize(next, 1300);
    try std.testing.expect(blink.isShown());
    try std.testing.expect(blink.advance(1830));
    next.column += 1;
    blink.synchronize(next, 1900);
    try std.testing.expect(blink.isShown());
    try std.testing.expect(blink.advance(2430));
    next.shape = .block;
    blink.synchronize(next, 2500);
    try std.testing.expect(blink.isShown());
}

test "cursor blink catches up once and stale wakes cannot rearm a stopped timer" {
    var blink: CursorBlink = .{};
    blink.synchronize(test_key, 0);
    try std.testing.expect(!blink.advance(1060));
    try std.testing.expect(blink.isShown());
    try std.testing.expectEqual(@as(?u32, 530), blink.nextDelayMs(1060));
    try std.testing.expect(blink.advance(2700));
    try std.testing.expect(!blink.isShown());
    try std.testing.expectEqual(@as(?u32, 480), blink.nextDelayMs(2700));
    blink.synchronize(null, 2800);
    try std.testing.expect(blink.isShown());
    try std.testing.expect(!blink.advance(10_000));
    try std.testing.expectEqual(@as(?u32, null), blink.nextDelayMs(10_000));
}

test "cursor eligibility rejects hidden, steady and out-of-viewport cursors" {
    var frame: terminal.TerminalFrame = .{ .columns = 80, .rows = 24, .cursor = .{ .visible = true, .blinking = true } };
    try std.testing.expect(CursorKey.fromFrame(@enumFromInt(1), &frame) != null);
    frame.cursor.visible = false;
    try std.testing.expectEqual(@as(?CursorKey, null), CursorKey.fromFrame(@enumFromInt(1), &frame));
    frame.cursor.visible = true;
    frame.cursor.blinking = false;
    try std.testing.expectEqual(@as(?CursorKey, null), CursorKey.fromFrame(@enumFromInt(1), &frame));
    frame.cursor.blinking = true;
    frame.cursor.row = 24;
    try std.testing.expectEqual(@as(?CursorKey, null), CursorKey.fromFrame(@enumFromInt(1), &frame));
}
