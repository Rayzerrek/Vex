const std = @import("std");

/// Program status is independent of the primary/alternate terminal screen.
pub const ProgramStatusState = enum(u32) {
    none,
    idle,
    working,
    done,
    blocked,
    failed,

    /// Presentation priority matches WPF across records, panes and tabs.
    pub fn displayPriority(self: ProgramStatusState) u8 {
        return switch (self) {
            .blocked => 5,
            .failed => 4,
            .done => 3,
            .working => 2,
            .idle => 1,
            .none => 0,
        };
    }
    /// Blocked reports cannot be dismissed by acknowledging completed results.
    pub fn needsAttention(self: ProgramStatusState) bool {
        return self == .blocked or self == .done or self == .failed;
    }
};
/// Only blocked reports have a reason kind.
pub const ProgramStatusKind = enum { permission, question, auth };
/// Borrows record storage until the next tracker mutation; never retain it in the UI.
pub const ProgramStatusSummary = struct {
    state: ProgramStatusState = .none,
    kind: ?ProgramStatusKind = null,
    progress: ?u8 = null,
    app: ?[]const u8 = null,
    title: ?[]const u8 = null,
    message: ?[]const u8 = null,

    /// Matches WPF's ProgramStatusPresentation badge label across states and blocked reason kinds.
    pub fn badgeLabel(self: ProgramStatusSummary) []const u8 {
        return switch (self.state) {
            .none, .idle => "",
            .working => "Working",
            .done => "Finished",
            .blocked => switch (self.kind orelse .question) {
                .permission => "Needs approval",
                .auth => "Needs sign-in",
                .question => "Needs input",
            },
            .failed => "Failed",
        };
    }

    /// Selects the most specific human-readable status detail without allocating.
    pub fn detailSlice(self: ProgramStatusSummary) []const u8 {
        if (self.message) |m| if (m.len > 0) return m;
        if (self.title) |t| if (t.len > 0) return t;
        if (self.app) |a| if (a.len > 0) return a;
        return self.badgeLabel();
    }

    /// Formats a bounded origin/detail line for multi-pane Tab Peek cards ("Pane N • Detail").
    pub fn formatOriginDetail(self: ProgramStatusSummary, buf: []u8, origin_pane_number: u8, pane_count: usize, origin_pane_title: []const u8) []const u8 {
        if (self.state == .none or self.state == .idle or buf.len == 0) return "";
        const raw_detail = self.detailSlice();
        const detail = if (raw_detail.len > 0) raw_detail else origin_pane_title;
        if (pane_count > 1 and origin_pane_number > 0) {
            const prefix = std.fmt.bufPrint(buf, "Pane {d}", .{origin_pane_number}) catch return "";
            if (detail.len == 0 or prefix.len + 3 >= buf.len) return prefix;
            buf[prefix.len] = ' ';
            buf[prefix.len + 1] = '-';
            buf[prefix.len + 2] = ' ';
            const avail = buf.len - (prefix.len + 3);
            const copy_len = @min(detail.len, avail);
            @memcpy(buf[prefix.len + 3 ..][0..copy_len], detail[0..copy_len]);
            return buf[0 .. prefix.len + 3 + copy_len];
        }
        const copy_len = @min(detail.len, buf.len);
        @memcpy(buf[0..copy_len], detail[0..copy_len]);
        return buf[0..copy_len];
    }
};

fn StatusField(comptime capacity: usize) type {
    return struct {
        bytes: [capacity]u8 = undefined,
        len: usize = 0,
        fn slice(self: *const @This()) []const u8 {
            return self.bytes[0..self.len];
        }
        fn init(bytes: []const u8) @This() {
            var result: @This() = .{ .len = bytes.len };
            @memcpy(result.bytes[0..bytes.len], bytes);
            return result;
        }
    };
}
const RecordId = StatusField(128);
const StatusRecord = struct {
    id: RecordId = .{},
    state: ProgramStatusState,
    kind: ?ProgramStatusKind = null,
    progress: ?u8 = null,
    app: ?StatusField(32) = null,
    title: ?StatusField(192) = null,
    message: ?StatusField(2048) = null,
    unread: bool = true,
};
const StatusReport = union(enum) { clear: RecordId, replace: StatusRecord };
const ShellFallback = union(enum) {
    none,
    idle,
    working: ?u8,
    done,
    blocked: ?u8,
    failed_progress: ?u8,
    failed_command: StatusField(64),
};

