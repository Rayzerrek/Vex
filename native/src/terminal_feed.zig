const std = @import("std");

/// Bounded OSC 7501 interception; all unrelated VT bytes are forwarded unchanged.
/// State survives pipe reads. CSI/DCS/APC/SOS/PM payloads cannot masquerade as status reports.
pub const TerminalFeedFilter = struct {
    state: enum { normal, escape, csi, string, string_escape, osc, osc_escape, passthrough_osc, passthrough_osc_escape, drop_osc, drop_osc_escape } = .normal,
    osc: [4096]u8 = undefined,
    osc_len: usize = 0,

    /// Owner callbacks execute in byte order; PTY replies and reset/status changes cannot overtake VT output.
    pub fn feedTerminalOutput(self: *TerminalFeedFilter, owner: anytype, bytes: []const u8) !void {
        if (self.state == .normal and std.mem.indexOfScalar(u8, bytes, 0x1b) == null) {
            if (bytes.len != 0) try owner.writeTerminalOutput(bytes);
            return;
        }
        var writer = OutputBatch(@TypeOf(owner)){ .owner = owner };
        for (bytes) |byte| {
            switch (self.state) {
                .normal => if (byte == 0x1b) {
                    self.state = .escape;
                } else try writer.append(byte),
                .escape => {
                    if (byte == ']') {
                        self.osc_len = 0;
                        self.state = .osc;
                    } else {
                        try writer.append(0x1b);
                        if (byte != 0x1b) try writer.append(byte);
                        self.state = switch (byte) {
                            '[' => .csi,
                            'P', 'X', '^', '_' => .string,
                            0x1b => .escape,
                            else => .normal,
                        };
                        if (byte == 'c') {
                            try writer.flush();
                            owner.onTerminalReset();
                        }
                    }
                },
                .csi => {
                    if (byte == 0x1b) {
                        self.state = .escape;
                    } else {
                        try writer.append(byte);
                        if ((byte >= 0x40 and byte <= 0x7e) or byte == 0x18 or byte == 0x1a) self.state = .normal;
                    }
                },
                .string => {
                    try writer.append(byte);
                    if (byte == 0x1b) self.state = .string_escape else if (byte == 0x18 or byte == 0x1a) self.state = .normal;
                },
                .string_escape => {
                    try writer.append(byte);
                    self.state = if (byte == '\\' or byte == 0x18 or byte == 0x1a) .normal else if (byte == 0x1b) .string_escape else .string;
                },
                .osc => {
                    if (byte == 7) {
                        try self.finishOsc(owner, &writer, "\x07");
                    } else if (byte == 0x1b) {
                        self.state = .osc_escape;
                    } else try self.appendOsc(&writer, byte);
                },
                .osc_escape => {
                    if (byte == '\\') {
                        try self.finishOsc(owner, &writer, "\x1b\\");
                    } else {
                        try self.appendOsc(&writer, 0x1b);
                        if (byte == 7) {
                            try self.finishOsc(owner, &writer, "\x07");
                        } else if (byte == 0x1b) {
                            if (self.state == .passthrough_osc) try writer.append(byte);
                            self.state = switch (self.state) {
                                .drop_osc => .drop_osc_escape,
                                .passthrough_osc => .passthrough_osc_escape,
                                else => .osc_escape,
                            };
                        } else if (self.state == .osc_escape) {
                            try self.appendOsc(&writer, byte);
                            if (self.state == .osc_escape) self.state = .osc;
                        } else if (self.state == .passthrough_osc) try writer.append(byte);
                    }
                },
                .passthrough_osc, .passthrough_osc_escape => {
                    try writer.append(byte);
                    if (byte == 7 or (self.state == .passthrough_osc_escape and byte == '\\')) self.state = .normal else self.state = if (byte == 0x1b) .passthrough_osc_escape else .passthrough_osc;
                },
                .drop_osc, .drop_osc_escape => {
                    if (byte == 7 or (self.state == .drop_osc_escape and byte == '\\')) self.state = .normal else self.state = if (byte == 0x1b) .drop_osc_escape else .drop_osc;
                },
            }
        }
        try writer.flush();
    }
    fn appendOsc(self: *TerminalFeedFilter, writer: anytype, byte: u8) !void {
        if (self.osc_len < self.osc.len) {
            self.osc[self.osc_len] = byte;
            self.osc_len += 1;
            return;
        }
        if (std.mem.startsWith(u8, &self.osc, "7501;")) {
            self.state = .drop_osc;
        } else {
            try writer.appendSlice("\x1b]");
            try writer.appendSlice(&self.osc);
            try writer.append(byte);
            self.state = .passthrough_osc;
        }
    }
    fn finishOsc(self: *TerminalFeedFilter, owner: anytype, writer: anytype, terminator: []const u8) !void {
        const state = self.state;
        self.state = .normal;
        if (state == .drop_osc) return;
        if (state == .passthrough_osc) {
            try writer.appendSlice(terminator);
            return;
        }
        const osc = self.osc[0..self.osc_len];
        if (std.mem.startsWith(u8, osc, "7501;")) {
            if (osc.len + 2 + terminator.len > 4096) return;
            try writer.flush();
            try owner.onProgramStatusOsc(osc[5..]);
        } else {
            try writer.appendSlice("\x1b]");
            try writer.appendSlice(osc);
            try writer.appendSlice(terminator);
        }
    }
};
fn OutputBatch(comptime Owner: type) type {
    return struct {
        owner: Owner,
        bytes: [8192]u8 = undefined,
        len: usize = 0,
        fn append(self: *@This(), byte: u8) !void {
            if (self.len == self.bytes.len) try self.flush();
            self.bytes[self.len] = byte;
            self.len += 1;
        }
        fn appendSlice(self: *@This(), bytes: []const u8) !void {
            for (bytes) |byte| try self.append(byte);
        }
        fn flush(self: *@This()) !void {
            if (self.len == 0) return;
            try self.owner.writeTerminalOutput(self.bytes[0..self.len]);
            self.len = 0;
        }
    };
}

