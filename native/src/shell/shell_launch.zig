const std = @import("std");
const directory = @import("directory.zig");
const wsl = @import("wsl_paths.zig");
const wsl_launch = @import("wsl_launch.zig");
const windows_arguments = @import("windows_arguments.zig");
const quoteWindowsArgument = windows_arguments.quoteWindowsArgument;
const validateShellText = windows_arguments.validateShellText;
const WslShellCommand = wsl_launch.WslShellCommand;

/// Built-in Windows shell IDs match WPF; WSL uses a separate launch request, never a cmd fallback.
pub const BuiltinShellProfile = enum { system, cmd, powershell, pwsh, nu, gitbash };
/// Resolved shell kind; the system alias has already resolved to cmd before reaching launch/render code.
pub const ShellKind = enum(u32) {
    cmd = 1,
    powershell = 2,
    pwsh = 3,
    nu = 4,
    gitbash = 5,
    custom = 6,
    wsl = 7,

    /// Human-readable shell label matching WPF ShellProfile display names.
    pub fn displayName(self: ShellKind) []const u8 {
        return switch (self) {
            .cmd => "Command Prompt",
            .powershell => "Windows PowerShell",
            .pwsh => "PowerShell 7",
            .nu => "Nushell",
            .gitbash => "Git Bash",
            .custom => "Custom shell",
            .wsl => "WSL",
        };
    }

    /// Secondary subtitle shown in the Shell Picker dropdown.
    pub fn subtitle(self: ShellKind) []const u8 {
        return switch (self) {
            .cmd => "cmd.exe",
            .powershell => "powershell.exe",
            .pwsh => "pwsh.exe",
            .nu => "nu.exe",
            .gitbash => "bash.exe --login -i",
            .custom => "custom program",
            .wsl => "wsl.exe",
        };
    }

    /// Maps built-in Windows shell kinds back to their profile enum.
    pub fn builtinProfile(self: ShellKind) ?BuiltinShellProfile {
        return switch (self) {
            .cmd => .cmd,
            .powershell => .powershell,
            .pwsh => .pwsh,
            .nu => .nu,
            .gitbash => .gitbash,
            .custom, .wsl => null,
        };
    }
};
/// Unresolved automatic WSL selection or a parsed explicit/directory distribution; resolved before launch construction.
pub const WslDistributionSelection = union(enum) { automatic, named: wsl.WslDistribution };
/// Startup-only shell selection borrows parsed argv until the launch owner has cloned it.
pub const ShellRequest = union(enum) {
    builtin: BuiltinShellProfile,
    custom: struct { program: []const u8, arguments: []const u8 },
    wsl: struct {
        distribution: WslDistributionSelection,
        command: WslShellCommand,
    },

    /// Accepts Windows/custom programs or --shell wsl[:/absolute/program] with optional --distribution NAME.
    pub fn parseArguments(arguments: []const []const u8) !ShellRequest {
        var kind: ?union(enum) { windows: BuiltinShellProfile, wsl: ?wsl.LinuxProgram } = null;
        var program: ?[]const u8 = null;
        var distribution: ?wsl.WslDistribution = null;
        var raw_arguments: ?[]const u8 = null;
        var index: usize = 0;
        while (index < arguments.len) {
            const flag = arguments[index];
            if (arguments.len - index < 2) return error.MissingShellArgument;
            const value = arguments[index + 1];
            if (std.mem.eql(u8, flag, "--shell")) {
                if (kind != null or program != null) return error.ConflictingShellArguments;
                if (std.mem.eql(u8, value, "wsl")) {
                    kind = .{ .wsl = null };
                } else if (std.mem.startsWith(u8, value, "wsl:")) {
                    kind = .{ .wsl = try wsl.LinuxProgram.parse(value[4..]) };
                } else {
                    if (std.mem.eql(u8, value, "custom")) return error.CustomShellRequiresProgram;
                    kind = .{ .windows = std.meta.stringToEnum(BuiltinShellProfile, value) orelse return error.UnknownShellProfile };
                }
            } else if (std.mem.eql(u8, flag, "--program")) {
                if (kind != null or program != null) return error.ConflictingShellArguments;
                try validateShellProgram(value);
                program = value;
            } else if (std.mem.eql(u8, flag, "--distribution")) {
                if (distribution != null) return error.ConflictingShellArguments;
                distribution = try wsl.WslDistribution.parse(value);
            } else if (std.mem.eql(u8, flag, "--arguments")) {
                if (raw_arguments != null) return error.ConflictingShellArguments;
                try validateShellText(value);
                raw_arguments = value;
            } else return error.UnknownLaunchArgument;
            index += 2;
        }
        if (kind) |selected| if (selected == .wsl) {
            const command: WslShellCommand = if (selected.wsl) |path| .{ .program = .{ .path = path, .arguments = raw_arguments orelse "" } } else blk: {
                if (raw_arguments != null) return error.WslArgumentsRequireProgram;
                break :blk .default;
            };
            return .{ .wsl = .{ .distribution = if (distribution) |name| .{ .named = name } else .automatic, .command = command } };
        };
        if (distribution != null) return error.DistributionRequiresWsl;
        if (program) |path| return .{ .custom = .{ .program = path, .arguments = raw_arguments orelse "" } };
        if (raw_arguments != null) return error.CustomShellRequiresProgram;
        return .{ .builtin = if (kind) |selected| selected.windows else .system };
    }
    /// WSL UNC working directories pin their distribution; incompatible shell requests fail rather than remap.
    pub fn forWorkingDirectory(self: ShellRequest, path: directory.DirectoryPath) !ShellRequest {
        const path_distribution = (try wsl.networkDistribution(path.bytes)) orelse return self;
        switch (self) {
            .wsl => |profile| {
                if (profile.distribution == .named and !std.ascii.eqlIgnoreCase(profile.distribution.named.bytes, path_distribution.bytes)) return error.WslDistributionMismatch;
                var selected = self;
                if (profile.distribution == .automatic) selected.wsl.distribution = .{ .named = path_distribution };
                return selected;
            },
            .builtin => |profile| if (profile == .system) return .{ .wsl = .{ .distribution = .{ .named = path_distribution }, .command = .default } },
            .custom => {},
        }
        return error.WslDirectoryRequiresShell;
    }
};

