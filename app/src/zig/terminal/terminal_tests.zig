const std = @import("std");
const binding = @import("api.zig");
const c = binding.c;
const status = @import("program_status.zig");
const terminal_mod = @import("terminal.zig");
const Terminal = terminal_mod.Terminal;
const CellWidth = terminal_mod.CellWidth;
const MouseTrackingMode = terminal_mod.MouseTrackingMode;
const MouseFormat = terminal_mod.MouseFormat;

test "pinned sized structs and UTF-8/color/wide cells match the actual DLL" {
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(c.GhosttyStyle));
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(c.GhosttyBuffer));
    try std.testing.expectEqual(@as(usize, 3), @sizeOf(c.GhosttyColorRgb));
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b[31mA\x1b[0mż界");
    const frame = try terminal.readFrame();
    try std.testing.expectEqualStrings("A", frame.cellText(frame.cells.items[0]));
    try std.testing.expectEqual(@as(u32, 0xff5a5a), frame.cells.items[0].style.foreground);
    try std.testing.expectEqualStrings("ż", frame.cellText(frame.cells.items[1]));
    try std.testing.expectEqualStrings("界", frame.cellText(frame.cells.items[2]));
    try std.testing.expectEqual(CellWidth.wide, frame.cells.items[2].width);
    try std.testing.expectEqual(CellWidth.tail, frame.cells.items[3].width);
    try terminal.resize(120, 40, 9, 17);
    try std.testing.expectEqual(@as(u16, 120), (try terminal.readFrame()).columns);
    try std.testing.expectError(error.InvalidTerminalSize, terminal.resize(0, 0, 0, 0));
}

test "terminal instances isolate output and clean snapshots retain generation and storage" {
    const api = try binding.Api.load();
    var first = try Terminal.init(std.testing.allocator, &api);
    defer first.deinit();
    var second = try Terminal.init(std.testing.allocator, &api);
    defer second.deinit();
    try first.feedOutput("ONLY_FIRST");
    try second.feedOutput("ONLY_SECOND");
    try std.testing.expect(try first.containsViewportText("ONLY_FIRST"));
    try std.testing.expect(!try first.containsViewportText("ONLY_SECOND"));
    try std.testing.expect(try second.containsViewportText("ONLY_SECOND"));
    const frame = try first.readFrame();
    const generation = frame.generation;
    const storage = frame.cells.items.ptr;
    _ = try first.readFrame();
    try std.testing.expectEqual(generation, frame.generation);
    try std.testing.expectEqual(storage, frame.cells.items.ptr);
}

test "alternate screen, scrollback, cursor keys and split UTF-8 retain terminal semantics" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("MARKER\xc5");
    try terminal.feedOutput("\xbc");
    try std.testing.expect(try terminal.containsViewportText("MARKERż"));
    try terminal.feedOutput("\x1b[?1049hALT");
    try std.testing.expect(!try terminal.containsViewportText("MARKER"));
    try terminal.feedOutput("\x1b[?1049l");
    try std.testing.expect(try terminal.containsViewportText("MARKER"));
    for (0..80) |_| try terminal.feedOutput("\r\nnext");
    try std.testing.expect(!try terminal.containsViewportText("MARKER"));
    terminal.scroll(-1000);
    try std.testing.expect(try terminal.containsViewportText("MARKER"));
    var bytes: [64]u8 = undefined;
    const normal = try terminal.encodeKey(0x26, 0, &bytes) orelse return error.MissingEncodedKey;
    try std.testing.expectEqualStrings("\x1b[A", normal);
    try terminal.feedOutput("\x1b[?1h");
    const application = try terminal.encodeKey(0x26, 0, &bytes) orelse return error.MissingEncodedKey;
    try std.testing.expectEqualStrings("\x1bOA", application);
    try std.testing.expectEqual(@as(?[]const u8, null), try terminal.encodeKey('A', 0, &bytes));
}

const TestReply = struct {
    bytes: [64]u8 = undefined,
    length: usize = 0,
    fn write(context: *anyopaque, bytes: []const u8) !void {
        const self: *TestReply = @ptrCast(@alignCast(context));
        if (bytes.len > self.bytes.len) return error.TestReplyOverflow;
        @memcpy(self.bytes[0..bytes.len], bytes);
        self.length = bytes.len;
    }
    fn reject(_: *anyopaque, _: []const u8) !void {
        return error.TestPtyWriteFailure;
    }
};

test "PTY reply callback targets its own terminal and propagates sink errors" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var reply: TestReply = .{};
    try terminal.attachResponseSink(.{ .context = &reply, .write = TestReply.write });
    try terminal.feedOutput("\x1b[6n");
    try std.testing.expectEqualStrings("\x1b[1;1R", reply.bytes[0..reply.length]);
    try terminal.attachResponseSink(.{ .context = &reply, .write = TestReply.reject });
    try std.testing.expectError(error.TestPtyWriteFailure, terminal.feedOutput("\x1b[6n"));
    try std.testing.expectError(error.TestPtyWriteFailure, terminal.resize(90, 30, 8, 16));
}

test "color scheme queries and subscribed changes follow the host mode per pane" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var other = try Terminal.init(std.testing.allocator, &api);
    defer other.deinit();
    var replies: StatusReplyLog = .{};
    defer replies.bytes.deinit(std.testing.allocator);
    var other_replies: StatusReplyLog = .{};
    defer other_replies.bytes.deinit(std.testing.allocator);
    try terminal.attachResponseSink(.{ .context = &replies, .write = StatusReplyLog.write });
    try other.attachResponseSink(.{ .context = &other_replies, .write = StatusReplyLog.write });
    try terminal.setColorScheme(false);
    try std.testing.expectEqualStrings("", replies.bytes.items);
    const query = "\x1b[?2031$p\x1b[?996n";
    for (query) |byte| try terminal.feedOutput(&.{byte});
    try std.testing.expectEqualStrings("\x1b[?2031;2$y\x1b[?997;2n", replies.bytes.items);
    replies.bytes.clearRetainingCapacity();
    for ("\x1b[?2031h") |byte| try terminal.feedOutput(&.{byte});
    // A colorscheme change is meaningful even when a TUI is on the alternate screen.
    try terminal.feedOutput("\x1b[?1049h");
    try terminal.setColorScheme(true);
    try terminal.setColorScheme(true);
    try terminal.setColorScheme(false);
    try std.testing.expectEqualStrings("\x1b[?997;1n\x1b[?997;2n", replies.bytes.items);
    try std.testing.expectEqualStrings("", other_replies.bytes.items);
    try other.feedOutput("\x1b[?996n");
    try std.testing.expectEqualStrings("\x1b[?997;1n", other_replies.bytes.items);
    replies.bytes.clearRetainingCapacity();
    try terminal.feedOutput("\x1b[?2031l");
    try terminal.setColorScheme(true);
    try std.testing.expectEqualStrings("", replies.bytes.items);
    try terminal.feedOutput("\x1b[?2031h\x1bc");
    try terminal.setColorScheme(false);
    try std.testing.expectEqualStrings("", replies.bytes.items);
    try terminal.feedOutput("\x1b[?996n");
    try std.testing.expectEqualStrings("\x1b[?997;2n", replies.bytes.items);
}