test "oversized unrelated OSC is forwarded verbatim, including repeated escapes at capacity" {
    for ([_][]const u8{ "\x07", "\x1b\\", "\x1b\x07", "\x1b\x1b\\" }) |terminator| {
        const payload = [_]u8{'a'} ** 6000;
        const bytes = try std.mem.concat(std.testing.allocator, u8, &.{ "\x1b]0;", &payload, terminator, "\x1b\x1b[31mfollowing" });
        defer std.testing.allocator.free(bytes);
        for ([_]usize{ 1, 4095, 4096, 4097, 4098, 6003 }) |split| {
            var owner: TestFeedOwner = .{};
            defer owner.output.deinit(std.testing.allocator);
            var filter: TerminalFeedFilter = .{};
            try filter.feedTerminalOutput(&owner, bytes[0..split]);
            try filter.feedTerminalOutput(&owner, bytes[split..]);
            try std.testing.expectEqualStrings(bytes, owner.output.items);
            try std.testing.expectEqual(@as(usize, 0), owner.reports);
        }
    }
}

test "OSC 7501 total byte limit includes introducer and BEL/ST terminator" {
    const payload = [_]u8{'a'} ** 4096;
    for ([_][]const u8{ "\x07", "\x1b\\" }) |terminator| {
        for ([_]usize{ 4096, 4097 }) |total| {
            const bytes = try std.mem.concat(std.testing.allocator, u8, &.{ "\x1b]7501;", payload[0 .. total - 7 - terminator.len], terminator, "ok" });
            defer std.testing.allocator.free(bytes);
            var owner: TestFeedOwner = .{};
            defer owner.output.deinit(std.testing.allocator);
            var filter: TerminalFeedFilter = .{};
            try filter.feedTerminalOutput(&owner, bytes);
            try std.testing.expectEqual(@as(usize, if (total == 4096) 1 else 0), owner.reports);
            try std.testing.expectEqualStrings("ok", owner.output.items);
        }
    }
}

const TestFeedOwner = struct {
    output: std.ArrayList(u8) = .empty,
    reports: usize = 0,
    resets: usize = 0,
    pub fn writeTerminalOutput(self: *@This(), bytes: []const u8) !void {
        try self.output.appendSlice(std.testing.allocator, bytes);
    }
    pub fn onProgramStatusOsc(self: *@This(), _: []const u8) !void {
        self.reports += 1;
    }
    pub fn onTerminalReset(self: *@This()) void {
        self.resets += 1;
    }
};
test "unrelated OSC/CSI and opaque strings pass unchanged at every chunk boundary" {
    const bytes = "before\x1b]7;file:///C:/test\x07\x1b]0;title\x1b\\\x1b[?1049h\x1b[31\x1b[0m\x1bPignored\x1b]7501;state=blocked\x1b\\after";
    for (0..bytes.len + 1) |split| {
        var owner: TestFeedOwner = .{};
        defer owner.output.deinit(std.testing.allocator);
        var filter: TerminalFeedFilter = .{};
        try filter.feedTerminalOutput(&owner, bytes[0..split]);
        try filter.feedTerminalOutput(&owner, bytes[split..]);
        try std.testing.expectEqualStrings(bytes, owner.output.items);
        try std.testing.expectEqual(@as(usize, 0), owner.reports);
    }
}
test "status is stripped across every chunk boundary and reset is not confused with soft reset" {
    const bytes = "before\x1b]7501;state=working\x1b\\after\x1b[!p\x1bc";
    for (0..bytes.len + 1) |split| {
        var owner: TestFeedOwner = .{};
        defer owner.output.deinit(std.testing.allocator);
        var filter: TerminalFeedFilter = .{};
        try filter.feedTerminalOutput(&owner, bytes[0..split]);
        try filter.feedTerminalOutput(&owner, bytes[split..]);
        try std.testing.expectEqualStrings("beforeafter\x1b[!p\x1bc", owner.output.items);
        try std.testing.expectEqual(@as(usize, 1), owner.reports);
        try std.testing.expectEqual(@as(usize, 1), owner.resets);
    }
}
test "oversized status drops through repeated ESC terminators without swallowing following output" {
    for ([_][]const u8{ "\x07", "\x1b\\", "\x1b\x07", "\x1b\x1b\\" }) |terminator| {
        var owner: TestFeedOwner = .{};
        defer owner.output.deinit(std.testing.allocator);
        var filter: TerminalFeedFilter = .{};
        try filter.feedTerminalOutput(&owner, "\x1b]7501;state=working:msg=");
        for (0..5000) |_| try filter.feedTerminalOutput(&owner, "A");
        try filter.feedTerminalOutput(&owner, terminator);
        try filter.feedTerminalOutput(&owner, "ok");
        try std.testing.expectEqualStrings("ok", owner.output.items);
        try std.testing.expectEqual(@as(usize, 0), owner.reports);
    }
}