/// Owns the resolved executable, Windows command line and child environment; immutable after construction.
pub const ShellLaunch = struct {
    allocator: std.mem.Allocator,
    kind: ShellKind,
    program: []const u8,
    command_line: []const u8,
    environment: []const u16,
    backend: union(enum) { windows, wsl: wsl_launch.WslLaunch } = .windows,

    /// Resolves cheap canonical/PATH probes once per application, not once per pane.
    pub fn resolve(allocator: std.mem.Allocator, request: ShellRequest, system_directory: []const u8, environment: *std.process.EnvMap) !ShellLaunch {
        const kind: ShellKind = switch (request) {
            .builtin => |profile| resolvedShellKind(profile),
            .custom => .custom,
            .wsl => .wsl,
        };
        var resolved_kind = kind;
        const program = switch (request) {
            .custom => |custom| blk: {
                try validateShellProgram(custom.program);
                try validateShellText(custom.arguments);
                if (!try shellFileExists(custom.program)) return error.ShellProgramUnavailable;
                break :blk try allocator.dupe(u8, custom.program);
            },
            .wsl => (try probeShellPath(allocator, system_directory, "wsl.exe")) orelse return error.WslLauncherUnavailable,
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
            .builtin, .wsl => "",
        };
        try normalizeTerminalEnvironment(environment);
        if (resolved_kind == .cmd) {
            const prompt = environment.get("PROMPT") orelse "$P$G";
            // Keep user prompt text out of cmd's command parser.
            const integrated = try std.fmt.allocPrint(allocator, "$E]133;A$E\\{s}", .{if (prompt.len == 0) "$P$G" else prompt});
            defer allocator.free(integrated);
            try environment.put("PROMPT", integrated);
        }
        var backend: @FieldType(ShellLaunch, "backend") = switch (request) {
            .wsl => |request_wsl| .{ .wsl = try wsl_launch.WslLaunch.init(allocator, switch (request_wsl.distribution) {
                .named => |name| name,
                .automatic => return error.WslDefaultDistributionUnavailable,
            }, request_wsl.command, environment) },
            else => .windows,
        };
        errdefer if (backend == .wsl) backend.wsl.deinit();
        const command_line = switch (backend) {
            .wsl => |*profile| try profile.commandPrefixAlloc(allocator, program),
            .windows => try buildShellCommandLine(allocator, resolved_kind, program, raw_arguments),
        };
        errdefer allocator.free(command_line);
        const block = try std.process.createWindowsEnvBlock(allocator, environment);
        errdefer allocator.free(block);
        if (block.len > 1024 * 1024) return error.ShellEnvironmentTooLarge;
        return .{ .allocator = allocator, .kind = resolved_kind, .program = program, .command_line = command_line, .environment = block, .backend = backend };
    }
    /// Releases all launch storage after the last session has closed.
    pub fn deinit(self: *ShellLaunch) void {
        self.allocator.free(self.program);
        self.allocator.free(self.command_line);
        self.allocator.free(self.environment);
        if (self.backend == .wsl) self.backend.wsl.deinit();
    }
    /// Borrows launch metadata until the final pane directory owner has been released.
    pub fn directoryContext(self: *const ShellLaunch) directory.DirectoryContext {
        return switch (self.backend) {
            .windows => .windows,
            .wsl => |profile| .{ .wsl = profile.distribution },
        };
    }
    /// Creates fresh command storage per pane; Windows paths are passed to WSL's own --cd conversion.
    pub fn commandForDirectoryAlloc(self: *const ShellLaunch, allocator: std.mem.Allocator, path: directory.DirectoryPath) ![]u8 {
        if (self.backend == .windows) {
            if (try wsl.networkDistribution(path.bytes) != null) return error.WslDirectoryRequiresShell;
            return allocator.dupe(u8, self.command_line);
        }
        return self.backend.wsl.commandForDirectoryAlloc(allocator, self.command_line, path);
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
            .wsl => "WSL",
        };
    }
};

