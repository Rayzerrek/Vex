const std = @import("std");

/// Per-pane activity; terminal output alone must never reveal the overlay.
pub const ScrollbarVisibility = struct {
    hide_at: ?u64 = null,
    pub const hold_ms = 900;
    pub const fade_ms = 220;

    pub fn reveal(self: *ScrollbarVisibility, now: u64) void {
        self.hide_at = now + hold_ms;
    }

    pub fn opacity(self: *ScrollbarVisibility, now: u64, available: bool, active: bool, animations: bool) f32 {
        if (!available) {
            self.hide_at = null;
            return 0;
        }
        if (active) self.reveal(now);
        const deadline = self.hide_at orelse return 0;
        if (now < deadline) return 1;
        if (!animations or now >= deadline + fade_ms) {
            self.hide_at = null;
            return 0;
        }
        const remaining: f32 = @floatFromInt(deadline + fade_ms - now);
        const t = remaining / fade_ms;
        return t * t * (3 - 2 * t);
    }

    pub fn nextDelayMs(self: ScrollbarVisibility, now: u64, active: bool) ?u32 {
        if (active) return null;
        const deadline = self.hide_at orelse return null;
        return if (now < deadline) @intCast(@min(deadline - now, hold_ms)) else 16;
    }
};

test "scrollbar stays hidden until interaction and fades after inactivity" {
    var state: ScrollbarVisibility = .{};
    try std.testing.expectEqual(@as(f32, 0), state.opacity(0, true, false, true));
    try std.testing.expectEqual(@as(?u32, null), state.nextDelayMs(0, false));
    state.reveal(100);
    try std.testing.expectEqual(@as(f32, 1), state.opacity(999, true, false, true));
    try std.testing.expectEqual(@as(?u32, 1), state.nextDelayMs(999, false));
    try std.testing.expectApproxEqAbs(@as(f32, 0.5), state.opacity(1110, true, false, true), 0.001);
    try std.testing.expectEqual(@as(f32, 0), state.opacity(1220, true, false, true));
    try std.testing.expectEqual(@as(?u32, null), state.nextDelayMs(1220, false));
}

test "hover and drag hold the thumb; no history and reduced motion reset cleanly" {
    var state: ScrollbarVisibility = .{};
    try std.testing.expectEqual(@as(f32, 1), state.opacity(0, true, true, true));
    try std.testing.expectEqual(@as(?u32, null), state.nextDelayMs(0, true));
    try std.testing.expectEqual(@as(f32, 1), state.opacity(5000, true, true, true));
    try std.testing.expectEqual(@as(f32, 1), state.opacity(5899, true, false, false));
    try std.testing.expectEqual(@as(f32, 0), state.opacity(5900, true, false, false));
    state.reveal(6000);
    try std.testing.expectEqual(@as(f32, 0), state.opacity(6000, false, true, true));
    try std.testing.expectEqual(@as(f32, 0), state.opacity(6001, true, false, true));
}
