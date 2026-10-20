const std = @import("std");

/// Monotonic, allocation-free motion shared by visible status indicators.
pub const StatusPulse = struct {
    state: union(enum) {
        stopped,
        pulsing: struct { next_ms: u64, origin_ms: u64 },
    } = .stopped,
    phase: u32 = 0,

    pub const step_ms: u64 = 16;
    /// Millisecond phase, not a frame counter: delayed paints never slow the wave.
    pub const steps_per_cycle: u32 = 1200;

    pub fn synchronize(self: *StatusPulse, active: bool, now_ms: u64) void {
        if (!active) {
            self.state = .stopped;
            self.phase = 0;
            return;
        }
        if (self.state != .pulsing) self.state = .{ .pulsing = .{ .next_ms = now_ms + step_ms, .origin_ms = now_ms } };
    }
    pub fn advance(self: *StatusPulse, now_ms: u64) bool {
        if (self.state != .pulsing or now_ms < self.state.pulsing.next_ms) return false;
        const elapsed = now_ms - self.state.pulsing.origin_ms;
        self.phase = @intCast(elapsed % steps_per_cycle);
        self.state.pulsing.next_ms = now_ms + step_ms - elapsed % step_ms;
        return true;
    }
    pub fn phaseIndex(self: *const StatusPulse) u32 {
        return self.phase;
    }
    pub fn nextDelayMs(self: *const StatusPulse, now_ms: u64) ?u32 {
        return switch (self.state) {
            .stopped => null,
            .pulsing => |state| @intCast(@max(1, state.next_ms -| now_ms)),
        };
    }
    pub fn animates(state: u32) bool {
        return state == 2 or state == 4;
    }
};

test "status motion follows elapsed time and skips missed frames without catch-up" {
    var pulse: StatusPulse = .{};
    try std.testing.expectEqual(@as(?u32, null), pulse.nextDelayMs(0));
    pulse.synchronize(true, 100);
    try std.testing.expectEqual(@as(?u32, 16), pulse.nextDelayMs(100));
    try std.testing.expect(!pulse.advance(115));
    try std.testing.expect(pulse.advance(116));
    try std.testing.expectEqual(@as(u32, 16), pulse.phaseIndex());
    pulse.synchronize(true, 200);
    try std.testing.expect(pulse.advance(1100));
    try std.testing.expectEqual(@as(u32, 1000), pulse.phaseIndex());
    try std.testing.expectEqual(@as(?u32, 8), pulse.nextDelayMs(1100));
    try std.testing.expect(!pulse.advance(1100));
    try std.testing.expect(pulse.advance(1300));
    try std.testing.expectEqual(@as(u32, 0), pulse.phaseIndex());
    pulse.synchronize(false, 1400);
    try std.testing.expectEqual(@as(?u32, null), pulse.nextDelayMs(1400));
    try std.testing.expectEqual(@as(u32, 0), pulse.phaseIndex());
    try std.testing.expect(!pulse.advance(3000));
    pulse.synchronize(true, 4000);
    try std.testing.expect(pulse.advance(4016));
    try std.testing.expectEqual(@as(u32, 16), pulse.phaseIndex());
    try std.testing.expect(StatusPulse.animates(2) and StatusPulse.animates(4));
    for ([_]u32{ 0, 1, 3, 5, 9 }) |state| try std.testing.expect(!StatusPulse.animates(state));
}
