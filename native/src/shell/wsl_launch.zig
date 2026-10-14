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
        const linux = if (try wsl.networkDistribution(path.bytes) != null) try wsl.linuxDirectoryFromNetworkAlloc(allocator, path.bytes, self.distribution) else null;
        defer if (linux) |owned| allocator.free(owned);
        if (linux == null and (!std.ascii.isAlphabetic(path.bytes[0]) or path.bytes[1] != ':')) return error.UnsupportedWslWorkingDirectory;
        const quoted_directory = try windows_arguments.quoteWindowsArgument(allocator, linux orelse path.bytes);
        defer allocator.free(quoted_directory);
        const command = try std.fmt.allocPrint(allocator, "{s} --cd {s} {s}", .{ prefix, quoted_directory, self.suffix });
        errdefer allocator.free(command);
        try windows_arguments.validateShellText(command);
        return command;
    }
};

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
