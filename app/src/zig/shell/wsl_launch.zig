const std = @import("std");
const directory = @import("directory.zig");
const wsl = @import("wsl_paths.zig");
const windows_arguments = @import("windows_arguments.zig");

/// Default Linux login shell or an explicit Linux program with raw Windows-argv arguments; no shell interpolation.
pub const WslShellCommand = union(enum) { default, program: struct { path: wsl.LinuxProgram, arguments: []const u8 } };

/// Owns WSL launch metadata independently of parsed argv; no distribution probes or user profile writes.
pub const WslLaunch = struct {
    allocator: std.mem.Allocator,
    distribution: wsl.WslDistribution,
    suffix: []const u8,

    /// Clones the distribution and encodes Linux argv/environment once, before creating pane processes.
    pub fn init(allocator: std.mem.Allocator, distribution: wsl.WslDistribution, command: WslShellCommand, environment: *const std.process.EnvMap) !WslLaunch {
        const name = try allocator.dupe(u8, distribution.bytes);
        errdefer allocator.free(name);
        const suffix = try buildWslCommandSuffix(allocator, command, environment);
        return .{ .allocator = allocator, .distribution = .{ .bytes = name }, .suffix = suffix };
    }
    /// Releases metadata after every pane directory has stopped borrowing its distribution name.
    pub fn deinit(self: *WslLaunch) void {
        self.allocator.free(self.distribution.bytes);
        self.allocator.free(self.suffix);
    }
    /// Quotes the Windows launcher and distribution, leaving --cd to the per-pane command builder.
    pub fn commandPrefixAlloc(self: *const WslLaunch, allocator: std.mem.Allocator, program: []const u8) ![]u8 {
        const quoted_program = try windows_arguments.quoteWindowsArgument(allocator, program);
        defer allocator.free(quoted_program);
        // Some WSL versions retain unnecessary distribution quotes; safe token names need none.
        const safe_name = for (self.distribution.bytes) |byte| {
            if (!std.ascii.isAlphanumeric(byte) and byte != '-' and byte != '_' and byte != '.') break false;
        } else true;
        const quoted_name = if (safe_name) null else try windows_arguments.quoteWindowsArgument(allocator, self.distribution.bytes);
        defer if (quoted_name) |owned| allocator.free(owned);
        return std.fmt.allocPrint(allocator, "{s} --distribution {s}", .{ quoted_program, quoted_name orelse self.distribution.bytes });
    }
    /// Creates fresh per-pane storage; Windows drive paths use WSL's own mount configuration via --cd.
    pub fn commandForDirectoryAlloc(self: *const WslLaunch, allocator: std.mem.Allocator, prefix: []const u8, path: directory.DirectoryPath) ![]u8 {
        return self.commandForSessionAlloc(allocator, prefix, path, null);
    }
    /// Per-pane tokens isolate Linux process inspection across tabs, splits and windows.
    pub fn commandForSessionAlloc(self: *const WslLaunch, allocator: std.mem.Allocator, prefix: []const u8, path: directory.DirectoryPath, token: ?[]const u8) ![]u8 {
        const process = @import("wsl_process.zig");
        const tagged = if (token) |value| blk: {
            if (!process.validToken(value)) return error.InvalidWslSessionToken;
            const env_prefix = "--exec env ";
            std.debug.assert(std.mem.startsWith(u8, self.suffix, env_prefix));
            break :blk try std.fmt.allocPrint(allocator, "{s}{s}{s} {s}", .{ env_prefix, process.session_variable, value, self.suffix[env_prefix.len..] });
        } else null;
        defer if (tagged) |owned| allocator.free(owned);
        const suffix = tagged orelse self.suffix;
        if (path.shell_home) return std.fmt.allocPrint(allocator, "{s} --cd ~ {s}", .{ prefix, suffix });
        const linux = if (try wsl.networkDistribution(path.bytes) != null) try wsl.linuxDirectoryFromNetworkAlloc(allocator, path.bytes, self.distribution) else null;
        defer if (linux) |owned| allocator.free(owned);
        if (linux == null and (!std.ascii.isAlphabetic(path.bytes[0]) or path.bytes[1] != ':')) return error.UnsupportedWslWorkingDirectory;
        const quoted_directory = try windows_arguments.quoteWindowsArgument(allocator, linux orelse path.bytes);
        defer allocator.free(quoted_directory);
        const command = try std.fmt.allocPrint(allocator, "{s} --cd {s} {s}", .{ prefix, quoted_directory, suffix });
        errdefer allocator.free(command);
        try windows_arguments.validateShellText(command);
        return command;
    }
};

fn checkSessionTagAllocation(allocator: std.mem.Allocator) !void {
    var environment = std.process.EnvMap.init(allocator);
    defer environment.deinit();
    var profile = try WslLaunch.init(allocator, .{ .bytes = "My Distro" }, .default, &environment);
    defer profile.deinit();
    const command = try profile.commandForSessionAlloc(allocator, "wsl.exe --distribution \"My Distro\"", .{ .bytes = "C:\\Users\\test", .shell_home = true }, "0123456789abcdef0123456789abcdef");
    defer allocator.free(command);
    try std.testing.expect(std.mem.indexOf(u8, command, "--exec env VEX_WSL_SESSION=0123456789abcdef0123456789abcdef ") != null);
    try std.testing.expect(std.mem.indexOf(u8, command, "exec ") != null);
    try std.testing.expectError(error.InvalidWslSessionToken, profile.commandForSessionAlloc(allocator, "wsl.exe", .{ .bytes = "C:\\test", .shell_home = true }, "bad token"));
}

test "WSL panes carry isolated process-query tokens without changing shell startup or leaking on OOM" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkSessionTagAllocation, .{});
}

fn buildWslCommandSuffix(allocator: std.mem.Allocator, command: WslShellCommand, environment: *const std.process.EnvMap) ![]u8 {
    const term = try std.fmt.allocPrint(allocator, "TERM={s}", .{environment.get("TERM") orelse "xterm-256color"});
    defer allocator.free(term);
    const color = try std.fmt.allocPrint(allocator, "COLORTERM={s}", .{environment.get("COLORTERM") orelse "truecolor"});
    defer allocator.free(color);
    const quoted_term = try windows_arguments.quoteWindowsArgument(allocator, term);
    defer allocator.free(quoted_term);
    const quoted_color = try windows_arguments.quoteWindowsArgument(allocator, color);
    defer allocator.free(quoted_color);
    const suffix = switch (command) {
        .default => blk: {
            const script = try windows_arguments.quoteWindowsArgument(allocator, "exec \"${SHELL:-/bin/sh}\" -l");
            defer allocator.free(script);
            break :blk try std.fmt.allocPrint(allocator, "--exec env {s} {s} sh -c {s}", .{ quoted_term, quoted_color, script });
        },
        .program => |program| blk: {
            try windows_arguments.validateShellText(program.arguments);
            const quoted_program = try windows_arguments.quoteWindowsArgument(allocator, program.path.bytes);
            defer allocator.free(quoted_program);
            break :blk try std.fmt.allocPrint(allocator, "--exec env {s} {s} {s} {s}", .{ quoted_term, quoted_color, quoted_program, program.arguments });
        },
    };
    errdefer allocator.free(suffix);
    try windows_arguments.validateShellText(suffix);
    return suffix;
}
