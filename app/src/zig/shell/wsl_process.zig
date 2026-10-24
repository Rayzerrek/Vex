const std = @import("std");
const arguments = @import("windows_arguments.zig");
const identity = @import("../terminal/command_line.zig");

pub const session_variable = "VEX_WSL_SESSION=";
pub const max_snapshot_bytes = 128 * 1024;
pub const ForegroundProcess = struct { name: []const u8, command: []const u8, is_shell: bool };
const Process = struct { pid: u32, parent: u32, group: u32, foreground: i32, name: []const u8, command: []const u8 };

pub fn validToken(token: []const u8) bool {
    if (token.len != 32) return false;
    for (token) |byte| if (!std.ascii.isHex(byte)) return false;
    return true;
}

/// Quote only arguments needing it: WSL retains unnecessary quotes around some switches/distribution names.
/// User metadata is always an argv value, never shell source.
pub fn probeCommandAlloc(allocator: std.mem.Allocator, program: []const u8, distribution: []const u8, token: []const u8) ![]u8 {
    if (!validToken(token)) return error.InvalidWslSessionToken;
    var result: std.ArrayList(u8) = .empty;
    errdefer result.deinit(allocator);
    const argv = [_][]const u8{ program, "--distribution", distribution, "--exec", "sh", "-c", @embedFile("integration/wsl_process_query.sh"), "vex-process-query", token };
    for (argv, 0..) |argument, i| {
        const safe = argument.len > 0 and for (argument) |byte| {
            if (!std.ascii.isAlphanumeric(byte) and std.mem.indexOfScalar(u8, "-_.:/\\=", byte) == null) break false;
        } else true;
        const quoted = if (safe) null else try arguments.quoteWindowsArgument(allocator, argument);
        defer if (quoted) |owned| allocator.free(owned);
        if (i > 0) try result.append(allocator, ' ');
        try result.appendSlice(allocator, quoted orelse argument);
    }
    return result.toOwnedSlice(allocator);
}

/// Borrowed process metadata, not OSC captions: select the nearest foreground application below this pane's shell.
pub fn parseSnapshot(text: []const u8) ?ForegroundProcess {
    if (text.len > max_snapshot_bytes or !std.unicode.utf8ValidateSlice(text)) return null;
    var lines = std.mem.tokenizeScalar(u8, text, '\n');
    const root_pid = std.fmt.parseInt(u32, std.mem.trim(u8, lines.next() orelse return null, " \t\r"), 10) catch return null;
    if (root_pid == 0) return null;
    var processes: [2048]Process = undefined;
    var count: usize = 0;
    var root: ?usize = null;
    while (lines.next()) |line| {
        const process = parseRow(line) orelse continue;
        if (count == processes.len) return null;
        if (process.pid == root_pid) root = count;
        processes[count] = process;
        count += 1;
    }
    const shell = processes[root orelse return null];
    if (shell.foreground <= 0) return null;
    var selected: ?Process = null;
    var selected_depth: usize = std.math.maxInt(usize);
    for (processes[0..count]) |process| {
        if (process.pid == root_pid or process.group != @as(u32, @intCast(shell.foreground))) continue;
        var parent = process.parent;
        var depth: usize = 1;
        while (parent != root_pid and depth < 32) : (depth += 1) {
            const ancestor = for (processes[0..count]) |candidate| {
                if (candidate.pid == parent) break candidate;
            } else break;
            if (ancestor.parent == parent) break;
            parent = ancestor.parent;
        }
        if (parent != root_pid) continue;
        if (depth < selected_depth or (depth == selected_depth and (selected == null or process.pid < selected.?.pid))) {
            selected = process;
            selected_depth = depth;
        }
    }
    if (selected) |process| {
        const target = identity.parseCommandLineIdentity(process.command);
        const name = if (target) |app|
            // Keep local scripts attached to their runtime, just like native Windows processes.
            (if (app.is_local_script) identity.commandExecutable(process.command) else app.name)
        else
            process.name;
        return .{ .name = name, .command = process.command, .is_shell = false };
    }
    return .{ .name = shell.name, .command = shell.command, .is_shell = true };
}