fn validateShellProgram(program: []const u8) !void {
    _ = try directory.DirectoryPath.parse(program);
    if (std.mem.indexOfScalar(u8, program, '"') != null) return error.InvalidShellProgram;
    try validateShellText(program);
}

/// Builds only the shell bootstrap; startup never replays a saved command or writes the user's profile.
pub fn buildShellCommandLine(allocator: std.mem.Allocator, kind: ShellKind, program: []const u8, raw_arguments: []const u8) ![]u8 {
    try validateShellProgram(program);
    const quoted = try quoteWindowsArgument(allocator, program);
    defer allocator.free(quoted);
    const arguments = switch (kind) {
        .cmd => try allocator.dupe(u8, "/q /k"),
        .powershell, .pwsh => blk: {
            const script = try std.unicode.utf8ToUtf16LeAlloc(allocator, @embedFile("integration/Vex.ps1"));
            defer allocator.free(script);
            const bytes = std.mem.sliceAsBytes(script);
            const encoded = try allocator.alloc(u8, std.base64.standard.Encoder.calcSize(bytes.len));
            defer allocator.free(encoded);
            break :blk try std.fmt.allocPrint(allocator, "-NoLogo -NoExit -EncodedCommand {s}", .{std.base64.standard.Encoder.encode(encoded, bytes)});
        },
        .nu => blk: {
            const argument = try quoteWindowsArgument(allocator, @embedFile("integration/Vex.nu"));
            defer allocator.free(argument);
            break :blk try std.fmt.allocPrint(allocator, "--execute {s}", .{argument});
        },
        .wsl => return error.WslLaunchRequiresDirectory,
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

/// Checks the executable only; shell bootstraps are embedded.
pub fn isBuiltinShellAvailable(allocator: std.mem.Allocator, kind: BuiltinShellProfile, system_directory: []const u8, environment: *const std.process.EnvMap) !bool {
    const program = (try resolveBuiltinProgram(allocator, kind, system_directory, environment)) orelse return false;
    defer allocator.free(program);
    return true;
}

/// Checks whether wsl.exe exists in the Windows system directory.
pub fn isWslLauncherAvailable(allocator: std.mem.Allocator, system_directory: []const u8) !bool {
    const path = (try probeShellPath(allocator, system_directory, "wsl.exe")) orelse return false;
    allocator.free(path);
    return true;
}

test "shell requests reject duplicates and relative/custom control paths without guessing" {
    const default = try ShellRequest.parseArguments(&.{});
    try std.testing.expectEqual(.system, default.builtin);
    try std.testing.expect((try ShellRequest.parseArguments(&.{ "--shell", "wsl:/bin/bash" })).wsl.distribution == .automatic);
    try std.testing.expectError(error.ConflictingShellArguments, ShellRequest.parseArguments(&.{ "--shell", "cmd", "--program", "C:\\cmd.exe" }));
    try std.testing.expectError(error.CustomShellRequiresProgram, ShellRequest.parseArguments(&.{ "--arguments", "/d" }));
    try std.testing.expectError(error.InvalidDirectoryPath, ShellRequest.parseArguments(&.{ "--program", "cmd.exe" }));
    try std.testing.expectError(error.InvalidShellText, ShellRequest.parseArguments(&.{ "--program", "C:\\cmd.exe", "--arguments", "bad\x00arg" }));
}

fn checkShellBuildAllocations(allocator: std.mem.Allocator) !void {
    for ([_]ShellKind{ .cmd, .powershell, .pwsh, .nu, .gitbash, .custom }) |kind| {
        const command = try buildShellCommandLine(allocator, kind, "C:\\Program Files\\shell.exe", "/d");
        defer allocator.free(command);
        try std.testing.expect(std.mem.startsWith(u8, command, "\"C:\\Program Files\\shell.exe\" "));
    }
}
test "shell bootstraps do not reference external integration files" {
    const allocator = std.testing.allocator;
    const cmd = try buildShellCommandLine(allocator, .cmd, "C:\\cmd.exe", "");
    defer allocator.free(cmd);
    try std.testing.expectEqualStrings("\"C:\\cmd.exe\" /q /k", cmd);
    const nu = try buildShellCommandLine(allocator, .nu, "C:\\nu.exe", "");
    defer allocator.free(nu);
    try std.testing.expect(std.mem.indexOf(u8, nu, "shell_integration.osc133") != null);
    try std.testing.expect(std.mem.indexOf(u8, nu, "shell_integration.osc7") != null);
    try std.testing.expect(std.mem.indexOf(u8, nu, "source ") == null);
}

test "all shell launch quoting/encoding allocation failures release intermediate buffers" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkShellBuildAllocations, .{});
}

