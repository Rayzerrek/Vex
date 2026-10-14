const std = @import("std");
const directory = @import("directory.zig");

/// Built-in Windows shell IDs match WPF. WSL is explicitly unsupported, never remapped to cmd.
pub const BuiltinShellProfile = enum { system, cmd, powershell, pwsh, nu, gitbash };
/// Resolved shell kind; the system alias has already resolved to cmd before reaching launch/render code.
pub const ShellKind = enum(u32) { cmd = 1, powershell = 2, pwsh = 3, nu = 4, gitbash = 5, custom = 6 };
/// Startup-only shell selection borrows parsed argv until the launch owner has cloned it.
pub const ShellRequest = union(enum) {
    builtin: BuiltinShellProfile,
    custom: struct { program: []const u8, arguments: []const u8 },

    /// Accepts --shell ID or --program ABSOLUTE_EXE [--arguments RAW_WINDOWS_ARGUMENTS].
    pub fn parseArguments(arguments: []const []const u8) !ShellRequest {
        var kind: ?BuiltinShellProfile = null;
        var program: ?[]const u8 = null;
        var raw_arguments: ?[]const u8 = null;
        var index: usize = 0;
        while (index < arguments.len) {
            const flag = arguments[index];
            if (arguments.len - index < 2) return error.MissingShellArgument;
            const value = arguments[index + 1];
            if (std.mem.eql(u8, flag, "--shell")) {
                if (kind != null or program != null) return error.ConflictingShellArguments;
                if (std.mem.eql(u8, value, "wsl") or std.mem.startsWith(u8, value, "wsl:")) return error.WslShellUnsupported;
                if (std.mem.eql(u8, value, "custom")) return error.CustomShellRequiresProgram;
                kind = std.meta.stringToEnum(BuiltinShellProfile, value) orelse return error.UnknownShellProfile;
            } else if (std.mem.eql(u8, flag, "--program")) {
                if (kind != null or program != null) return error.ConflictingShellArguments;
                try validateShellProgram(value);
                program = value;
            } else if (std.mem.eql(u8, flag, "--arguments")) {
                if (raw_arguments != null) return error.ConflictingShellArguments;
                try validateShellText(value);
                raw_arguments = value;
            } else return error.UnknownLaunchArgument;
            index += 2;
        }
        if (program) |path| return .{ .custom = .{ .program = path, .arguments = raw_arguments orelse "" } };
        if (raw_arguments != null) return error.CustomShellRequiresProgram;
        return .{ .builtin = kind orelse .system };
    }
};

