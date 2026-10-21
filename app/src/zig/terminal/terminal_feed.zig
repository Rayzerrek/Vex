const std = @import("std");

/// Bounded OSC 7501/133/9;4 interception and ConPTY-stripped DCS query filtering; unrelated VT bytes pass unchanged.
/// State survives pipe reads. CSI/DCS/APC/SOS/PM payloads cannot masquerade as status reports or bare queries.
pub const TerminalFeedFilter = struct {
    state: enum {
        normal,
        escape,
        csi,
        string,
        string_escape,
        osc,
        osc_escape,
        passthrough_osc,
        passthrough_osc_escape,
        drop_osc,
        drop_osc_escape,
        plus_seen,
        plus_query,
        dollar_seen,
        dollar_q,
    } = .normal,
    osc: [4096]u8 = undefined,
    osc_len: usize = 0,
    plus_query: [256]u8 = undefined,
    plus_len: usize = 0,

    /// Owner callbacks execute in byte order; PTY replies and reset/status changes cannot overtake VT output.
    pub fn feedTerminalOutput(self: *TerminalFeedFilter, owner: anytype, bytes: []const u8) !void {
        if (self.state == .normal and !needsSlowPath(bytes)) {
            if (bytes.len != 0) try owner.writeTerminalOutput(bytes);
            return;
        }
        var writer = OutputBatch(@TypeOf(owner)){ .owner = owner };
        var index: usize = 0;
        while (index < bytes.len) {
            const byte = bytes[index];
            index += 1;
            switch (self.state) {
                .normal => switch (byte) {
                    0x1b, '+', '$' => try self.stepNormal(&writer, byte),
                    else => {
                        // ANSI output usually contains long text runs between short control sequences.
                        if (index == bytes.len or bytes[index] == 0x1b) {
                            try writer.append(byte);
                        } else {
                            const end = std.mem.indexOfAnyPos(u8, bytes, index, "\x1b+$") orelse bytes.len;
                            if (end == index) try writer.append(byte) else try writer.appendSlice(bytes[index - 1 .. end]);
                            index = end;
                        }
                    },
                },
                .plus_seen => if (byte == 'q') {
                    self.plus_len = 0;
                    self.state = .plus_query;
                } else {
                    try writer.append('+');
                    self.state = .normal;
                    try self.stepNormal(&writer, byte);
                },
                .plus_query => if (isQueryByte(byte)) {
                    if (self.plus_len < self.plus_query.len) {
                        self.plus_query[self.plus_len] = byte;
                        self.plus_len += 1;
                    } else {
                        try writer.appendSlice("+q");
                        try writer.appendSlice(self.plus_query[0..self.plus_len]);
                        try writer.append(byte);
                        self.plus_len = 0;
                        self.state = .normal;
                    }
                } else {
                    if (!isStrippedXtgettcap(self.plus_query[0..self.plus_len])) {
                        try writer.appendSlice("+q");
                        try writer.appendSlice(self.plus_query[0..self.plus_len]);
                    }
                    self.plus_len = 0;
                    self.state = .normal;
                    try self.stepNormal(&writer, byte);
                },
                .dollar_seen => if (byte == 'q') {
                    self.state = .dollar_q;
                } else {
                    try writer.append('$');
                    self.state = .normal;
                    try self.stepNormal(&writer, byte);
                },
                .dollar_q => if (byte == 'm') {
                    self.state = .normal;
                } else {
                    try writer.appendSlice("$q");
                    self.state = .normal;
                    try self.stepNormal(&writer, byte);
                },
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
                    var parameter = byte;
                    while (true) {
                        if (parameter == 0x1b) {
                            self.state = .escape;
                            break;
                        }
                        try writer.append(parameter);
                        if ((parameter >= 0x40 and parameter <= 0x7e) or parameter == 0x18 or parameter == 0x1a) {
                            self.state = .normal;
                            break;
                        }
                        if (index == bytes.len) break;
                        parameter = bytes[index];
                        index += 1;
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
    fn stepNormal(self: *TerminalFeedFilter, writer: anytype, byte: u8) !void {
        switch (byte) {
            0x1b => self.state = .escape,
            '+' => self.state = .plus_seen,
            '$' => self.state = .dollar_seen,
            else => try writer.append(byte),
        }
    }
    fn appendOsc(self: *TerminalFeedFilter, writer: anytype, byte: u8) !void {
        if (self.osc_len < self.osc.len) {
            self.osc[self.osc_len] = byte;
            self.osc_len += 1;
            return;
        }
        if (std.mem.startsWith(u8, &self.osc, "7501;") or isPromptMarker(&self.osc) or std.mem.startsWith(u8, &self.osc, "9;4;")) {
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
        } else if (isPromptMarker(osc)) {
            // ConPTY prompt redraws must not let libghostty mutate the terminal grid for OSC 133.
            try writer.flush();
            if (std.mem.startsWith(u8, osc, "133;")) try owner.onShellMarkerOsc(osc[4..]);
        } else if (std.mem.startsWith(u8, osc, "9;4;")) {
            try writer.flush();
            owner.onProgressOsc(osc[4..]);
        } else {
            try writer.appendSlice("\x1b]");
            try writer.appendSlice(osc);
            try writer.appendSlice(terminator);
        }
    }
};
const stripped_xtgettcap_caps = [_][]const u8{
    "5463",
    "524742",
    "4D73",
    "536D756C78",
    "53796E63",
    "73657472676266",
    "73657472676262",
    "536574756C63",
    "544E",
    "436F",
};
fn needsSlowPath(bytes: []const u8) bool {
    if (bytes.len == 0) return false;
    if (bytes[bytes.len - 1] == '+' or bytes[bytes.len - 1] == '$') return true;
    for (bytes, 0..) |byte, index| {
        if (byte == 0x1b) return true;
        if ((byte == '+' or byte == '$') and index + 1 < bytes.len and bytes[index + 1] == 'q') return true;
    }
    return false;
}
fn isQueryByte(byte: u8) bool {
    return std.ascii.isHex(byte) or byte == ';';
}
fn isStrippedXtgettcap(payload: []const u8) bool {
    if (payload.len == 0) return false;
    var start: usize = 0;
    for (0..payload.len + 1) |index| {
        if (index == payload.len or payload[index] == ';') {
            const token = payload[start..index];
            if (token.len == 0 or !isKnownXtgettcapToken(token)) return false;
            start = index + 1;
        }
    }
    return true;
}
fn isKnownXtgettcapToken(token: []const u8) bool {
    for (stripped_xtgettcap_caps) |cap| {
        if (std.ascii.eqlIgnoreCase(token, cap)) return true;
    }
    return false;
}
fn isPromptMarker(osc: []const u8) bool {
    var digits: usize = 0;
    while (digits < osc.len and std.ascii.isDigit(osc[digits])) : (digits += 1) {}
    return (std.fmt.parseInt(u32, osc[0..digits], 10) catch return false) == 133;
}
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
            var remaining = bytes;
            while (remaining.len > 0) {
                if (self.len == self.bytes.len) try self.flush();
                const count = @min(remaining.len, self.bytes.len - self.len);
                @memcpy(self.bytes[self.len..][0..count], remaining[0..count]);
                self.len += count;
                remaining = remaining[count..];
            }
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

test "long ANSI text runs survive output batches and pipe boundaries without losing reports" {
    const text = "abcdefghij 音 cost $5 C++ file/path\r\n" ** 2000;
    const bytes = "\x1b[32m" ++ text ++ "\x1b[0m\x1b]7501;state=working\x07" ++ text ++ "\x1bc";
    const expected = "\x1b[32m" ++ text ++ "\x1b[0m" ++ text ++ "\x1bc";
    for ([_]usize{ 1, 17, 8191, 8192, 8193, 65536 }) |chunk_size| {
        var owner: TestFeedOwner = .{};
        defer owner.output.deinit(std.testing.allocator);
        var filter: TerminalFeedFilter = .{};
        var offset: usize = 0;
        while (offset < bytes.len) {
            const end = @min(offset + chunk_size, bytes.len);
            try filter.feedTerminalOutput(&owner, bytes[offset..end]);
            offset = end;
        }
        try std.testing.expectEqualStrings(expected, owner.output.items);
        try std.testing.expectEqual(@as(usize, 1), owner.reports);
        try std.testing.expectEqual(@as(usize, 1), owner.resets);
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
    shell_markers: usize = 0,
    progress_reports: usize = 0,
    pub fn writeTerminalOutput(self: *@This(), bytes: []const u8) !void {
        try self.output.appendSlice(std.testing.allocator, bytes);
    }
    pub fn onProgramStatusOsc(self: *@This(), _: []const u8) !void {
        self.reports += 1;
    }
    pub fn onShellMarkerOsc(self: *@This(), _: []const u8) !void {
        self.shell_markers += 1;
    }
    pub fn onProgressOsc(self: *@This(), _: []const u8) void {
        self.progress_reports += 1;
    }
    pub fn onTerminalReset(self: *@This()) void {
        self.resets += 1;
    }
};
test "unrelated OSC/CSI and opaque strings pass unchanged at every chunk boundary" {
    const bytes = "before\x1b]7;file:///C:/test\x07\x1b]0;title\x1b\\\x1b[?1049h\x1b[123\x18\x1b[456\x1a\x1b[31\x1b[0m\x1bPignored\x1b]7501;state=blocked\x1b\\after";
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
test "OSC 133 and legacy progress are consumed without stripping OSC 1337 at every boundary" {
    const bytes = "before\x1b]133;A\x07\x1b]133;C\x1b\\\x1b]9;4;1;30\x07\x1b]1337;CurrentDir=C:/test\x07after";
    for (0..bytes.len + 1) |split| {
        var owner: TestFeedOwner = .{};
        defer owner.output.deinit(std.testing.allocator);
        var filter: TerminalFeedFilter = .{};
        try filter.feedTerminalOutput(&owner, bytes[0..split]);
        try filter.feedTerminalOutput(&owner, bytes[split..]);
        try std.testing.expectEqualStrings("before\x1b]1337;CurrentDir=C:/test\x07after", owner.output.items);
        try std.testing.expectEqual(@as(usize, 2), owner.shell_markers);
        try std.testing.expectEqual(@as(usize, 1), owner.progress_reports);
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
test "ConPTY-stripped XTGETTCAP and DECRQSS payloads are removed across every chunk boundary while intact DCS passes through" {
    const bytes = "head+q4D73\x1b[Hmid+q5463;524742;73657472676266;73657472676262\x1b[H$qm\x1b[K\x1bP+q5463\x1b\\\x1bP$qm\x1b\\tail";
    const expected = "head\x1b[Hmid\x1b[H\x1b[K\x1bP+q5463\x1b\\\x1bP$qm\x1b\\tail";
    for (0..bytes.len + 1) |split| {
        var owner: TestFeedOwner = .{};
        defer owner.output.deinit(std.testing.allocator);
        var filter: TerminalFeedFilter = .{};
        try filter.feedTerminalOutput(&owner, bytes[0..split]);
        try filter.feedTerminalOutput(&owner, bytes[split..]);
        try std.testing.expectEqualStrings(expected, owner.output.items);
    }
}
test "ordinary plus, dollar, unknown +q capabilities and oversized +q runs fail open across every split" {
    for ([_][]const u8{
        "a+b=c",
        "C++ rocks\n",
        "echo $query $q1 $env:x\n",
        "cost $100 and $5\n",
        "xx+qCD12yy",
        "what+q is this",
        "cost $q? maybe",
    }) |text| {
        for (0..text.len + 1) |split| {
            var owner: TestFeedOwner = .{};
            defer owner.output.deinit(std.testing.allocator);
            var filter: TerminalFeedFilter = .{};
            try filter.feedTerminalOutput(&owner, text[0..split]);
            try filter.feedTerminalOutput(&owner, text[split..]);
            try std.testing.expectEqualStrings(text, owner.output.items);
        }
    }
    const long_hex = [_]u8{'A'} ** 260;
    const oversized = try std.mem.concat(std.testing.allocator, u8, &.{ "+q", &long_hex, "!" });
    defer std.testing.allocator.free(oversized);
    var owner: TestFeedOwner = .{};
    defer owner.output.deinit(std.testing.allocator);
    var filter: TerminalFeedFilter = .{};
    try filter.feedTerminalOutput(&owner, oversized);
    try std.testing.expectEqualStrings(oversized, owner.output.items);
}