test "subscribed applications can re-query the updated background after a scheme notification" {
    const themes = @import("../workspace/terminal_theme.zig");
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var replies: StatusReplyLog = .{};
    defer replies.bytes.deinit(std.testing.allocator);
    try terminal.attachResponseSink(.{ .context = &replies, .write = StatusReplyLog.write });
    try terminal.feedOutput("\x1b[?2031h");
    for ([_]bool{ false, true, false }) |dark| {
        const theme = themes.themes[if (dark) themes.default_dark_index else themes.default_light_index];
        const background: u32 = if (dark) 0x111111 else 0xffffff;
        try terminal.applyPalette(theme.foreground, background, theme.cursor, &theme.ansi);
        try terminal.setColorScheme(dark);
        try std.testing.expectEqualStrings(if (dark) "\x1b[?997;1n" else "\x1b[?997;2n", replies.bytes.items);
        replies.bytes.clearRetainingCapacity();
        try terminal.feedOutput("\x1b]11;?\x1b\\");
        try std.testing.expectEqualStrings(if (dark) "\x1b]11;rgb:1111/1111/1111\x1b\\" else "\x1b]11;rgb:ffff/ffff/ffff\x1b\\", replies.bytes.items);
        replies.bytes.clearRetainingCapacity();
    }
}

test "color scheme notification failures reach the caller" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var reply: TestReply = .{};
    try terminal.attachResponseSink(.{ .context = &reply, .write = TestReply.reject });
    try terminal.feedOutput("\x1b[?2031h");
    try std.testing.expectError(error.TestPtyWriteFailure, terminal.setColorScheme(false));
}

fn checkFrameAllocationFailures(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    try terminal.feedOutput("unicode: ż界\x1b[31mred\x1b[0m");
    _ = try terminal.readFrame();
    try std.testing.expect(try terminal.containsViewportText("unicode"));
    try terminal.feedOutput("\r\nmore");
    _ = try terminal.readFrame();
}

test "all Zig frame allocation failures free native owners and snapshot buffers" {
    const api = try binding.Api.load();
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkFrameAllocationFailures, .{&api});
}

test "viewport text keeps blank-cell gaps and row boundaries, including overlapping prefixes" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("AAAB\x1b[2CC\r\nXYZ");
    try std.testing.expect(try terminal.containsViewportText("AAB"));
    try std.testing.expect(try terminal.containsViewportText("B  C"));
    try std.testing.expect(!try terminal.containsViewportText("BC"));
    try std.testing.expect(!try terminal.containsViewportText("CXYZ"));
}

test "long combining graphemes grow owned buffers without truncation" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var input: [129]u8 = undefined;
    input[0] = 'e';
    for (0..64) |index| {
        input[1 + index * 2] = 0xcc;
        input[2 + index * 2] = 0x81;
    }
    try terminal.feedOutput(&input);
    const frame = try terminal.readFrame();
    try std.testing.expectEqualStrings(&input, frame.cellText(frame.cells.items[0]));
    try std.testing.expect(terminal.grapheme.capacity >= input.len);
}

test "OSC 7 borrowed directory reports survive every UTF-8/terminator chunk boundary" {
    const api = try binding.Api.load();
    for ([_][]const u8{ "\x07", "\x1b\\" }) |terminator| {
        const report = "file:///C:/My%20files/%C5%BB%C3%B3%C5%82%C4%87";
        const bytes = try std.mem.concat(std.testing.allocator, u8, &.{ "\x1b]7;", report, terminator });
        defer std.testing.allocator.free(bytes);
        for (1..bytes.len) |split| {
            var terminal = try Terminal.init(std.testing.allocator, &api);
            defer terminal.deinit();
            try terminal.feedOutput(bytes[0..split]);
            try terminal.feedOutput(bytes[split..]);
            try std.testing.expectEqualStrings(report, try terminal.reportedDirectory());
        }
    }
}

test "shell markers preserve the actual DLL prompt grid and never surface status over explicit reports" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    const bytes = "prompt> \x1b]133;A\x07\x1b]133;C\x07\x1b]133;D;7\x07\x1b]133;A\x07";
    for (bytes) |byte| try terminal.feedOutput(&.{byte});
    try std.testing.expect(try terminal.containsViewportText("prompt> "));
    try std.testing.expectEqual(@as(u16, 8), (try terminal.readFrame()).cursor.column);
    try std.testing.expectEqual(.none, terminal.program_status.programStatus().state);
    try terminal.feedOutput("\x1b]9;4;1;30\x07");
    try std.testing.expectEqual(.none, terminal.program_status.programStatus().state);
    try terminal.feedOutput("\x1b]7501;state=blocked:kind=auth\x07\x1b]9;4;1;90\x07");
    try std.testing.expectEqual(@as(?status.ProgramStatusKind, .auth), terminal.program_status.programStatus().kind);
}

const StatusReplyLog = struct {
    bytes: std.ArrayList(u8) = .empty,
    fn write(context: *anyopaque, bytes: []const u8) !void {
        const self: *@This() = @ptrCast(@alignCast(context));
        try self.bytes.appendSlice(std.testing.allocator, bytes);
    }
};