/// Owns the resolved executable, Windows command line and child environment; immutable after construction.
pub const ShellLaunch = struct {
    allocator: std.mem.Allocator,
    kind: ShellKind,
    program: []const u8,
    command_line: []const u8,
    environment: []const u16,

    /// Resolves cheap canonical/PATH probes once per application, not once per pane.
    pub fn resolve(allocator: std.mem.Allocator, request: ShellRequest, system_directory: []const u8, integration_directory: []const u8, environment: *std.process.EnvMap) !ShellLaunch {
        const kind: ShellKind = switch (request) {
            .builtin => |profile| resolvedShellKind(profile),
            .custom => .custom,
        };
        var resolved_kind = kind;
        const program = switch (request) {
            .custom => |custom| blk: {
                try validateShellProgram(custom.program);
                try validateShellText(custom.arguments);
                if (!try shellFileExists(custom.program)) return error.ShellProgramUnavailable;
                break :blk try allocator.dupe(u8, custom.program);
            },
            .builtin => |builtin| blk: {
                if (try resolveBuiltinProgram(allocator, builtin, system_directory, environment)) |path| break :blk path;
                // Missing Windows profiles resolve to the same system default as WPF.
                resolved_kind = .cmd;
                break :blk (try probeShellPath(allocator, system_directory, "cmd.exe")) orelse return error.SystemShellUnavailable;
            },
        };
        errdefer allocator.free(program);
        const raw_arguments = switch (request) {
            .custom => |custom| custom.arguments,
            .builtin => "",
        };
        if (resolved_kind == .cmd or resolved_kind == .nu) {
            const script = (try probeShellPath(allocator, integration_directory, if (resolved_kind == .cmd) "Vex.cmd" else "Vex.nu")) orelse return error.ShellIntegrationUnavailable;
            allocator.free(script);
        }
        const command_line = try buildShellCommandLine(allocator, resolved_kind, program, integration_directory, raw_arguments);
        errdefer allocator.free(command_line);
        try normalizeTerminalEnvironment(environment);
        const block = try std.process.createWindowsEnvBlock(allocator, environment);
        errdefer allocator.free(block);
        if (block.len > 1024 * 1024) return error.ShellEnvironmentTooLarge;
        return .{ .allocator = allocator, .kind = resolved_kind, .program = program, .command_line = command_line, .environment = block };
    }
    /// Releases all launch storage after the last session has closed.
    pub fn deinit(self: *ShellLaunch) void {
        self.allocator.free(self.program);
        self.allocator.free(self.command_line);
        self.allocator.free(self.environment);
    }
    /// Resolved display names are not raw terminal titles or untrusted profile text.
    pub fn shellName(self: *const ShellLaunch) []const u8 {
        return switch (self.kind) {
            .cmd => "Command Prompt",
            .powershell => "Windows PowerShell",
            .pwsh => "PowerShell 7",
            .nu => "Nushell",
            .gitbash => "Git Bash",
            .custom => "Custom shell",
        };
    }
};

fn validateShellText(bytes: []const u8) !void {
    if (!std.unicode.utf8ValidateSlice(bytes) or std.mem.indexOfScalar(u8, bytes, 0) != null) return error.InvalidShellText;
    var units: usize = 0;
    var iterator = std.unicode.Utf8View.initUnchecked(bytes).iterator();
    while (iterator.nextCodepoint()) |scalar| units += if (scalar > 0xffff) @as(usize, 2) else 1;
    if (units >= 32767) return error.ShellCommandLineTooLong;
}
fn validateShellProgram(program: []const u8) !void {
    _ = try directory.DirectoryPath.parse(program);
    if (std.mem.indexOfScalar(u8, program, '"') != null) return error.InvalidShellProgram;
    try validateShellText(program);
}

/// Quotes one argv element with the Windows CRT rules, including trailing backslashes and embedded quotes.
pub fn quoteWindowsArgument(allocator: std.mem.Allocator, argument: []const u8) ![]u8 {
    try validateShellText(argument);
    var result: std.ArrayList(u8) = .empty;
    errdefer result.deinit(allocator);
    try result.append(allocator, '"');
    var slashes: usize = 0;
    for (argument) |byte| {
        if (byte == '\\') {
            slashes += 1;
            continue;
        }
        try result.appendNTimes(allocator, '\\', if (byte == '"') slashes * 2 + 1 else slashes);
        slashes = 0;
        try result.append(allocator, byte);
    }
    try result.appendNTimes(allocator, '\\', slashes * 2);
    try result.append(allocator, '"');
    return result.toOwnedSlice(allocator);
}