/// Owns at most 256 OSC 7501 records, ordered by last update. Never copy a live tracker.
pub const ProgramStatusTracker = struct {
    allocator: std.mem.Allocator,
    records: std.ArrayList(*StatusRecord) = .empty,
    attention_pending: bool = false,
    has_program_status: bool = false,
    shell_started_ns: ?u64 = null,
    shell_status: ShellFallback = .none,
    pub const max_records = 256;

    /// Releases every record and the bounded pointer list.
    pub fn deinit(self: *ProgramStatusTracker) void {
        self.resetProgramStatus();
        self.records.deinit(self.allocator);
    }
    /// Malformed reports are ignored atomically; allocation failures preserve existing records.
    pub fn applyProgramStatus(self: *ProgramStatusTracker, body: []const u8) !void {
        const report = parseStatusReport(body) orelse return;
        switch (report) {
            .clear => |id| {
                var index: usize = 0;
                while (index < self.records.items.len) {
                    const record = self.records.items[index];
                    if (isStatusSubtree(record.id.slice(), id.slice())) {
                        self.allocator.destroy(record);
                        _ = self.records.orderedRemove(index);
                    } else index += 1;
                }
            },
            .replace => |value| {
                var previous: ?usize = null;
                for (self.records.items, 0..) |record, index| {
                    if (std.mem.eql(u8, record.id.slice(), value.id.slice())) {
                        previous = index;
                        break;
                    }
                }
                if (previous == null and self.records.items.len < max_records) try self.records.ensureUnusedCapacity(self.allocator, 1);
                const record = try self.allocator.create(StatusRecord);
                record.* = value;
                const attention = value.state.needsAttention() and if (previous) |index| blk: {
                    const old = self.records.items[index];
                    break :blk old.state != value.state or old.kind != value.kind or !old.unread;
                } else true;
                if (previous) |index| {
                    self.allocator.destroy(self.records.orderedRemove(index));
                } else if (self.records.items.len == max_records) {
                    self.allocator.destroy(self.records.orderedRemove(0));
                }
                self.records.appendAssumeCapacity(record);
                self.attention_pending = self.attention_pending or attention;
            },
        }
        self.has_program_status = true;
    }
    /// OSC 133 lifecycle uses monotonic nanoseconds, supplied by the terminal rather than a wall clock.
    pub fn applyShellMarker(self: *ProgramStatusTracker, body: []const u8, now_ns: u64) !void {
        if (body.len == 0 or (body.len > 1 and body[1] != ';')) return;
        switch (body[0]) {
            'A' => {
                self.removeRunningRecords();
                try self.completeShellCommand(0, now_ns);
                switch (self.shell_status) {
                    .done, .failed_progress, .failed_command => {},
                    else => self.shell_status = .idle,
                }
            },
            'C' => {
                self.shell_started_ns = now_ns;
                self.shell_status = .{ .working = null };
            },
            'D' => {
                const code = if (body.len > 2) std.fmt.parseInt(i32, std.mem.trim(u8, body[2..], " \t\r\n\x0b\x0c"), 10) catch 0 else 0;
                try self.completeShellCommand(code, now_ns);
            },
            else => {},
        }
    }
    /// OSC 9;4 is ignored after a valid explicit OSC 7501 report, including clear, until RIS.
    pub fn applyProgressReport(self: *ProgramStatusTracker, body: []const u8) void {
        if (self.has_program_status) return;
        const separator = std.mem.indexOfScalar(u8, body, ';');
        const state = if (separator) |index| body[0..index] else body;
        const progress = if (separator) |index| parseStatusPercentage(body[index + 1 ..]) else null;
        if (state.len != 1) return;
        self.shell_status = switch (state[0]) {
            '0' => .none,
            '1' => .{ .working = progress },
            '2' => .{ .failed_progress = progress },
            '3' => .{ .working = null },
            '4' => .{ .blocked = progress },
            else => return,
        };
    }
    /// Shell/progress lifecycle is observable for close safety and diagnostics, never presented as OSC 7501 status.
    pub fn shellActivityState(self: *const ProgramStatusTracker) ProgramStatusState {
        return switch (self.shell_status) {
            .none => .none,
            .idle => .idle,
            .done => .done,
            .working => .working,
            .blocked => .blocked,
            .failed_progress, .failed_command => .failed,
        };
    }
    fn completeShellCommand(self: *ProgramStatusTracker, code: i32, now_ns: u64) !void {
        const started = self.shell_started_ns orelse return;
        if (code != 0) {
            var message: StatusField(64) = .{};
            message.len = (try std.fmt.bufPrint(&message.bytes, "Exited with code {d}", .{code})).len;
            self.shell_status = .{ .failed_command = message };
        } else self.shell_status = if (now_ns -| started >= 2 * std.time.ns_per_s) .done else .idle;
        self.shell_started_ns = null;
    }
    /// Chooses highest priority, then most recent update; child records inherit the nearest ancestor app.
    pub fn programStatus(self: *const ProgramStatusTracker) ProgramStatusSummary {
        var selected: ?*const StatusRecord = null;
        for (self.records.items) |record| {
            if (!record.unread and (record.state == .done or record.state == .failed)) continue;
            if (selected) |current| {
                if (record.state.displayPriority() >= current.state.displayPriority()) selected = record;
            } else selected = record;
        }
        const record = selected orelse return .{};
        var app: ?[]const u8 = if (record.app) |*field| field.slice() else null;
        var id = record.id.slice();
        while (app == null and id.len != 0) {
            id = if (std.mem.lastIndexOfScalar(u8, id, '/')) |separator| id[0..separator] else "";
            for (self.records.items) |ancestor| if (std.mem.eql(u8, ancestor.id.slice(), id)) {
                if (ancestor.app) |*field| app = field.slice();
                break;
            };
        }
        return .{ .state = record.state, .kind = record.kind, .progress = record.progress, .app = app, .title = if (record.title) |*field| field.slice() else null, .message = if (record.message) |*field| field.slice() else null };
    }
    /// Dismisses done/error results, retaining blocked records and batch attention until consumed.
    pub fn acknowledgeProgramStatus(self: *ProgramStatusTracker) void {
        for (self.records.items) |record| if (record.state == .done or record.state == .failed) {
            record.unread = false;
        };
        switch (self.shell_status) {
            .done, .failed_command, .failed_progress => self.shell_status = .none,
            else => {},
        }
    }
    /// Latches attention across reports in one pipe/UI batch, including a subsequent clear.
    pub fn takeAttention(self: *ProgramStatusTracker) bool {
        const pending = self.attention_pending;
        self.attention_pending = false;
        return pending;
    }
    /// Terminal bell (BEL) latches background tab attention without altering program status badges.
    pub fn onBell(self: *ProgramStatusTracker) void {
        self.attention_pending = true;
    }
    /// Process exit removes running/blocked/idle records but retains unread results.
    pub fn onProgramExited(self: *ProgramStatusTracker) void {
        self.removeRunningRecords();
        self.shell_started_ns = null;
        switch (self.shell_status) {
            .done, .failed_command, .failed_progress => {},
            else => self.shell_status = .none,
        }
    }
    fn removeRunningRecords(self: *ProgramStatusTracker) void {
        var index: usize = 0;
        while (index < self.records.items.len) {
            const record = self.records.items[index];
            if (record.state == .working or record.state == .blocked or record.state == .idle) {
                self.allocator.destroy(self.records.orderedRemove(index));
            } else index += 1;
        }
    }
    /// Full terminal reset clears records; a soft reset or screen switch does not.
    pub fn resetProgramStatus(self: *ProgramStatusTracker) void {
        for (self.records.items) |record| self.allocator.destroy(record);
        self.records.clearRetainingCapacity();
        self.attention_pending = false;
        self.has_program_status = false;
        self.shell_started_ns = null;
        self.shell_status = .none;
    }
};