test "OSC 7501 real DLL transport preserves text, reply order and pane isolation at every boundary" {
    const api = try binding.Api.load();
    for ([_][]const u8{ "\x07", "\x1b\\" }) |terminator| {
        const bytes = try std.mem.concat(std.testing.allocator, u8, &.{ "before\x1b]7501;state=working:progress=42", terminator, "after\x1b]7501;?", terminator, "\x1b[6n" });
        defer std.testing.allocator.free(bytes);
        for (0..bytes.len + 1) |split| {
            var terminal = try Terminal.init(std.testing.allocator, &api);
            defer terminal.deinit();
            var other = try Terminal.init(std.testing.allocator, &api);
            defer other.deinit();
            var replies: StatusReplyLog = .{};
            defer replies.bytes.deinit(std.testing.allocator);
            try terminal.attachResponseSink(.{ .context = &replies, .write = StatusReplyLog.write });
            try terminal.feedOutput(bytes[0..split]);
            try terminal.feedOutput(bytes[split..]);
            try std.testing.expectEqual(.working, terminal.program_status.programStatus().state);
            try std.testing.expectEqual(@as(?u8, 42), terminal.program_status.programStatus().progress);
            try std.testing.expectEqual(.none, other.program_status.programStatus().state);
            try std.testing.expectEqualStrings("\x1b]7501;?\x1b\\\x1b[1;12R", replies.bytes.items);
            try std.testing.expect(try terminal.containsViewportText("beforeafter"));
        }
    }
}

test "status survives alternate screen and soft reset; user acknowledgement and RIS are independent" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b]7501;state=done\x07\x1b[?1049h\x1b[?1049l\x1b[!p");
    try std.testing.expectEqual(.done, terminal.program_status.programStatus().state);
    terminal.program_status.acknowledgeProgramStatus();
    try std.testing.expectEqual(.none, terminal.program_status.programStatus().state);
    try terminal.feedOutput("\x1b]7501;state=blocked:kind=auth\x07");
    terminal.program_status.acknowledgeProgramStatus();
    try std.testing.expectEqual(.blocked, terminal.program_status.programStatus().state);
    try terminal.feedOutput("\x1bc");
    try std.testing.expectEqual(.none, terminal.program_status.programStatus().state);
}

fn checkStatusFeedAllocations(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b]7501;state=working:progress=42\x07");
    terminal.feedOutput("\x1b]7501;state=error\x07") catch |err| {
        try std.testing.expectEqual(.working, terminal.program_status.programStatus().state);
        return err;
    };
}
test "status feed allocation and capability sink errors reach the real terminal caller" {
    const api = try binding.Api.load();
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkStatusFeedAllocations, .{&api});
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var replies: TestReply = .{};
    try terminal.attachResponseSink(.{ .context = &replies, .write = TestReply.reject });
    try std.testing.expectError(error.TestPtyWriteFailure, terminal.feedOutput("\x1b]7501;?\x07"));
}

const paths = @import("../shell/directory.zig");
const TestDirectoryOwner = struct {
    directory: paths.PaneDirectory,
    pub fn onDirectoryReport(self: *@This(), report: []const u8) !void {
        try self.directory.applyReport(report, "my-pc");
    }
};

test "WSL OSC 7 directory callbacks decode Linux paths at every real-DLL chunk boundary" {
    const api = try binding.Api.load();
    const distribution = try @import("../shell/wsl_paths.zig").WslDistribution.parse("Ubuntu");
    const output = "\x1b]7;file://localhost/home/Żółć%20O%27Brien\x1b\\\x1b]7;file://remote/home/other\x07";
    for (0..output.len + 1) |boundary| {
        var owner: TestDirectoryOwner = .{ .directory = try paths.PaneDirectory.initForContext(std.testing.allocator, try paths.DirectoryPath.parse("C:\\launch"), .{ .wsl = distribution }) };
        defer owner.directory.deinit();
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        try terminal.attachDirectorySink(&owner);
        try terminal.feedOutput(output[0..boundary]);
        try terminal.feedOutput(output[boundary..]);
        try std.testing.expectEqualStrings("\\\\wsl.localhost\\Ubuntu\\home\\Żółć O'Brien", owner.directory.current().bytes);
    }
}

test "directory callbacks preserve valid reports before invalid/empty reports in the same read" {
    const api = try binding.Api.load();
    var owner: TestDirectoryOwner = .{ .directory = try paths.PaneDirectory.init(std.testing.allocator, try paths.DirectoryPath.parse("C:\\launch")) };
    defer owner.directory.deinit();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.attachDirectorySink(&owner);
    try terminal.feedOutput("\x1b]7;file:///C:/good\x07\x1b]7;file://remote/C:/bad\x07\x1b]7;\x07");
    try std.testing.expectEqualStrings("C:\\good", owner.directory.current().bytes);
}

fn checkDirectoryCallbackAllocations(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var owner: TestDirectoryOwner = .{ .directory = try paths.PaneDirectory.init(allocator, try paths.DirectoryPath.parse("C:\\launch")) };
    defer owner.directory.deinit();
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    try terminal.attachDirectorySink(&owner);
    terminal.feedOutput("\x1b]7;file:///C:/reported\x07") catch |err| {
        try std.testing.expectEqualStrings("C:\\launch", owner.directory.current().bytes);
        return err;
    };
    try std.testing.expectEqualStrings("C:\\reported", owner.directory.current().bytes);
}

test "native directory callback OOM reaches feedOutput and releases both owners" {
    const api = try binding.Api.load();
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkDirectoryCallbackAllocations, .{&api});
}

test "theme changes replace startup OSC overrides and repaint idle text in both directions" {
    const themes = @import("../workspace/terminal_theme.zig");
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b]10;#eeeeee\x07\x1b]11;#111111\x07\x1b]4;1;#222222\x07A\x1b[31mB\x1b[38;2;18;52;86mC");
    _ = try terminal.readFrame();
    for ([_]usize{ themes.default_light_index, themes.default_dark_index, themes.default_light_index }) |index| {
        const theme = themes.themes[index];
        const generation = (try terminal.readFrame()).generation;
        try terminal.applyPalette(theme.foreground, theme.background, theme.cursor, &theme.ansi);
        const frame = try terminal.readFrame();
        try std.testing.expect(frame.generation > generation);
        try std.testing.expectEqual(theme.foreground, frame.cells.items[0].style.foreground);
        try std.testing.expectEqual(theme.background, frame.cells.items[0].style.background);
        try std.testing.expectEqual(theme.ansi[1], frame.cells.items[1].style.foreground);
        try std.testing.expectEqual(@as(u32, 0x123456), frame.cells.items[2].style.foreground);
        try std.testing.expectEqual(theme.background, frame.cells.items[3].style.background);
    }
}

