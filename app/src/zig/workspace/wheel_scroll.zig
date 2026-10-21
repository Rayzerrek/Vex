const std = @import("std");

/// Keep sub-notch wheel input per owner; touchpads must not lose every delta below WHEEL_DELTA.
pub const WheelScroll = struct {
    remainder: i64 = 0,
    units: u32 = 0,

    pub fn take(self: *WheelScroll, delta: i32, units: u32) i32 {
        if (self.units != units) self.remainder = 0;
        self.units = units;
        const total = self.remainder + @as(i64, delta) * @min(units, 1000);
        const steps = @divTrunc(total, 120);
        self.remainder = @rem(total, 120);
        return @intCast(steps);
    }
};

test "high resolution wheel deltas accumulate instead of disappearing" {
    var wheel: WheelScroll = .{};
    try std.testing.expectEqual(@as(i32, 0), wheel.take(10, 3));
    try std.testing.expectEqual(@as(i32, 0), wheel.take(10, 3));
    try std.testing.expectEqual(@as(i32, 0), wheel.take(10, 3));
    try std.testing.expectEqual(@as(i32, 1), wheel.take(10, 3));
    try std.testing.expectEqual(@as(i32, -3), wheel.take(-120, 3));
    try std.testing.expectEqual(@as(i32, 0), wheel.take(60, 1));
    try std.testing.expectEqual(@as(i32, 1), wheel.take(60, 1));
    try std.testing.expectEqual(@as(i32, 0), wheel.take(30, 1));
    try std.testing.expectEqual(@as(i32, 0), wheel.take(-30, 1));
    try std.testing.expectEqual(@as(i32, 0), wheel.take(120, 0));
}