fn parseRow(raw: []const u8) ?Process {
    const line = std.mem.trim(u8, raw, " \t\r");
    var fields = std.mem.tokenizeAny(u8, line, " \t");
    const pid = std.fmt.parseInt(u32, fields.next() orelse return null, 10) catch return null;
    const parent = std.fmt.parseInt(u32, fields.next() orelse return null, 10) catch return null;
    const group = std.fmt.parseInt(u32, fields.next() orelse return null, 10) catch return null;
    const foreground = std.fmt.parseInt(i32, fields.next() orelse return null, 10) catch return null;
    const name = fields.next() orelse return null;
    const command = std.mem.trim(u8, fields.rest(), " \t");
    if (pid == 0 or command.len == 0) return null;
    return .{ .pid = pid, .parent = parent, .group = group, .foreground = foreground, .name = name, .command = command };
}

test "WSL foreground groups isolate panes, background jobs and application workers without name lists" {
    const snapshot = "100\n" ++
        "100 1 100 200 future-shell future-shell --login\n" ++
        "200 100 200 200 MainThread node /home/user/.npm-global/bin/codex\n" ++
        "201 200 200 200 codex /vendor/codex\n" ++
        "202 201 200 200 worker worker --agent unrelated\n" ++
        "300 100 300 200 agy /home/user/.local/bin/agy\n" ++
        "400 1 200 200 unrelated unrelated\n";
    const codex = parseSnapshot(snapshot) orelse return error.MissingProcess;
    try std.testing.expectEqualStrings("codex", codex.name);
    try std.testing.expect(!codex.is_shell);
    const agy = parseSnapshot("500\n500 1 500 600 arbitrary-shell arbitrary-shell\n600 500 600 600 agy /home/user/.local/bin/agy\n") orelse return error.MissingProcess;
    try std.testing.expectEqualStrings("agy", agy.name);
    const future = parseSnapshot("1\n1 0 1 2 shell shell\n2 1 2 2 python python -m never_seen_agent\n") orelse return error.MissingProcess;
    try std.testing.expectEqualStrings("never_seen_agent", future.name);
    const idle = parseSnapshot("100\n100 1 100 100 arbitrary-shell arbitrary-shell\n300 100 300 100 work work\n") orelse return error.MissingProcess;
    try std.testing.expect(idle.is_shell);
    try std.testing.expectEqualStrings("arbitrary-shell", idle.name);
}

test "WSL local scripts retain runtime icons while installed tools keep their identity" {
    const app_icon = @import("../terminal/app_icon.zig");
    const cases = [_]struct { row: []const u8, glyph: app_icon.GlyphId }{
        .{ .row = "200 100 200 200 python3.13 /usr/bin/python3.13 server.py", .glyph = .python },
        .{ .row = "200 100 200 200 MainThread node server.js", .glyph = .nodedotjs },
        .{ .row = "200 100 200 200 bun bun run server.ts", .glyph = .bun },
        .{ .row = "200 100 200 200 node node /npm/node_modules/@anthropic-ai/claude-code/cli.js", .glyph = .claudecode },
    };
    for (cases) |case| {
        const snapshot = try std.mem.concat(std.testing.allocator, u8, &.{ "100\n100 1 100 200 bash bash\n", case.row, "\n" });
        defer std.testing.allocator.free(snapshot);
        const process = parseSnapshot(snapshot) orelse return error.MissingProcess;
        try std.testing.expectEqual(case.glyph, app_icon.resolve(process.name, process.command, null, null).glyph);
    }
}

test "WSL process snapshots fail closed on missing roots, malformed input and parent cycles" {
    for ([_][]const u8{ "", "0\n", "garbage\n", "100\n200 100 200 200 work work\n", "100\n100 1 100 -1 shell shell\n", "100\n100 1 100 200 shell shell\n\xff" }) |text| {
        try std.testing.expect(parseSnapshot(text) == null);
    }
    const cycle = parseSnapshot("100\n100 1 100 200 shell shell\n200 201 200 200 wrong wrong\n201 200 200 200 wrong wrong\n") orelse return error.MissingProcess;
    try std.testing.expect(cycle.is_shell);
}

fn checkProbeAllocation(allocator: std.mem.Allocator) !void {
    const command = try probeCommandAlloc(allocator, "C:\\Windows\\System32\\wsl.exe", "My Distro", "0123456789abcdef0123456789abcdef");
    defer allocator.free(command);
    try std.testing.expect(std.mem.indexOf(u8, command, "--distribution \"My Distro\"") != null);
}

test "WSL query quoting, session tokens and allocation failures preserve ownership" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkProbeAllocation, .{});
    try std.testing.expectError(error.InvalidWslSessionToken, probeCommandAlloc(std.testing.allocator, "wsl.exe", "NixOS", "not-a-token"));
}
