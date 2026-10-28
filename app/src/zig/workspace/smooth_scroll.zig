const std = @import("std");

/// Scroll in fractional rows; only crossing a row boundary invalidates the terminal snapshot.
/// Timing matches the native PixelScroll used by lists and dropdowns.
pub const SmoothScroll = struct {
    from: f64 = 0,
    destination: f64 = 0,
    started_ms: u64 = 0,
    observed_row: u64 = 0,
    maximum: u64 = 0,
    initialized: bool = false,
    pub const duration_ms: u64 = 100;

    pub fn clear(self: *SmoothScroll) void {
        self.initialized = false;
    }
    pub fn synchronize(self: *SmoothScroll, row: u64, maximum: u64, now: u64) void {
        // External seeks, output pruning and screen changes must never be undone by a stale target.
        if (!self.initialized or row != self.observed_row or maximum != self.maximum) {
            self.* = .{ .from = @floatFromInt(row), .destination = @floatFromInt(row), .observed_row = row, .maximum = maximum, .started_ms = now, .initialized = true };
        }
    }
    pub fn sample(self: SmoothScroll, now: u64) f64 {
        const elapsed = now -| self.started_ms;
        if (elapsed >= duration_ms) return self.destination;
        const remaining = 1 - @as(f64, @floatFromInt(elapsed)) / duration_ms;
        return self.destination + (self.from - self.destination) * remaining * remaining * remaining;
    }
    pub fn pending(self: SmoothScroll, now: u64) bool {
        return self.initialized and self.from != self.destination and now -| self.started_ms < duration_ms;
    }
    pub fn wheel(self: *SmoothScroll, delta: i32, units: u32, now: u64, animate: bool) void {
        const next = std.math.clamp(self.destination + @as(f64, @floatFromInt(delta)) * @as(f64, @floatFromInt(@min(units, 1000))) / 120, 0, @as(f64, @floatFromInt(self.maximum)));
        if (next == self.destination) return;
        self.from = if (animate) self.sample(now) else next;
        self.destination = next;
        self.started_ms = now;
    }
    pub fn position(self: *SmoothScroll, now: u64, animate: bool) f64 {
        if (!animate) self.from = self.destination;
        return if (animate) self.sample(now) else @floor(self.destination);
    }
};

test "wheel scroll retains sub-row input, accumulates targets and reverses continuously" {
    var scroll: SmoothScroll = .{};
    scroll.synchronize(100, 200, 0);
    scroll.wheel(-10, 3, 0, true);
    try std.testing.expectEqual(@as(f64, 99.75), scroll.destination);
    const middle = scroll.sample(50);
    try std.testing.expect(middle > 99.75 and middle < 100);
    scroll.wheel(-120, 3, 50, true);
    try std.testing.expectEqual(middle, scroll.sample(50));
    try std.testing.expectEqual(@as(f64, 96.75), scroll.destination);
    const reversed = scroll.sample(80);
    scroll.wheel(120, 3, 80, true);
    try std.testing.expectEqual(reversed, scroll.sample(80));
    try std.testing.expectEqual(@as(f64, 99.75), scroll.sample(180));
    try std.testing.expect(!scroll.pending(180));
}

test "external seeks, history changes, reduced motion and limits cannot leave stale scroll wakes" {
    var scroll: SmoothScroll = .{};
    scroll.synchronize(5, 20, 0);
    scroll.wheel(-12000, 3, 0, true);
    try std.testing.expectEqual(@as(f64, 0), scroll.destination);
    scroll.synchronize(20, 20, 10);
    try std.testing.expectEqual(@as(f64, 20), scroll.sample(10));
    try std.testing.expect(!scroll.pending(10));
    scroll.wheel(-120, 3, 20, true);
    try std.testing.expectEqual(@as(f64, 17), scroll.position(30, false));
    try std.testing.expect(!scroll.pending(30));
    scroll.synchronize(0, 0, 40);
    scroll.wheel(120, 3, 40, true);
    try std.testing.expect(!scroll.pending(40));
    scroll.clear();
    try std.testing.expect(!scroll.pending(40));
}