/// Builds only the shell bootstrap; startup never replays a saved command or writes the user's profile.
pub fn buildShellCommandLine(allocator: std.mem.Allocator, kind: ShellKind, program: []const u8, integration_directory: []const u8, raw_arguments: []const u8) ![]u8 {
    try validateShellProgram(program);
    const quoted = try quoteWindowsArgument(allocator, program);
    defer allocator.free(quoted);
    const arguments = switch (kind) {
        .cmd => blk: {
            _ = try directory.DirectoryPath.parse(integration_directory);
            if (std.mem.indexOfAny(u8, integration_directory, "\"%") != null) return error.InvalidShellIntegrationPath;
            break :blk try std.fmt.allocPrint(allocator, "/q /k \"call \"{s}\\Vex.cmd\"\"", .{integration_directory});
        },
        .powershell, .pwsh => blk: {
            const script = try std.unicode.utf8ToUtf16LeAlloc(allocator, @embedFile("integration/Vex.ps1"));
            defer allocator.free(script);
            const bytes = std.mem.sliceAsBytes(script);
            const encoded = try allocator.alloc(u8, std.base64.standard.Encoder.calcSize(bytes.len));
            defer allocator.free(encoded);
            break :blk try std.fmt.allocPrint(allocator, "-NoLogo -NoExit -EncodedCommand {s}", .{std.base64.standard.Encoder.encode(encoded, bytes)});
        },
        .nu => blk: {
            _ = try directory.DirectoryPath.parse(integration_directory);
            const path = try std.fmt.allocPrint(allocator, "{s}\\Vex.nu", .{integration_directory});
            defer allocator.free(path);
            var escaped: std.ArrayList(u8) = .empty;
            defer escaped.deinit(allocator);
            for (path) |byte| {
                if (byte == '\\' or byte == '"') try escaped.append(allocator, '\\');
                try escaped.append(allocator, byte);
            }
            const script = try std.fmt.allocPrint(allocator, "source \"{s}\"", .{escaped.items});
            defer allocator.free(script);
            const argument = try quoteWindowsArgument(allocator, script);
            defer allocator.free(argument);
            break :blk try std.fmt.allocPrint(allocator, "--execute {s}", .{argument});
        },
        .gitbash => try allocator.dupe(u8, "--login -i"),
        .custom => blk: {
            try validateShellText(raw_arguments);
            break :blk try allocator.dupe(u8, raw_arguments);
        },
    };
    defer allocator.free(arguments);
    const command = try std.fmt.allocPrint(allocator, "{s} {s}", .{ quoted, arguments });
    errdefer allocator.free(command);
    try validateShellText(command);
    return command;
}

fn normalizeTerminalEnvironment(environment: *std.process.EnvMap) !void {
    const term = environment.get("TERM") orelse "";
    if (term.len == 0 or std.mem.eql(u8, term, "dumb")) try environment.put("TERM", "xterm-256color");
    if ((environment.get("COLORTERM") orelse "").len == 0) try environment.put("COLORTERM", "truecolor");
}
fn shellFileExists(path: []const u8) !bool {
    const info = std.fs.cwd().statFile(path) catch |err| switch (err) {
        error.FileNotFound, error.NotDir, error.BadPathName => return false,
        else => return err,
    };
    return info.kind == .file;
}
fn probeShellPath(allocator: std.mem.Allocator, root: []const u8, relative: []const u8) !?[]u8 {
    _ = directory.DirectoryPath.parse(root) catch return null;
    const path = try std.fmt.allocPrint(allocator, "{s}\\{s}", .{ root, relative });
    errdefer allocator.free(path);
    if (try shellFileExists(path)) return path;
    allocator.free(path);
    return null;
}
fn shellOnPath(allocator: std.mem.Allocator, environment: *const std.process.EnvMap, executable: []const u8) !?[]u8 {
    var paths = std.mem.splitScalar(u8, environment.get("PATH") orelse "", ';');
    while (paths.next()) |root| if (try probeShellPath(allocator, std.mem.trim(u8, root, " \t\""), executable)) |path| return path;
    return null;
}
fn resolvedShellKind(profile: BuiltinShellProfile) ShellKind {
    return switch (profile) {
        .system, .cmd => .cmd,
        .powershell => .powershell,
        .pwsh => .pwsh,
        .nu => .nu,
        .gitbash => .gitbash,
    };
}
fn resolveBuiltinProgram(allocator: std.mem.Allocator, kind: BuiltinShellProfile, system_directory: []const u8, environment: *const std.process.EnvMap) !?[]u8 {
    switch (kind) {
        .system, .cmd => return probeShellPath(allocator, system_directory, "cmd.exe"),
        .powershell => return probeShellPath(allocator, system_directory, "WindowsPowerShell\\v1.0\\powershell.exe"),
        .pwsh => {
            if (try probeShellPath(allocator, environment.get("ProgramFiles") orelse "", "PowerShell\\7\\pwsh.exe")) |path| return path;
            return shellOnPath(allocator, environment, "pwsh.exe");
        },
        .nu => {
            if (try probeShellPath(allocator, environment.get("USERPROFILE") orelse "", "scoop\\apps\\nu\\current\\nu.exe")) |path| return path;
            return shellOnPath(allocator, environment, "nu.exe");
        },
        .gitbash => {
            for ([_][]const u8{ "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" }) |name| {
                const relative = if (std.mem.eql(u8, name, "LOCALAPPDATA")) "Programs\\Git\\bin\\bash.exe" else "Git\\bin\\bash.exe";
                if (try probeShellPath(allocator, environment.get(name) orelse "", relative)) |path| return path;
            }
            return null;
        },
    }
}