fn isStatusSubtree(id: []const u8, parent: []const u8) bool {
    return parent.len == 0 or std.mem.eql(u8, id, parent) or (id.len > parent.len and id[parent.len] == '/' and std.mem.startsWith(u8, id, parent));
}
fn parseStatusReport(body: []const u8) ?StatusReport {
    var record: StatusRecord = .{ .state = .none };
    var clear = false;
    var id: ?[]const u8 = null;
    var pairs = std.mem.splitScalar(u8, body, ':');
    while (pairs.next()) |raw_pair| {
        const pair = std.mem.trim(u8, raw_pair, " \t\n\r\x0b\x0c");
        const separator = std.mem.indexOfScalar(u8, pair, '=') orelse continue;
        if (separator == 0) continue;
        const key = std.mem.trim(u8, pair[0..separator], " \t\n\r\x0b\x0c");
        const value = std.mem.trim(u8, pair[separator + 1 ..], " \t\n\r\x0b\x0c");
        if (key.len > 16 or !statusFieldWithinLimits(key, value)) return null;
        if (key.len == 0) continue;
        var valid = true;
        for (key) |byte| if (byte < 'a' or byte > 'z') {
            valid = false;
        };
        for (value) |byte| if (!std.ascii.isAlphanumeric(byte) and std.mem.indexOfScalar(u8, "_.,+/=-", byte) == null) {
            valid = false;
        };
        if (!valid) continue;
        if (std.mem.eql(u8, key, "state")) {
            clear = std.mem.eql(u8, value, "clear");
            record.state = if (std.mem.eql(u8, value, "idle")) .idle else if (std.mem.eql(u8, value, "working")) .working else if (std.mem.eql(u8, value, "done")) .done else if (std.mem.eql(u8, value, "blocked")) .blocked else if (std.mem.eql(u8, value, "error")) .failed else .none;
        } else if (std.mem.eql(u8, key, "id")) {
            id = value;
        } else if (std.mem.eql(u8, key, "app")) {
            record.app = if (isStatusIdSegment(value)) StatusField(32).init(value) else null;
        } else if (std.mem.eql(u8, key, "kind")) {
            record.kind = if (std.mem.eql(u8, value, "permission")) .permission else if (std.mem.eql(u8, value, "question")) .question else if (std.mem.eql(u8, value, "auth")) .auth else null;
        } else if (std.mem.eql(u8, key, "progress")) {
            record.progress = parseStatusPercentage(value);
        } else if (std.mem.eql(u8, key, "title")) {
            record.title = decodeStatusText(192, value) orelse return null;
        } else if (std.mem.eql(u8, key, "msg")) {
            record.message = decodeStatusText(2048, value) orelse return null;
        }
    }
    if (record.state == .none and !clear) return null;
    if (id) |value| {
        var segments = std.mem.splitScalar(u8, value, '/');
        while (segments.next()) |segment| if (!isStatusIdSegment(segment)) return null;
        record.id = RecordId.init(value);
    }
    if (clear) return .{ .clear = record.id };
    if (record.state != .blocked) record.kind = null;
    if (record.state != .working and record.state != .blocked) record.progress = null;
    return .{ .replace = record };
}
fn statusFieldWithinLimits(key: []const u8, value: []const u8) bool {
    if (std.mem.eql(u8, key, "app")) return value.len <= 32;
    if (std.mem.eql(u8, key, "title")) return value.len <= 256;
    if (std.mem.eql(u8, key, "msg")) return value.len <= 2732;
    if (!std.mem.eql(u8, key, "id")) return true;
    if (value.len > 128) return false;
    var segments = std.mem.splitScalar(u8, value, '/');
    var depth: usize = 0;
    while (segments.next()) |segment| {
        depth += 1;
        if (depth > 8 or segment.len > 32) return false;
    }
    return true;
}
fn isStatusIdSegment(value: []const u8) bool {
    if (value.len == 0 or value.len > 32) return false;
    for (value) |byte| if (!std.ascii.isAlphanumeric(byte) and std.mem.indexOfScalar(u8, "_.+-", byte) == null) return false;
    return true;
}
fn parseStatusPercentage(value: []const u8) ?u8 {
    if (value.len == 0 or value.len > 3) return null;
    for (value) |byte| if (!std.ascii.isDigit(byte)) return null;
    const percentage = std.fmt.parseInt(u8, value, 10) catch return null;
    return if (percentage <= 100) percentage else null;
}
fn decodeStatusText(comptime capacity: usize, encoded: []const u8) ?StatusField(capacity) {
    // Accept padded and unpadded standard Base64, not the URL-safe alphabet.
    const unpadded = std.mem.trimEnd(u8, encoded, "=");
    const padding = encoded.len - unpadded.len;
    if (padding > 2 or (padding != 0 and encoded.len % 4 != 0)) return null;
    const decoder = std.base64.standard_no_pad.Decoder;
    const length = decoder.calcSizeForSlice(unpadded) catch return null;
    if (length > capacity) return null;
    var decoded: [capacity]u8 = undefined;
    decoder.decode(decoded[0..length], unpadded) catch return null;
    if (!std.unicode.utf8ValidateSlice(decoded[0..length])) return null;
    var result: StatusField(capacity) = .{};
    var iterator = std.unicode.Utf8View.initUnchecked(decoded[0..length]).iterator();
    while (iterator.nextCodepointSlice()) |bytes| {
        const scalar = std.unicode.utf8Decode(bytes) catch return null;
        if (scalar < 32 or (scalar >= 127 and scalar <= 159)) return null;
        if (isStatusFormatScalar(scalar)) continue;
        @memcpy(result.bytes[result.len..][0..bytes.len], bytes);
        result.len += bytes.len;
    }
    return result;
}
fn isStatusFormatScalar(scalar: u21) bool {
    // Unicode Cf (including supplementary tags): outside-grid labels must not spoof pane identity.
    return switch (scalar) {
        0xad, 0x600...0x605, 0x61c, 0x6dd, 0x70f, 0x890...0x891, 0x8e2, 0x180e, 0x200b...0x200f, 0x202a...0x202e, 0x2060...0x2064, 0x2066...0x206f, 0xfeff, 0xfff9...0xfffb, 0x110bd, 0x110cd, 0x13430...0x1343f, 0x1bca0...0x1bca3, 0x1d173...0x1d17a, 0xe0001, 0xe0020...0xe007f => true,
        else => false,
    };
}