test "OSC palette updates invalidate cached resolved colors" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b[31mX");
    const before = try terminal.readFrame();
    const generation = before.generation;
    try std.testing.expectEqual(@as(u32, 0xff5a5a), before.cells.items[0].style.foreground);
    try terminal.feedOutput("\x1b]4;1;rgb:00/ff/00\x1b\\");
    const after = try terminal.readFrame();
    try std.testing.expectEqual(@as(u32, 0x00ff00), after.cells.items[0].style.foreground);
    try std.testing.expect(after.generation > generation);
}

test "ConPTY-stripped DCS payloads never reach the real DLL grid while intact XTGETTCAP replies and ordinary text survive" {
    const api = try binding.Api.load();
    const stream = "+q4D73\x1b[H+q5463;524742;73657472676266;73657472676262\x1b[H$qm\x1b[K\x1bP+q5463\x1b\\visible xx+qCD12yy cost $q? maybe";
    for (0..stream.len + 1) |split| {
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        var replies: StatusReplyLog = .{};
        defer replies.bytes.deinit(std.testing.allocator);
        try terminal.attachResponseSink(.{ .context = &replies, .write = StatusReplyLog.write });
        try terminal.feedOutput(stream[0..split]);
        try terminal.feedOutput(stream[split..]);
        try std.testing.expect(!try terminal.containsViewportText("+q4D73"));
        try std.testing.expect(!try terminal.containsViewportText("+q5463"));
        try std.testing.expect(!try terminal.containsViewportText("$qm"));
        try std.testing.expect(try terminal.containsViewportText("visible xx+qCD12yy cost $q? maybe"));
        try std.testing.expect(replies.bytes.items.len > 0);
    }
}

test "growing rows on resize does not pull scrollback into the active ConPTY screen" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(5, 3, 8, 16);
    try terminal.feedOutput("1\r\n2\r\n3\r\n4\r\n5");
    try terminal.resize(5, 5, 8, 16);
    const frame = try terminal.readFrame();
    const expected = [_][]const u8{ "3", "4", "5", "", "" };
    for (expected, 0..) |want, row| {
        const first = frame.cellText(frame.cells.items[row * frame.columns]);
        try std.testing.expectEqualStrings(want, first);
    }
}

test "default cursor is bar, DECSCUSR updates and resets shape, and encodeKey stays ConPTY-compatible under Kitty/modifyOtherKeys" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var replies: StatusReplyLog = .{};
    defer replies.bytes.deinit(std.testing.allocator);
    try terminal.attachResponseSink(.{ .context = &replies, .write = StatusReplyLog.write });
    try std.testing.expectEqual(.bar, (try terminal.readFrame()).cursor.shape);
    try terminal.feedOutput("\x1b[2 q");
    try std.testing.expectEqual(.block, (try terminal.readFrame()).cursor.shape);
    try terminal.feedOutput("\x1b[4 q");
    try std.testing.expectEqual(.underline, (try terminal.readFrame()).cursor.shape);
    try terminal.feedOutput("\x1b[0 q");
    try std.testing.expectEqual(.bar, (try terminal.readFrame()).cursor.shape);
    try terminal.feedOutput("\x1b[>1u\x1b[>4;2m\x1b[?u");
    try std.testing.expectEqualStrings("\x1b[?1u", replies.bytes.items);
    var encoded: [64]u8 = undefined;
    try std.testing.expectEqualStrings("\x1b[A", (try terminal.encodeKey(0x26, 0, &encoded)).?);
    try std.testing.expectEqualStrings("\x1b[1;2A", (try terminal.encodeKey(0x26, 1, &encoded)).?);
}

test "cursor blink flag follows DECSCUSR, DEC 12 and visibility independently in the real DLL" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try std.testing.expect((try terminal.readFrame()).cursor.blinking);
    const reports = [_][]const u8{ "\x1b[1 q", "\x1b[2 q", "\x1b[3 q", "\x1b[4 q", "\x1b[5 q", "\x1b[6 q", "\x1b[0 q" };
    for (reports, 0..) |report, index| {
        try terminal.feedOutput(report);
        try std.testing.expectEqual(index % 2 == 0, (try terminal.readFrame()).cursor.blinking);
    }
    try terminal.feedOutput("\x1b[?12l");
    try std.testing.expect(!(try terminal.readFrame()).cursor.blinking);
    try terminal.feedOutput("\x1b[?12h\x1b[?25l");
    const hidden = (try terminal.readFrame()).cursor;
    try std.testing.expect(hidden.blinking);
    try std.testing.expect(!hidden.visible);
    try terminal.feedOutput("\x1b[?25h");
    try std.testing.expect((try terminal.readFrame()).cursor.visible);
}

fn checkPasteAllocationFailures(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b[?2004h");
    const pasted = try terminal.encodePasteAlloc(allocator, "first\r\nsecond\n\x1b[201~third");
    defer allocator.free(pasted);
    try std.testing.expectEqualStrings("\x1b[200~first\rsecond\rthird\x1b[201~", pasted);
}

test "encodePasteAlloc normalizes newlines, tracks DEC 2004 across screens/resets, and strips breakout sequences" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try std.testing.expect(!try terminal.bracketedPaste());
    const plain = try terminal.encodePasteAlloc(std.testing.allocator, "a\r\nb\nc\rd");
    defer std.testing.allocator.free(plain);
    try std.testing.expectEqualStrings("a\rb\rc\rd", plain);
    try terminal.feedOutput("\x1b[?2004h");
    try std.testing.expect(try terminal.bracketedPaste());
    const bracketed = try terminal.encodePasteAlloc(std.testing.allocator, "line1\r\nline2\n\x1b[201~safe");
    defer std.testing.allocator.free(bracketed);
    try std.testing.expectEqualStrings("\x1b[200~line1\rline2\rsafe\x1b[201~", bracketed);
    try terminal.feedOutput("\x1b[?2004l");
    try std.testing.expect(!try terminal.bracketedPaste());
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkPasteAllocationFailures, .{&api});
}