test "shell requests reject duplicates, relative/custom control paths and WSL without guessing" {
    const default = try ShellRequest.parseArguments(&.{});
    try std.testing.expectEqual(.system, default.builtin);
    try std.testing.expectError(error.WslShellUnsupported, ShellRequest.parseArguments(&.{ "--shell", "wsl:/bin/bash" }));
    try std.testing.expectError(error.ConflictingShellArguments, ShellRequest.parseArguments(&.{ "--shell", "cmd", "--program", "C:\\cmd.exe" }));
    try std.testing.expectError(error.CustomShellRequiresProgram, ShellRequest.parseArguments(&.{ "--arguments", "/d" }));
    try std.testing.expectError(error.InvalidDirectoryPath, ShellRequest.parseArguments(&.{ "--program", "cmd.exe" }));
    try std.testing.expectError(error.InvalidShellText, ShellRequest.parseArguments(&.{ "--program", "C:\\cmd.exe", "--arguments", "bad\x00arg" }));
}

test "Windows quoting handles empty/space/Unicode/quote and trailing backslash arguments" {
    for ([_]struct { input: []const u8, output: []const u8 }{
        .{ .input = "", .output = "\"\"" },                           .{ .input = "a b", .output = "\"a b\"" },
        .{ .input = "Żółć", .output = "\"Żółć\"" },           .{ .input = "a\"b", .output = "\"a\\\"b\"" },
        .{ .input = "C:\\folder\\", .output = "\"C:\\folder\\\\\"" },
    }) |fixture| {
        const quoted = try quoteWindowsArgument(std.testing.allocator, fixture.input);
        defer std.testing.allocator.free(quoted);
        try std.testing.expectEqualStrings(fixture.output, quoted);
    }
}

fn checkShellBuildAllocations(allocator: std.mem.Allocator) !void {
    for ([_]ShellKind{ .cmd, .powershell, .pwsh, .nu, .gitbash, .custom }) |kind| {
        const command = try buildShellCommandLine(allocator, kind, "C:\\Program Files\\shell.exe", "C:\\Żółć pane\\ShellIntegration", "/d");
        defer allocator.free(command);
        try std.testing.expect(std.mem.startsWith(u8, command, "\"C:\\Program Files\\shell.exe\" "));
    }
}
test "all shell launch quoting/encoding allocation failures release intermediate buffers" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkShellBuildAllocations, .{});
}