test "OSC 7501 parser replaces fields and follows final duplicate grammar" {
    var tracker: ProgramStatusTracker = .{ .allocator = std.testing.allocator };
    defer tracker.deinit();
    try tracker.applyProgramStatus("\x0bstate\x0c= blocked : app=build:kind=question:progress=50:msg=SGVsbG8=:id=bad//id:id=correct");
    try std.testing.expectEqual(.blocked, tracker.programStatus().state);
    try std.testing.expectEqualStrings("Hello", tracker.programStatus().message orelse return error.MissingStatusMessage);
    try tracker.applyProgramStatus("state=done:state=working:id=correct:malformed:future=value:progress=101");
    const summary = tracker.programStatus();
    try std.testing.expectEqual(.working, summary.state);
    try std.testing.expect(summary.app == null and summary.kind == null and summary.progress == null and summary.message == null);
}

test "OSC 7501 malformed and oversized earlier fields preserve the previous record" {
    var tracker: ProgramStatusTracker = .{ .allocator = std.testing.allocator };
    defer tracker.deinit();
    try tracker.applyProgramStatus("state=working:progress=25");
    for ([_][]const u8{ "state=unknown", "state=error:id=bad//id", "state=error:msg=A", "state=error:msg=Cg==", "state=error:msg=/w==", "state=error:abcdefghijklmnopq=x", "state=error:id=a/b/c/d/e/f/g/h/i:id=valid", "state=error:id=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:id=valid", "state=error:app=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:app=valid" }) |body| {
        try tracker.applyProgramStatus(body);
        try std.testing.expectEqual(.working, tracker.programStatus().state);
        try std.testing.expectEqual(@as(?u8, 25), tracker.programStatus().progress);
    }
    for ([_]struct { key: []const u8, length: usize }{ .{ .key = "app", .length = 33 }, .{ .key = "id", .length = 129 }, .{ .key = "title", .length = 257 }, .{ .key = "msg", .length = 2733 } }) |field| {
        const value = try std.testing.allocator.alloc(u8, field.length);
        defer std.testing.allocator.free(value);
        @memset(value, 'A');
        value[value.len - 1] = '!';
        const body = try std.mem.concat(std.testing.allocator, u8, &.{ "state=error:", field.key, "=", value });
        defer std.testing.allocator.free(body);
        try tracker.applyProgramStatus(body);
        try std.testing.expectEqual(@as(?u8, 25), tracker.programStatus().progress);
    }
}