test "viewport scroll shortcuts move primary scrollback, yield on alternate screen, and encode Ctrl+Backspace" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(10, 2, 8, 16);
    try terminal.feedOutput("ROW_TOP\r\nROW_MID1\r\nROW_MID2\r\nROW_BOT1\r\nROW_BOT2");
    try std.testing.expect(try terminal.containsViewportText("ROW_BOT2"));
    try std.testing.expect(try terminal.scrollViewportShortcut(0x21, 1));
    try std.testing.expect(try terminal.containsViewportText("ROW_MID1"));
    try std.testing.expect(try terminal.scrollViewportShortcut(0x24, 2));
    try std.testing.expect(try terminal.containsViewportText("ROW_TOP"));
    try std.testing.expect(try terminal.scrollViewportShortcut(0x22, 1));
    try std.testing.expect(try terminal.containsViewportText("ROW_MID2"));
    try std.testing.expect(try terminal.scrollViewportShortcut(0x23, 2));
    try std.testing.expect(try terminal.containsViewportText("ROW_BOT2"));
    try terminal.feedOutput("\x1b[?1049h");
    try std.testing.expect(try terminal.isAlternateScreen());
    try std.testing.expect(!try terminal.scrollViewportShortcut(0x24, 2));
    try std.testing.expect(!try terminal.scrollViewportShortcut(0x21, 1));
    var encoded: [64]u8 = undefined;
    try std.testing.expectEqualStrings("\x17", (try terminal.encodeKey(0x08, 2, &encoded)).?);
    try std.testing.expectEqualStrings("\x7f", (try terminal.encodeKey(0x08, 0, &encoded)).?);
    try std.testing.expectEqualStrings("\x7f", (try terminal.encodeKey(0x08, 1, &encoded)).?);
    try std.testing.expectError(error.InvalidTerminalBufferLength, terminal.encodeKey(0x08, 0, encoded[0..0]));
}

const ClipboardLog = struct {
    allocator: std.mem.Allocator,
    writes: std.ArrayList([]u8) = .empty,
    pub fn deinit(self: *ClipboardLog) void {
        for (self.writes.items) |entry| self.allocator.free(entry);
        self.writes.deinit(self.allocator);
    }
    pub fn onClipboardWrite(self: *ClipboardLog, bytes: []const u8) !void {
        const copy = try self.allocator.dupe(u8, bytes);
        errdefer self.allocator.free(copy);
        try self.writes.append(self.allocator, copy);
    }
};

test "copy feedback validates parsed source after split OSC and TUI redraws, not inside the clipboard callback" {
    const feedback = @import("../app/copy_feedback.zig");
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    var log: ClipboardLog = .{ .allocator = std.testing.allocator };
    defer log.deinit();
    try terminal.attachClipboardSink(&log);
    try terminal.resize(24, 6, 8, 16);
    try terminal.feedOutput("\x1b[?1049h\x1b[1;1H 1  \x1b[7malpha\x1b[0m\r\n 2    \x1b[7mbeta\x1b[0m");
    var before: feedback.Snapshot = .{};
    defer before.deinit(std.testing.allocator);
    try before.capture(std.testing.allocator, try terminal.readFrame(), 1000);
    // The provider writes the clipboard before repainting its own selection.
    try terminal.feedOutput("\x1b]52;c;YWxwaGEK");
    try terminal.feedOutput("YmV0YQ==\x07\x1b[1;5Halpha\x1b[2;7Hbeta");
    try std.testing.expectEqual(@as(usize, 1), log.writes.items.len);
    const copied = log.writes.items[0];
    const box = feedback.unchanged(std.testing.allocator, &before, try terminal.readFrame(), copied).?;
    try std.testing.expectEqual(@as(u16, 0), box.first_row);
    try std.testing.expectEqual(@as(u16, 1), box.last_row);
    // A later output block completing a delete must revoke the same candidate.
    try terminal.feedOutput("\x1b[1;1H\x1b[2K");
    try std.testing.expect(feedback.unchanged(std.testing.allocator, &before, try terminal.readFrame(), copied) == null);
}

fn checkClipboardAllocationFailures(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    var log: ClipboardLog = .{ .allocator = allocator };
    defer log.deinit();
    try terminal.attachClipboardSink(&log);
    try terminal.feedOutput("\x1b]52;c;b2s=\x07");
    try std.testing.expectEqual(@as(usize, 1), log.writes.items.len);
    try std.testing.expectEqualStrings("ok", log.writes.items[0]);
}