fn checkShellResolveAllocations(allocator: std.mem.Allocator, root: []const u8) !void {
    var environment = std.process.EnvMap.init(allocator);
    defer environment.deinit();
    const missing = try std.fmt.allocPrint(allocator, "{s}\\missing", .{root});
    defer allocator.free(missing);
    const integration = try std.fmt.allocPrint(allocator, "{s}\\integration", .{root});
    defer allocator.free(integration);
    const path = try std.fmt.allocPrint(allocator, ";relative;\"{s}\\bin\"", .{root});
    defer allocator.free(path);
    try environment.put("ProgramFiles", missing);
    try environment.put("ProgramFiles(x86)", missing);
    try environment.put("USERPROFILE", root);
    try environment.put("LOCALAPPDATA", root);
    try environment.put("PATH", path);
    for ([_]BuiltinShellProfile{ .system, .cmd, .powershell, .pwsh, .nu, .gitbash }) |kind| {
        var launch = try ShellLaunch.resolve(allocator, .{ .builtin = kind }, root, integration, &environment);
        defer launch.deinit();
        try std.testing.expectEqual(resolvedShellKind(kind), launch.kind);
        try std.testing.expect(launch.environment.len >= 2);
        try std.testing.expectEqual(@as(u16, 0), launch.environment[launch.environment.len - 1]);
        try std.testing.expectEqual(@as(u16, 0), launch.environment[launch.environment.len - 2]);
    }
    try environment.put("PATH", "");
    var fallback = try ShellLaunch.resolve(allocator, .{ .builtin = .pwsh }, root, integration, &environment);
    defer fallback.deinit();
    try std.testing.expectEqual(.cmd, fallback.kind);
    if (ShellLaunch.resolve(allocator, .{ .builtin = .cmd }, root, missing, &environment)) |unexpected| {
        var owned = unexpected;
        defer owned.deinit();
        return error.ExpectedMissingShellIntegration;
    } else |err| {
        if (err == error.OutOfMemory) return err;
        try std.testing.expectEqual(error.ShellIntegrationUnavailable, err);
    }
    var custom = try ShellLaunch.resolve(allocator, .{ .custom = .{ .program = fallback.program, .arguments = "/d" } }, root, missing, &environment);
    defer custom.deinit();
    try std.testing.expectEqual(.custom, custom.kind);
    try std.testing.expect(std.mem.endsWith(u8, custom.command_line, " /d"));
}

test "canonical/PATH resolution, missing-profile fallback and environment allocation failures release every owner" {
    var fixture = std.testing.tmpDir(.{});
    defer fixture.cleanup();
    for ([_][]const u8{ "integration", "bin", "WindowsPowerShell/v1.0", "scoop/apps/nu/current", "Programs/Git/bin" }) |path| try fixture.dir.makePath(path);
    for ([_][]const u8{ "cmd.exe", "integration/Vex.cmd", "integration/Vex.nu", "bin/pwsh.exe", "WindowsPowerShell/v1.0/powershell.exe", "scoop/apps/nu/current/nu.exe", "Programs/Git/bin/bash.exe" }) |path| {
        const file = try fixture.dir.createFile(path, .{});
        file.close();
    }
    const root = try fixture.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(root);
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkShellResolveAllocations, .{root});
}

test "launch lengths count UTF-16 units after argv quoting, not only the raw argument length" {
    const long_arguments = [_]u8{'a'} ** 32760;
    try std.testing.expectError(error.ShellCommandLineTooLong, buildShellCommandLine(std.testing.allocator, .custom, "C:\\cmd.exe", "", &long_arguments));
    try std.testing.expectError(error.InvalidShellIntegrationPath, buildShellCommandLine(std.testing.allocator, .cmd, "C:\\cmd.exe", "C:\\percent%path", ""));
}

test "terminal environment defaults preserve explicit user overrides" {
    var environment = std.process.EnvMap.init(std.testing.allocator);
    defer environment.deinit();
    try environment.put("TERM", "dumb");
    try normalizeTerminalEnvironment(&environment);
    try std.testing.expectEqualStrings("xterm-256color", environment.get("TERM") orelse return error.MissingTerm);
    try std.testing.expectEqualStrings("truecolor", environment.get("COLORTERM") orelse return error.MissingColorTerm);
    try environment.put("TERM", "vt100");
    try environment.put("COLORTERM", "user-value");
    try normalizeTerminalEnvironment(&environment);
    try std.testing.expectEqualStrings("vt100", environment.get("TERM") orelse return error.MissingTerm);
    try std.testing.expectEqualStrings("user-value", environment.get("COLORTERM") orelse return error.MissingColorTerm);
}