test "hierarchy inherits app, clears only its subtree and ranks most recent tied records" {
    var tracker: ProgramStatusTracker = .{ .allocator = std.testing.allocator };
    defer tracker.deinit();
    try tracker.applyProgramStatus("state=working:app=build");
    try tracker.applyProgramStatus("state=blocked:id=test/slow:kind=permission");
    try tracker.applyProgramStatus("state=working:id=testing");
    try std.testing.expectEqualStrings("build", tracker.programStatus().app orelse return error.MissingInheritedApp);
    try tracker.applyProgramStatus("state=blocked:id=test/fast:app=runner");
    try std.testing.expectEqualStrings("runner", tracker.programStatus().app orelse return error.MissingInheritedApp);
    try tracker.applyProgramStatus("state=clear:id=test");
    try std.testing.expectEqual(.working, tracker.programStatus().state);
    try std.testing.expectEqual(@as(usize, 2), tracker.records.items.len);
    try tracker.applyProgramStatus("state=clear");
    try std.testing.expectEqual(.none, tracker.programStatus().state);
}

test "attention latches through clear; acknowledgement and exit retain only unread results" {
    var tracker: ProgramStatusTracker = .{ .allocator = std.testing.allocator };
    defer tracker.deinit();
    try tracker.applyProgramStatus("state=done:id=finished");
    try std.testing.expect(tracker.takeAttention());
    try tracker.applyProgramStatus("state=done:id=finished");
    try std.testing.expect(!tracker.takeAttention());
    tracker.onProgramExited();
    try std.testing.expectEqual(.done, tracker.programStatus().state);
    try tracker.applyProgramStatus("state=blocked:id=pending");
    tracker.acknowledgeProgramStatus();
    try std.testing.expectEqual(.blocked, tracker.programStatus().state);
    tracker.onProgramExited();
    try std.testing.expectEqual(.none, tracker.programStatus().state);
    try tracker.applyProgramStatus("state=error:id=finished");
    try tracker.applyProgramStatus("state=clear");
    try std.testing.expect(tracker.takeAttention());
    try std.testing.expect(!tracker.takeAttention());
}