test "write-only OSC 52 and OSC 1337 Copy sequences decode Unicode across splits while ignoring reads, primary selection, invalid UTF-8 and oversized writes" {
    const api = try binding.Api.load();
    const copied = "Zażółć 漢 🦀 e\u{0301}\nsecond line";
    var encoded_buf: [128]u8 = undefined;
    const encoded = std.base64.standard.Encoder.encode(&encoded_buf, copied);
    for ([_][]const u8{ "\x07", "\x1b\\" }) |terminator| {
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        var log: ClipboardLog = .{ .allocator = std.testing.allocator };
        defer log.deinit();
        try terminal.attachClipboardSink(&log);
        var stream: [256]u8 = undefined;
        const payload = try std.fmt.bufPrint(&stream, "before\x1b]52;c;{s}{s}after", .{ encoded, terminator });
        for (payload) |byte| try terminal.feedOutput(&.{byte});
        try std.testing.expectEqual(@as(usize, 1), log.writes.items.len);
        try std.testing.expectEqualStrings(copied, log.writes.items[0]);
        try std.testing.expect(try terminal.containsViewportText("beforeafter"));
    }
    {
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        var log: ClipboardLog = .{ .allocator = std.testing.allocator };
        defer log.deinit();
        try terminal.attachClipboardSink(&log);
        try terminal.feedOutput("\x1b]52;c;?\x07\x1b]52;c;invalid!\x07\x1b]52;p;aGVsbG8=\x07\x1b]52;c;/w==\x07after");
        try std.testing.expectEqual(@as(usize, 0), log.writes.items.len);
        try std.testing.expect(try terminal.containsViewportText("after"));
        try terminal.feedOutput("\x1b]1337;Copy=:b2s=\x07");
        try std.testing.expectEqual(@as(usize, 1), log.writes.items.len);
        try std.testing.expectEqualStrings("ok", log.writes.items[0]);
    }
    {
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        var log: ClipboardLog = .{ .allocator = std.testing.allocator };
        defer log.deinit();
        try terminal.attachClipboardSink(&log);
        const text = try std.testing.allocator.alloc(u8, 20_000);
        defer std.testing.allocator.free(text);
        @memset(text, 'x');
        const b64_len = std.base64.standard.Encoder.calcSize(text.len);
        const packet = try std.testing.allocator.alloc(u8, 7 + b64_len + 2);
        defer std.testing.allocator.free(packet);
        @memcpy(packet[0..7], "\x1b]52;c;");
        _ = std.base64.standard.Encoder.encode(packet[7 .. 7 + b64_len], text);
        @memcpy(packet[7 + b64_len ..], "\x1b\\");
        var offset: usize = 0;
        while (offset < packet.len) : (offset += 37) {
            try terminal.feedOutput(packet[offset..@min(offset + 37, packet.len)]);
        }
        try std.testing.expectEqual(@as(usize, 1), log.writes.items.len);
        try std.testing.expectEqualStrings(text, log.writes.items[0]);
    }
    {
        var terminal = try Terminal.init(std.testing.allocator, &api);
        defer terminal.deinit();
        var log: ClipboardLog = .{ .allocator = std.testing.allocator };
        defer log.deinit();
        try terminal.attachClipboardSink(&log);
        const huge = try std.testing.allocator.alloc(u8, 7 + 1024 * 1024 + 13);
        defer std.testing.allocator.free(huge);
        @memcpy(huge[0..7], "\x1b]52;c;");
        @memset(huge[7 .. 7 + 1024 * 1024], 'A');
        @memcpy(huge[7 + 1024 * 1024 ..], "\x07\x1b]52;c;b2s=\x07");
        try terminal.feedOutput(huge);
        try std.testing.expectEqual(@as(usize, 1), log.writes.items.len);
        try std.testing.expectEqualStrings("ok", log.writes.items[0]);
    }
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkClipboardAllocationFailures, .{&api});
}

test "BEL control latches background attention without firing on OSC BEL terminators or changing program status" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.feedOutput("\x1b]7;file:///C:/project\x07\x1b]7501;state=working\x07\x1b]133;A\x07\x1b]0;Window Title\x07");
    try std.testing.expect(!terminal.program_status.takeAttention());
    try terminal.feedOutput("before\x07after");
    try std.testing.expect(terminal.program_status.takeAttention());
    try std.testing.expect(!terminal.program_status.takeAttention());
    try std.testing.expectEqual(.none, terminal.program_status.programStatus().state);
    try std.testing.expect(try terminal.containsViewportText("beforeafter"));
    try terminal.feedOutput("\x07\x1bc");
    try std.testing.expect(!terminal.program_status.takeAttention());
}

test "OSC 0 and OSC 2 update sanitized terminal title and reset on shell prefix or RIS" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try std.testing.expectEqualStrings("", terminal.title.slice());
    try terminal.feedOutput("\x1b]0;App.xaml.cs [+] - nvim\x07");
    try std.testing.expectEqualStrings("App.xaml.cs*", terminal.title.slice());
    try terminal.feedOutput("\x1b]2;Administrator: Windows PowerShell\x1b\\");
    try std.testing.expectEqualStrings("", terminal.title.slice());
    try terminal.feedOutput("\x1b]2;claude - Fix rendering pipeline\x07");
    try std.testing.expectEqualStrings("Fix rendering pipeline", terminal.title.slice());
    try terminal.feedOutput("\x1bc");
    try std.testing.expectEqualStrings("", terminal.title.slice());
}

fn checkSelectionAllocationFailures(allocator: std.mem.Allocator, api: *const binding.Api) !void {
    var terminal = try Terminal.init(allocator, api);
    defer terminal.deinit();
    try terminal.resize(10, 4, 8, 16);
    try terminal.feedOutput("hello_world\r\nsecond");
    try terminal.setKeyboardSelection(0, 0, 5, 0);
    const selected = (try terminal.selectedTextAlloc(allocator, true)) orelse return error.MissingSelectedText;
    defer allocator.free(selected);
    try std.testing.expectEqualStrings("hello", selected);
}

test "selection gestures, keyboard selection, cell highlight flags and soft-wrap unwrapping match Ghostty" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(8, 4, 8, 16);
    try terminal.feedOutput("abcdefghij\r\nword_two rest");
    try std.testing.expect(!try terminal.hasSelection());
    try std.testing.expectEqual(@as(?[]u8, null), try terminal.selectedTextAlloc(std.testing.allocator, true));

    // Soft-wrapped line selection (row 0..1) unwraps without inserting an artificial newline.
    try terminal.setKeyboardSelection(0, 0, 2, 1);
    try std.testing.expect(try terminal.hasSelection());
    const unwrapped = (try terminal.selectedTextAlloc(std.testing.allocator, true)).?;
    defer std.testing.allocator.free(unwrapped);
    try std.testing.expectEqualStrings("abcdefghij", unwrapped);
    const frame = try terminal.readFrame();
    try std.testing.expect(frame.cells.items[0].selected);
    try std.testing.expect(frame.cells.items[7].selected);
    try std.testing.expect(frame.cells.items[8].selected);
    try std.testing.expect(frame.cells.items[9].selected);
    try std.testing.expect(!frame.cells.items[10].selected);

    // Double-click on "word_two" on row 2 selects the whole word.
    try terminal.clearSelection(true);
    try std.testing.expect(!try terminal.hasSelection());
    try std.testing.expectEqual(@as(u8, 1), try terminal.selectionPress(1, 2, 12.0, 40.0));
    try terminal.selectionRelease(1, 2);
    try std.testing.expectEqual(@as(u8, 2), try terminal.selectionPress(1, 2, 12.0, 40.0));
    try terminal.selectionRelease(1, 2);
    const word = (try terminal.selectedTextAlloc(std.testing.allocator, true)).?;
    defer std.testing.allocator.free(word);
    try std.testing.expectEqualStrings("word_two", word);

    // Triple-click on row 2 selects the entire logical line (including soft-wrapped continuation on row 3).
    try std.testing.expectEqual(@as(u8, 3), try terminal.selectionPress(1, 2, 12.0, 40.0));
    try terminal.selectionRelease(1, 2);
    const line = (try terminal.selectedTextAlloc(std.testing.allocator, true)).?;
    defer std.testing.allocator.free(line);
    try std.testing.expectEqualStrings("word_two rest", line);

    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkSelectionAllocationFailures, .{&api});
}

