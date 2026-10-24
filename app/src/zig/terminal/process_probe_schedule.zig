const std = @import("std");

/// Brief bursts catch command startup/exit without continuously scanning idle panes.
pub const ProcessProbeSchedule = struct {
    last_probe_ms: i64 = 0,
    fast_until_ms: i64 = 0,
    last_output_ms: ?i64 = null,
    foreground: bool = false,
    visible: bool = false,

    pub const fast_interval_ms = 50;
    pub const idle_interval_ms = 1000;

    pub fn noteVisibility(self: *ProcessProbeSchedule, now: i64, visible: bool) bool {
        const activated = visible and !self.visible;
        self.visible = visible;
        if (activated) {
            self.last_probe_ms = 0;
            self.fast_until_ms = @max(self.fast_until_ms, now + 1500);
        }
        return activated;
    }

    pub fn noteInput(self: *ProcessProbeSchedule, now: i64, bytes: []const u8) bool {
        // Ordinary typing and terminal replies do not imply a process transition.
        if (std.mem.indexOfAny(u8, bytes, "\r\n\x03\x04\x0f\x1a\x1c") == null) return false;
        self.fast_until_ms = now + 1500;
        return true;
    }

    pub fn noteOutput(self: *ProcessProbeSchedule, now: i64) void {
        // A new output burst often precedes the first prompt or a foreground exit.
        if (self.last_output_ms == null or now - self.last_output_ms.? >= 500)
            self.fast_until_ms = @max(self.fast_until_ms, now + 300);
        self.last_output_ms = now;
    }

    pub fn due(self: ProcessProbeSchedule, now: i64) bool {
        const interval: i64 = if (now < self.fast_until_ms) fast_interval_ms else if (self.foreground) 200 else idle_interval_ms;
        return self.last_probe_ms == 0 or now - self.last_probe_ms >= interval;
    }

    pub fn nextFastCheck(self: ProcessProbeSchedule, now: i64) ?u32 {
        if (now >= self.fast_until_ms) return null;
        return @intCast(std.math.clamp(self.last_probe_ms + fast_interval_ms - now, 1, fast_interval_ms));
    }
};

test "command transitions get bounded fast probes, not every keystroke" {
    var schedule: ProcessProbeSchedule = .{ .last_probe_ms = 1000 };
    try std.testing.expect(!schedule.noteInput(1001, "python"));
    try std.testing.expect(!schedule.due(1050));
    try std.testing.expect(schedule.noteInput(1002, "\r"));
    try std.testing.expect(!schedule.due(1049));
    try std.testing.expect(schedule.due(1050));
    schedule.last_probe_ms = 1050;
    try std.testing.expectEqual(@as(?u32, 50), schedule.nextFastCheck(1050));
    try std.testing.expect(!schedule.due(1099));
    try std.testing.expectEqual(@as(?u32, null), schedule.nextFastCheck(2502));
    schedule.last_probe_ms = 2500;
    try std.testing.expect(!schedule.due(3499));
    try std.testing.expect(schedule.due(3500));
}

test "submit, multiline paste, interrupt and EOF all trigger the same bounded policy" {
    for ([_][]const u8{ "\r", "\n", "\x03", "\x04", "\x0f", "\x1a", "\x1c", "\x1b[200~python\rnode\x1b[201~" }) |bytes| {
        var schedule: ProcessProbeSchedule = .{ .last_probe_ms = 1000 };
        try std.testing.expect(schedule.noteInput(1100, bytes));
        try std.testing.expect(schedule.due(1100));
        try std.testing.expectEqual(@as(i64, 2600), schedule.fast_until_ms);
        try std.testing.expectEqual(@as(?u32, null), schedule.nextFastCheck(2600));
    }
}

test "idle polling costs one query per second instead of one per key or output block" {
    var schedule: ProcessProbeSchedule = .{ .last_probe_ms = 1000 };
    var queries: usize = 0;
    for (1001..61001) |step| {
        const now: i64 = @intCast(step);
        _ = schedule.noteInput(now, "x");
        if (schedule.due(now)) {
            schedule.last_probe_ms = now;
            queries += 1;
        }
    }
    try std.testing.expectEqual(@as(usize, 60), queries);
}

test "tab and split visibility transitions refresh metadata but ordinary layout does not" {
    var schedule: ProcessProbeSchedule = .{};
    try std.testing.expect(schedule.noteVisibility(1000, true));
    schedule.last_probe_ms = 1000;
    try std.testing.expect(!schedule.noteVisibility(1001, true));
    try std.testing.expectEqual(@as(i64, 1000), schedule.last_probe_ms);
    try std.testing.expect(!schedule.noteVisibility(1002, false));
    try std.testing.expect(schedule.noteVisibility(1003, true));
    try std.testing.expect(schedule.due(1003));
}

test "silent foreground applications retain exit detection cadence" {
    const schedule: ProcessProbeSchedule = .{ .last_probe_ms = 1000, .foreground = true };
    try std.testing.expect(!schedule.due(1199));
    try std.testing.expect(schedule.due(1200));
}

test "continuous output does not cause continuous fast polling" {
    var schedule: ProcessProbeSchedule = .{};
    schedule.noteOutput(1000);
    try std.testing.expectEqual(@as(i64, 1300), schedule.fast_until_ms);
    for (1..100) |step| schedule.noteOutput(1000 + @as(i64, @intCast(step)) * 20);
    try std.testing.expectEqual(@as(i64, 1300), schedule.fast_until_ms);
    schedule.noteOutput(4000);
    try std.testing.expectEqual(@as(i64, 4300), schedule.fast_until_ms);
}