test "status text rejects controls and strips BMP/supplementary formatting" {
    const text = "safe\u{202e}\u{e0001} label";
    var encoded: [128]u8 = undefined;
    const base64 = std.base64.standard.Encoder.encode(&encoded, text);
    const decoded = decodeStatusText(192, base64) orelse return error.StatusDecodeFailed;
    try std.testing.expectEqualStrings("safe label", decoded.slice());
    try std.testing.expect(decodeStatusText(192, "SGVsbG8") != null);
    for ([_][]const u8{ "A", "Cg==", "/w==", "AA===", "====" }) |invalid| try std.testing.expect(decodeStatusText(192, invalid) == null);
    const message = [_]u8{'x'} ** 2048;
    var long_encoded: [2732]u8 = undefined;
    const long_decoded = decodeStatusText(2048, std.base64.standard.Encoder.encode(&long_encoded, &message)) orelse return error.StatusDecodeFailed;
    try std.testing.expectEqualStrings(&message, long_decoded.slice());
}

test "record cap evicts the least recently updated without a revision counter" {
    var tracker: ProgramStatusTracker = .{ .allocator = std.testing.allocator };
    defer tracker.deinit();
    try tracker.applyProgramStatus("state=blocked:id=oldest");
    for (0..256) |index| {
        var buffer: [64]u8 = undefined;
        try tracker.applyProgramStatus(try std.fmt.bufPrint(&buffer, "state=working:id=task{d}", .{index}));
    }
    try std.testing.expectEqual(.working, tracker.programStatus().state);
    try std.testing.expectEqual(@as(usize, 256), tracker.records.items.len);
}

test "OOM at record cap preserves both an eviction candidate and a replacement target" {
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    var tracker: ProgramStatusTracker = .{ .allocator = failing.allocator() };
    defer tracker.deinit();
    try tracker.applyProgramStatus("state=blocked:id=oldest");
    for (0..255) |index| {
        var buffer: [64]u8 = undefined;
        try tracker.applyProgramStatus(try std.fmt.bufPrint(&buffer, "state=working:id=task{d}", .{index}));
    }
    failing.fail_index = failing.alloc_index;
    try std.testing.expectError(error.OutOfMemory, tracker.applyProgramStatus("state=working:id=next"));
    try std.testing.expectError(error.OutOfMemory, tracker.applyProgramStatus("state=working:id=oldest"));
    try std.testing.expectEqual(@as(usize, 256), tracker.records.items.len);
    try std.testing.expectEqual(.blocked, tracker.programStatus().state);
}