test "extendKeyboardSelection and consumeKeyboardSelectionBackspaces handle word navigation, wide glyphs, and cursor-end deletion" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(20, 4, 8, 16);
    try terminal.feedOutput("first\r\ncmd arg1 界x");

    // Ctrl+Shift+Left from cursor at end of "cmd arg1 界x" (col 12, row 1) selects "界x" (3 cells, 2 codepoints).
    try std.testing.expect(try terminal.extendKeyboardSelection(0x25, 3));
    {
        const text = (try terminal.selectedTextAlloc(std.testing.allocator, true)).?;
        defer std.testing.allocator.free(text);
        try std.testing.expectEqualStrings("界x", text);
    }
    try std.testing.expectEqual(@as(usize, 2), try terminal.consumeKeyboardSelectionBackspaces());
    try std.testing.expect(!try terminal.hasSelection());

    // Shift+Home selects the entire current line up to the cursor ("cmd arg1 界x" = 11 codepoints).
    try std.testing.expect(try terminal.extendKeyboardSelection(0x24, 1));
    try std.testing.expectEqual(@as(usize, 11), try terminal.consumeKeyboardSelectionBackspaces());

    // Selections crossing a hard newline cannot be deleted via backspaces.
    try terminal.setKeyboardSelection(0, 0, 12, 1);
    try std.testing.expectEqual(@as(usize, 0), try terminal.consumeKeyboardSelectionBackspaces());

    // Alternate screen yields keyboard selection chords back to the terminal application.
    try terminal.feedOutput("\x1b[?1049h");
    try std.testing.expect(!try terminal.extendKeyboardSelection(0x25, 1));
}

test "mouse tracking priority, partial mode reset resilience, SGR/pixel formats, wheel, motion deduplication, and DEC 2026/2048" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(20, 10, 10, 20);
    var out: [64]u8 = undefined;

    try std.testing.expect(!try terminal.isMouseTracking());
    try std.testing.expectEqual(MouseTrackingMode.none, try terminal.mouseTrackingMode());
    try std.testing.expectEqual(MouseFormat.x10, try terminal.mouseFormat());
    try std.testing.expectEqualStrings("", try terminal.encodeMouse(.press, .left, 0, 15.0, 25.0, true, &out));

    // Enabling 1000 and 1002, then resetting 1000 must keep 1002 active rather than collapsing to none.
    try terminal.feedOutput("\x1b[?1000h\x1b[?1002h\x1b[?1006h\x1b[?1000l");
    try std.testing.expect(try terminal.isMouseTracking());
    try std.testing.expectEqual(MouseTrackingMode.button, try terminal.mouseTrackingMode());
    try std.testing.expectEqual(MouseFormat.sgr, try terminal.mouseFormat());

    // Cell (1, 1) in 0-based (x=15..19, y=25..39 with 10x20 cells) is (2, 2) in 1-based SGR coordinates.
    try std.testing.expectEqualStrings("\x1b[<0;2;2M", try terminal.encodeMouse(.press, .left, 0, 15.0, 25.0, true, &out));
    // Motion within the same cell is deduplicated; moving to cell (3, 2) -> 1-based (4, 3) emits button-motion 32.
    try std.testing.expectEqualStrings("", try terminal.encodeMouse(.motion, .left, 0, 18.0, 29.0, true, &out));
    try std.testing.expectEqualStrings("\x1b[<32;4;3M", try terminal.encodeMouse(.motion, .left, 0, 35.0, 45.0, true, &out));
    // Unpressed motion is filtered in button-event mode (1002) but emitted in any-event mode (1003).
    try std.testing.expectEqualStrings("", try terminal.encodeMouse(.motion, .none, 0, 55.0, 45.0, false, &out));
    try terminal.feedOutput("\x1b[?1003h");
    try std.testing.expectEqual(MouseTrackingMode.any, try terminal.mouseTrackingMode());
    try std.testing.expectEqualStrings("\x1b[<35;6;3M", try terminal.encodeMouse(.motion, .none, 0, 55.0, 45.0, false, &out));
    try std.testing.expectEqualStrings("\x1b[<0;6;3m", try terminal.encodeMouse(.release, .left, 0, 55.0, 45.0, false, &out));

    // Wheel up/down in SGR format (buttons 64 and 65).
    try std.testing.expectEqualStrings("\x1b[<64;2;2M", try terminal.encodeMouse(.press, .wheel_up, 0, 15.0, 25.0, false, &out));
    try std.testing.expectEqualStrings("\x1b[<65;2;2M", try terminal.encodeMouse(.press, .wheel_down, 0, 15.0, 25.0, false, &out));

    // SGR-Pixels format (1016) encodes pixel coordinates directly.
    try terminal.feedOutput("\x1b[?1016h");
    try std.testing.expectEqual(MouseFormat.sgr_pixels, try terminal.mouseFormat());
    try std.testing.expectEqualStrings("\x1b[<0;15;25M", try terminal.encodeMouse(.press, .left, 0, 15.0, 25.0, true, &out));

    // Synchronized output (DEC 2026) and in-band resize (DEC 2048) live mode bits.
    try std.testing.expect(!try terminal.synchronizedOutput());
    try std.testing.expect(!try terminal.inBandResize());
    try terminal.feedOutput("\x1b[?2026h\x1b[?2048h");
    try std.testing.expect(try terminal.synchronizedOutput());
    try std.testing.expect(try terminal.inBandResize());
    try terminal.feedOutput("\x1b[?2026l\x1b[?2048l");
    try std.testing.expect(!try terminal.synchronizedOutput());
    try std.testing.expect(!try terminal.inBandResize());
}