fn checkShellResolveAllocations(allocator: std.mem.Allocator, root: []const u8) !void {
    var environment = std.process.EnvMap.init(allocator);
    defer environment.deinit();
    const missing = try std.fmt.allocPrint(allocator, "{s}\\missing", .{root});
    defer allocator.free(missing);
    const path = try std.fmt.allocPrint(allocator, ";relative;\"{s}\\bin\"", .{root});
    defer allocator.free(path);
    try environment.put("ProgramFiles", missing);
    try environment.put("ProgramFiles(x86)", missing);
    try environment.put("USERPROFILE", root);
    try environment.put("LOCALAPPDATA", root);
    try environment.put("PATH", path);
    for ([_]BuiltinShellProfile{ .system, .cmd, .powershell, .pwsh, .nu, .gitbash }) |kind| {
        var launch = try ShellLaunch.resolve(allocator, .{ .builtin = kind }, root, &environment);
        defer launch.deinit();
        try std.testing.expectEqual(resolvedShellKind(kind), launch.kind);
        if (launch.kind == .cmd) {
            try std.testing.expect(std.mem.startsWith(u8, environment.get("PROMPT") orelse return error.MissingPrompt, "$E]133;A$E\\"));
        }
        try std.testing.expect(launch.environment.len >= 2);
        try std.testing.expectEqual(@as(u16, 0), launch.environment[launch.environment.len - 1]);
        try std.testing.expectEqual(@as(u16, 0), launch.environment[launch.environment.len - 2]);
    }
    try environment.put("PATH", "");
    var fallback = try ShellLaunch.resolve(allocator, .{ .builtin = .pwsh }, root, &environment);
    defer fallback.deinit();
    try std.testing.expectEqual(.cmd, fallback.kind);
    var custom = try ShellLaunch.resolve(allocator, .{ .custom = .{ .program = fallback.program, .arguments = "/d" } }, root, &environment);
    defer custom.deinit();
    try std.testing.expectEqual(.custom, custom.kind);
    try std.testing.expect(std.mem.endsWith(u8, custom.command_line, " /d"));
}