test "OSC 133 markers never surface program status or attention, matching the protocol" {
    var tracker: ProgramStatusTracker = .{ .allocator = std.testing.allocator };
    defer tracker.deinit();
    try tracker.applyShellMarker("A", 0);
    try tracker.applyShellMarker("C;ignored=field", 10);
    try std.testing.expectEqual(.none, tracker.programStatus().state);
    try tracker.applyShellMarker("D;0", 2 * std.time.ns_per_s);
    try tracker.applyShellMarker("C", 0);
    try tracker.applyShellMarker("D;-2147483648", 1);
    try std.testing.expectEqual(.none, tracker.programStatus().state);
    try std.testing.expect(!tracker.takeAttention());
}

test "prompt removes running records but retains unread results across screen/process changes" {
    var tracker: ProgramStatusTracker = .{ .allocator = std.testing.allocator };
    defer tracker.deinit();
    try tracker.applyProgramStatus("state=done:id=finished");
    try tracker.applyProgramStatus("state=blocked:id=pending");
    try tracker.applyShellMarker("A", 0);
    try std.testing.expectEqual(.done, tracker.programStatus().state);
    tracker.onProgramExited();
    try std.testing.expectEqual(.done, tracker.programStatus().state);
    tracker.acknowledgeProgramStatus();
    try std.testing.expectEqual(.none, tracker.programStatus().state);
    try tracker.applyShellMarker("C", 0);
    try tracker.applyShellMarker("A", std.time.ns_per_s);
    try std.testing.expectEqual(.none, tracker.programStatus().state);
    try tracker.applyShellMarker("D;7", 2 * std.time.ns_per_s);
    try std.testing.expectEqual(.none, tracker.programStatus().state);
}

test "OSC 9;4 progress never surfaces status, and explicit OSC 7501 records still win" {
    var tracker: ProgramStatusTracker = .{ .allocator = std.testing.allocator };
    defer tracker.deinit();
    tracker.applyProgressReport("1;30");
    try std.testing.expectEqual(.none, tracker.programStatus().state);
    try std.testing.expect(!tracker.takeAttention());
    tracker.applyProgressReport("4;50");
    try std.testing.expect(!tracker.takeAttention());
    try tracker.applyProgramStatus("state=blocked:kind=auth");
    tracker.applyProgressReport("1;90");
    try std.testing.expectEqual(@as(?ProgramStatusKind, .auth), tracker.programStatus().kind);
    try std.testing.expectEqual(.blocked, tracker.programStatus().state);
}

fn checkStatusAllocations(allocator: std.mem.Allocator) !void {
    var tracker: ProgramStatusTracker = .{ .allocator = allocator };
    defer tracker.deinit();
    try tracker.applyProgramStatus("state=working:progress=42");
    tracker.applyProgramStatus("state=blocked:msg=SGVsbG8=") catch |err| {
        try std.testing.expectEqual(.working, tracker.programStatus().state);
        try std.testing.expectEqual(@as(?u8, 42), tracker.programStatus().progress);
        return err;
    };
    try tracker.applyProgramStatus("state=done:id=child");
}
test "status allocator failures preserve records and release every owner" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkStatusAllocations, .{});
}

test "status presentation labels and multi-pane origin detail formatting match WPF" {
    var buf: [64]u8 = undefined;
    const perm: ProgramStatusSummary = .{ .state = .blocked, .kind = .permission, .app = "claude", .message = "Allow bash tool?" };
    try std.testing.expectEqualStrings("Needs approval", perm.badgeLabel());
    try std.testing.expectEqualStrings("Allow bash tool?", perm.detailSlice());
    try std.testing.expectEqualStrings("Pane 2 - Allow bash tool?", perm.formatOriginDetail(&buf, 2, 3, "PowerShell 7"));
    try std.testing.expectEqualStrings("Allow bash tool?", perm.formatOriginDetail(&buf, 1, 1, "PowerShell 7"));

    const auth: ProgramStatusSummary = .{ .state = .blocked, .kind = .auth };
    try std.testing.expectEqualStrings("Needs sign-in", auth.badgeLabel());
    try std.testing.expectEqualStrings("Pane 3 - Needs sign-in", auth.formatOriginDetail(&buf, 3, 4, "Nushell"));

    const idle: ProgramStatusSummary = .{ .state = .idle };
    try std.testing.expectEqualStrings("", idle.badgeLabel());
    try std.testing.expectEqualStrings("", idle.formatOriginDetail(&buf, 1, 2, "Command Prompt"));
}

