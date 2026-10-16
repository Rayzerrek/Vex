const std = @import("std");

/// Allocation-free program-status icon animation scheduling; timestamps are monotonic milliseconds, never wall clock.
pub const StatusPulse = struct {
    state: union(enum) {
        stopped,
        pulsing: struct { next_ms: u64 },
    } = .stopped,
    /// Advances once per wave step; the native drawing derives its look from the phase alone.
    phase: u32 = 0,

    /// One dot-wave step; `steps_per_cycle` steps complete the native icon animation cycle.
    pub const step_ms: u64 = 300;
    /// Native icons normalize their wave from the current phase index.
    pub const steps_per_cycle: u32 = 3;

    /// Overlays, rename editing and inactive windows stop animation; a stopped pulse draws static icons.
    pub fn synchronize(self: *StatusPulse, active: bool, now_ms: u64) void {
        if (!active) {
            self.state = .stopped;
            return;
        }
        if (self.state != .pulsing) self.state = .{ .pulsing = .{ .next_ms = now_ms + step_ms } };
    }
    /// Early/stale wakes never repaint; a mature wake advances one wave step without catch-up loops.
    pub fn advance(self: *StatusPulse, now_ms: u64) bool {
        if (self.state != .pulsing or now_ms < self.state.pulsing.next_ms) return false;
        const elapsed = now_ms - self.state.pulsing.next_ms;
        self.state.pulsing.next_ms = now_ms + step_ms - elapsed % step_ms;
        self.phase +%= 1;
        return true;
    }
    /// Bounded wave index handed to the native drawing; static states ignore it.
    pub fn phaseIndex(self: *const StatusPulse) u32 {
        return self.phase % steps_per_cycle;
    }
    /// The window arms one shared wake; a stopped pulse never keeps the timer alive on its own.
    pub fn nextDelayMs(self: *const StatusPulse, now_ms: u64) ?u32 {
        return switch (self.state) {
            .stopped => null,
            .pulsing => |state| @intCast(@max(1, state.next_ms -| now_ms)),
        };
    }
    /// Whether a status state uses animated icon phases; idle/done/failed draw statically.
    pub fn animates(state: u32) bool {
        return state == 2 or state == 4;
    }
};

test "status pulse arms only for animated states and skips missed steps without repaint loops" {
    var pulse: StatusPulse = .{};
    try std.testing.expectEqual(@as(?u32, null), pulse.nextDelayMs(0));
    pulse.synchronize(true, 100);
    try std.testing.expectEqual(@as(?u32, 300), pulse.nextDelayMs(100));
    try std.testing.expectEqual(@as(u32, 0), pulse.phaseIndex());
    try std.testing.expect(!pulse.advance(200));
    try std.testing.expect(pulse.advance(400));
    try std.testing.expectEqual(@as(u32, 1), pulse.phaseIndex());
    try std.testing.expectEqual(@as(?u32, 300), pulse.nextDelayMs(400));
    // One very late wake advances exactly once, then realigns inside the step.
    try std.testing.expect(pulse.advance(400 + 2 * StatusPulse.step_ms + 100));
    try std.testing.expectEqual(@as(?u32, 200), pulse.nextDelayMs(1100));
    try std.testing.expect(pulse.advance(1300));
    try std.testing.expectEqual(@as(u32, 0), pulse.phaseIndex());
    pulse.synchronize(false, 2000);
    try std.testing.expectEqual(@as(?u32, null), pulse.nextDelayMs(2000));
    try std.testing.expect(!pulse.advance(3000));
    try std.testing.expect(StatusPulse.animates(2) and StatusPulse.animates(4));
    for ([_]u32{ 0, 1, 3, 5, 9 }) |state| try std.testing.expect(!StatusPulse.animates(state));
}