test "RIS clears mouse motion deduplication before a new application tracks the same cell" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(20, 10, 10, 20);
    var out: [64]u8 = undefined;
    const enable = "\x1b[?1003h\x1b[?1006h";
    try terminal.feedOutput(enable);
    try std.testing.expectEqualStrings("\x1b[<35;2;2M", try terminal.encodeMouse(.motion, .none, 0, 15, 25, false, &out));
    try std.testing.expectEqualStrings("", try terminal.encodeMouse(.motion, .none, 0, 15, 25, false, &out));
    try terminal.feedOutput("\x1bc" ++ enable);
    try std.testing.expectEqualStrings("\x1b[<35;2;2M", try terminal.encodeMouse(.motion, .none, 0, 15, 25, false, &out));
    try std.testing.expectEqualStrings("", try terminal.encodeMouse(.motion, .none, 0, 15, 25, false, &out));
}

test "bundled ConPTY forwards color scheme negotiation to the host terminal" {
    const allocator = std.testing.allocator;
    const directory = try std.fs.selfExeDirPathAlloc(allocator);
    defer allocator.free(directory);
    const fixture = try std.fs.path.join(allocator, &.{ directory, "vex-conpty-fixture.exe" });
    defer allocator.free(fixture);
    const output = try std.process.Child.run(.{ .allocator = allocator, .argv = &.{ fixture, "color-scheme" }, .max_output_bytes = 64 * 1024 });
    defer allocator.free(output.stdout);
    defer allocator.free(output.stderr);
    try std.testing.expectEqual(std.process.Child.Term{ .Exited = 0 }, output.term);
    try std.testing.expect(std.mem.indexOf(u8, output.stdout, "\x1b[?2031$p") != null);
    try std.testing.expect(std.mem.indexOf(u8, output.stdout, "\x1b[?2031h") != null);
    try std.testing.expect(std.mem.indexOf(u8, output.stdout, "\x1b[?996n") != null);
    try std.testing.expect(std.mem.indexOf(u8, output.stdout, "COLOR_SCHEME_REPLY_OK") != null);
}

test "bundled ConPTY preserves erase semantics from child output through the terminal" {
    const allocator = std.testing.allocator;
    const directory = try std.fs.selfExeDirPathAlloc(allocator);
    defer allocator.free(directory);
    const fixture = try std.fs.path.join(allocator, &.{ directory, "vex-conpty-fixture.exe" });
    defer allocator.free(fixture);
    const api = try binding.Api.load();
    for ([_][]const u8{ "all", "screen", "history", "alternate" }) |scenario| {
        const output = try std.process.Child.run(.{ .allocator = allocator, .argv = &.{ fixture, scenario }, .max_output_bytes = 64 * 1024 });
        defer allocator.free(output.stdout);
        defer allocator.free(output.stderr);
        try std.testing.expectEqual(std.process.Child.Term{ .Exited = 0 }, output.term);
        try std.testing.expect(std.mem.indexOf(u8, output.stdout, "OLD_PROMPT") != null);
        if (!std.mem.eql(u8, scenario, "screen")) try std.testing.expect(std.mem.indexOf(u8, output.stdout, "\x1b[3J") != null);
        var terminal = try Terminal.init(allocator, &api);
        defer terminal.deinit();
        try terminal.resize(40, 5, 8, 16);
        var reply: TestReply = .{};
        try terminal.attachResponseSink(.{ .context = &reply, .write = TestReply.write });
        for (output.stdout) |byte| try terminal.feedOutput(&.{byte});
        try std.testing.expect(try terminal.containsViewportText("NEW_PROMPT"));
        const keep_screen = std.mem.eql(u8, scenario, "history") or std.mem.eql(u8, scenario, "alternate");
        try std.testing.expectEqual(keep_screen, try terminal.containsViewportText("ROW_4"));
        const keep_history = std.mem.eql(u8, scenario, "screen") or std.mem.eql(u8, scenario, "alternate");
        try std.testing.expectEqual(keep_history, (try terminal.scrollbar()).isVisible());
        try terminal.scrollToRow(0);
        try std.testing.expectEqual(keep_history, try terminal.containsViewportText("OLD_PROMPT"));
    }
}

test "erase saved lines removes scrollback even when viewing older output" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(20, 4, 8, 16);
    try terminal.feedOutput("OLD_PROMPT\r\n1\r\n2\r\n3\r\n4\r\n5");
    try std.testing.expect((try terminal.scrollbar()).isVisible());
    try terminal.scrollToRow(0);
    for ("\x1b[H\x1b[2J\x1b[3J") |byte| try terminal.feedOutput(&.{byte});
    try terminal.scrollToRow(0);
    try std.testing.expect(!try terminal.containsViewportText("OLD_PROMPT"));
    try std.testing.expect(!(try terminal.scrollbar()).isVisible());
}

test "scrollbar state and scrollToRow seek exact scrollback offsets" {
    const api = try binding.Api.load();
    var terminal = try Terminal.init(std.testing.allocator, &api);
    defer terminal.deinit();
    try terminal.resize(20, 4, 8, 16);

    try std.testing.expect(!(try terminal.scrollbar()).isVisible());
    try terminal.feedOutput("ROW_0\r\nROW_1\r\nROW_2\r\nROW_3\r\nROW_4\r\nROW_5\r\nROW_6\r\nROW_7");
    const bottom = try terminal.scrollbar();
    try std.testing.expect(bottom.isVisible());
    try std.testing.expectEqual(@as(u64, 8), bottom.total);
    try std.testing.expectEqual(@as(u64, 4), bottom.len);
    try std.testing.expectEqual(@as(u64, 4), bottom.offset);

    try terminal.scrollToRow(0);
    try std.testing.expectEqual(@as(u64, 0), (try terminal.scrollbar()).offset);
    try std.testing.expect(try terminal.containsViewportText("ROW_0"));
    try std.testing.expect(!try terminal.containsViewportText("ROW_7"));

    try terminal.scrollToRow(2);
    try std.testing.expectEqual(@as(u64, 2), (try terminal.scrollbar()).offset);
    try std.testing.expect(try terminal.containsViewportText("ROW_2"));

    try terminal.scrollToRow(99);
    try std.testing.expectEqual(@as(u64, 4), (try terminal.scrollbar()).offset);
    try std.testing.expect(try terminal.containsViewportText("ROW_7"));
}