test "canonical/PATH resolution, missing-profile fallback and environment allocation failures release every owner" {
    var fixture = std.testing.tmpDir(.{});
    defer fixture.cleanup();
    for ([_][]const u8{ "bin", "WindowsPowerShell/v1.0", "scoop/apps/nu/current", "Programs/Git/bin" }) |path| try fixture.dir.makePath(path);
    for ([_][]const u8{ "cmd.exe", "bin/pwsh.exe", "WindowsPowerShell/v1.0/powershell.exe", "scoop/apps/nu/current/nu.exe", "Programs/Git/bin/bash.exe" }) |path| {
        const file = try fixture.dir.createFile(path, .{});
        file.close();
    }
    const root = try fixture.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(root);
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkShellResolveAllocations, .{root});
}

test "launch lengths count UTF-16 units after argv quoting, not only the raw argument length" {
    const long_arguments = [_]u8{'a'} ** 32760;
    try std.testing.expectError(error.ShellCommandLineTooLong, buildShellCommandLine(std.testing.allocator, .custom, "C:\\cmd.exe", &long_arguments));
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

test "WSL requests retain automatic or explicit distribution selection and arbitrary absolute Linux programs" {
    const default = try ShellRequest.parseArguments(&.{ "--distribution", "Ubuntu", "--shell", "wsl" });
    try std.testing.expectEqualStrings("Ubuntu", default.wsl.distribution.named.bytes);
    try std.testing.expect(default.wsl.command == .default);
    const explicit = try ShellRequest.parseArguments(&.{ "--shell", "wsl:/opt/Żółć program", "--arguments", "--interactive", "--distribution", "Ubuntu" });
    try std.testing.expectEqualStrings("/opt/Żółć program", explicit.wsl.command.program.path.bytes);
    try std.testing.expectEqualStrings("--interactive", explicit.wsl.command.program.arguments);
    try std.testing.expectError(error.DistributionRequiresWsl, ShellRequest.parseArguments(&.{ "--shell", "cmd", "--distribution", "Ubuntu" }));
    try std.testing.expectError(error.WslArgumentsRequireProgram, ShellRequest.parseArguments(&.{ "--shell", "wsl", "--distribution", "Ubuntu", "--arguments", "-l" }));
    try std.testing.expectError(error.InvalidLinuxDirectory, ShellRequest.parseArguments(&.{ "--shell", "wsl:relative", "--distribution", "Ubuntu" }));
    try std.testing.expectError(error.InvalidWslProgram, ShellRequest.parseArguments(&.{ "--shell", "wsl:/", "--distribution", "Ubuntu" }));
}

test "working directory selection keeps Windows defaults and pins WSL paths without registry access" {
    const windows_path = try directory.DirectoryPath.parse("C:\\work");
    const linux_path = try directory.DirectoryPath.parse("\\\\wsl$\\Ubuntu\\home\\user");
    const default = try ShellRequest.parseArguments(&.{});
    try std.testing.expectEqual(.system, (try default.forWorkingDirectory(windows_path)).builtin);
    const selected_default = try default.forWorkingDirectory(linux_path);
    try std.testing.expectEqualStrings("Ubuntu", selected_default.wsl.distribution.named.bytes);
    try std.testing.expect(selected_default.wsl.command == .default);
    const automatic = try ShellRequest.parseArguments(&.{ "--shell", "wsl:/usr/bin/fish", "--arguments", "--interactive" });
    try std.testing.expect((try automatic.forWorkingDirectory(windows_path)).wsl.distribution == .automatic);
    const selected = try automatic.forWorkingDirectory(linux_path);
    try std.testing.expectEqualStrings("Ubuntu", selected.wsl.distribution.named.bytes);
    try std.testing.expectEqualStrings("/usr/bin/fish", selected.wsl.command.program.path.bytes);
    try std.testing.expectEqualStrings("--interactive", selected.wsl.command.program.arguments);
    const explicit = try ShellRequest.parseArguments(&.{ "--shell", "wsl", "--distribution", "Ubuntu" });
    try std.testing.expectEqualStrings("Ubuntu", (try explicit.forWorkingDirectory(linux_path)).wsl.distribution.named.bytes);
    const other = try ShellRequest.parseArguments(&.{ "--shell", "wsl", "--distribution", "Debian" });
    try std.testing.expectError(error.WslDistributionMismatch, other.forWorkingDirectory(linux_path));
    try std.testing.expectError(error.WslDirectoryRequiresShell, (try ShellRequest.parseArguments(&.{ "--shell", "cmd" })).forWorkingDirectory(linux_path));
    try std.testing.expectError(error.WslDirectoryRequiresShell, (try ShellRequest.parseArguments(&.{ "--program", "C:\\custom.exe" })).forWorkingDirectory(linux_path));
}

fn checkWslLaunchAllocations(allocator: std.mem.Allocator, root: []const u8) !void {
    var environment = std.process.EnvMap.init(allocator);
    defer environment.deinit();
    for ([_]ShellRequest{
        try ShellRequest.parseArguments(&.{ "--shell", "wsl", "--distribution", "Ubuntu" }),
        try ShellRequest.parseArguments(&.{ "--shell", "wsl:/opt/O'Brien shell", "--distribution", "Ubuntu", "--arguments", "--interactive \"an argument\"" }),
        try ShellRequest.parseArguments(&.{ "--shell", "wsl:/usr/bin/nu", "--distribution", "Ubuntu Test" }),
    }) |request| {
        var launch = try ShellLaunch.resolve(allocator, request, root, &environment);
        defer launch.deinit();
        try std.testing.expectEqual(.wsl, launch.kind);
        try std.testing.expectEqualStrings(request.wsl.distribution.named.bytes, launch.directoryContext().wsl.bytes);
        const network = try wsl.networkDirectoryFromLinuxAlloc(allocator, request.wsl.distribution.named, try wsl.LinuxDirectory.parse("/home/Żółć O'Brien"));
        defer allocator.free(network);
        const command = try launch.commandForDirectoryAlloc(allocator, try directory.DirectoryPath.parse(network));
        defer allocator.free(command);
        try std.testing.expect(std.mem.indexOf(u8, command, "--cd \"/home/Żółć O'Brien\" --exec env \"TERM=xterm-256color\" \"COLORTERM=truecolor\"") != null);
        if (request.wsl.command == .program) {
            try std.testing.expect(std.mem.indexOf(u8, command, request.wsl.command.program.path.bytes) != null);
            try std.testing.expect(std.mem.indexOf(u8, command, "sh -c") == null);
            try std.testing.expect(std.mem.indexOf(u8, command, " -l") == null);
            try std.testing.expect(std.mem.endsWith(u8, command, request.wsl.command.program.arguments));
        }
        const home = try launch.commandForDirectoryAlloc(allocator, .{ .bytes = "C:\\Users\\WindowsUser", .shell_home = true });
        defer allocator.free(home);
        try std.testing.expect(std.mem.indexOf(u8, home, " --cd ~ --exec env ") != null);
        try std.testing.expect(std.mem.indexOf(u8, home, "WindowsUser") == null);
        const drive = try launch.commandForDirectoryAlloc(allocator, try directory.DirectoryPath.parse("D:\\custom mount\\project"));
        defer allocator.free(drive);
        try std.testing.expect(std.mem.indexOf(u8, drive, "--cd \"D:\\custom mount\\project\"") != null);
        try std.testing.expectError(error.WslDistributionMismatch, launch.commandForDirectoryAlloc(allocator, try directory.DirectoryPath.parse("\\\\wsl$\\Debian\\home")));
        try std.testing.expectError(error.UnsupportedWslWorkingDirectory, launch.commandForDirectoryAlloc(allocator, try directory.DirectoryPath.parse("\\\\server\\share")));
    }
}

test "WSL launch ownership, per-pane directories and allocation failures are independent of Windows profiles" {
    var fixture = std.testing.tmpDir(.{});
    defer fixture.cleanup();
    const file = try fixture.dir.createFile("wsl.exe", .{});
    file.close();
    const root = try fixture.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(root);
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkWslLaunchAllocations, .{root});
    var unresolved_environment = std.process.EnvMap.init(std.testing.allocator);
    defer unresolved_environment.deinit();
    const unresolved = try ShellRequest.parseArguments(&.{ "--shell", "wsl" });
    try std.testing.expectError(error.WslDefaultDistributionUnavailable, ShellLaunch.resolve(std.testing.allocator, unresolved, root, &unresolved_environment));
    try fixture.dir.deleteFile("wsl.exe");
    const cmd = try fixture.dir.createFile("cmd.exe", .{});
    cmd.close();
    var environment = std.process.EnvMap.init(std.testing.allocator);
    defer environment.deinit();
    const request = try ShellRequest.parseArguments(&.{ "--shell", "wsl", "--distribution", "Ubuntu" });
    try std.testing.expectError(error.WslLauncherUnavailable, ShellLaunch.resolve(std.testing.allocator, request, root, &environment));
}
