const std = @import("std");
const TerminalApplication = @import("terminal_application.zig").TerminalApplication;
const platform = @import("../windows/windows_ffi.zig");
const shell = @import("../shell/shell_launch.zig");
const wsl_registry = @import("../windows/wsl_registry.zig");
const custom_profiles = @import("../shell/custom_profiles.zig");

/// Refresh only when opening shell-selection UI or validating an external configuration change.
/// Modal updates reuse the snapshot so sliders and theme previews never scan PATH.
pub fn discoverAvailableShells(self: *TerminalApplication) void {
    const system_directory = platform.systemDirectoryAlloc(self.allocator) catch null;
    defer if (system_directory) |dir| self.allocator.free(dir);
    var environment = platform.shellEnvironment(self.allocator) catch null;
    defer if (environment) |*env| env.deinit();

    const candidates = [_]shell.ShellKind{ .pwsh, .powershell, .cmd, .nu, .gitbash, .wsl };
    var count: u8 = 0;
    for (candidates) |candidate| {
        var available = candidate == self.launch.kind;
        if (!available) if (system_directory) |sys_dir| if (environment) |*env_ptr| {
            if (candidate == .wsl) {
                if (shell.isWslLauncherAvailable(self.allocator, sys_dir) catch false) {
                    if (wsl_registry.readDefaultWslDistributionAlloc(self.allocator)) |distro| {
                        self.allocator.free(distro.bytes);
                        available = true;
                    } else |_| {}
                }
            } else if (candidate.builtinProfile()) |profile| {
                available = shell.isBuiltinShellAvailable(self.allocator, profile, sys_dir, env_ptr) catch false;
            }
        };
        if (available and count < self.available_shells.len) {
            self.available_shells[count] = candidate;
            count += 1;
        }
    }
    if (self.launch.kind == .custom) {
        self.available_shells[count] = .custom;
        count += 1;
    }
    for (self.custom_shells.profiles.items) |profile| {
        self.available_shells[count] = @enumFromInt(profile.id);
        count += 1;
    }
    if (count == 0) {
        self.available_shells[0] = self.launch.kind;
        count = 1;
    }
    self.available_shell_count = count;
}
pub fn shellLabel(self: *const TerminalApplication, kind: shell.ShellKind) []const u8 {
    if (self.custom_shells.find(@intFromEnum(kind))) |profile| return profile.name;
    return if (kind == .custom) self.launch.shellName() else kind.displayName();
}
pub fn shellDetail(self: *const TerminalApplication, kind: shell.ShellKind) []const u8 {
    if (self.custom_shells.find(@intFromEnum(kind))) |profile| return profile.displayProgram();
    return if (kind == .custom) self.launch.program else kind.subtitle();
}
pub fn addCustomShell(self: *TerminalApplication, name: []const u8, program: []const u8, arguments: []const u8) !void {
    const edit_lock = try custom_profiles.Catalog.editLock(self.allocator, self.settings_path);
    defer edit_lock.close();
    var latest = custom_profiles.Catalog.load(self.allocator, self.settings_path, &self.settings_diagnostic) catch |err| {
        self.custom_shell_load_error = err;
        return if (err == error.OutOfMemory) err else error.InvalidLuaShellProfiles;
    };
    defer latest.deinit();
    if (@intFromEnum(self.settings.default_shell) >= 256 and latest.find(@intFromEnum(self.settings.default_shell)) == null) return error.UnavailableShellSelection;
    const id = @max(try latest.nextId(), try self.custom_shells.nextId());
    const profile: custom_profiles.Profile = .{ .id = id, .name = name, .program = program, .arguments = arguments };
    try profile.validate();
    var request = try profile.request();
    const distribution = if (request == .wsl) try wsl_registry.readDefaultWslDistributionAlloc(self.allocator) else null;
    defer if (distribution) |value| self.allocator.free(value.bytes);
    if (distribution) |value| request.wsl.distribution = .{ .named = value };
    const system_directory = try platform.systemDirectoryAlloc(self.allocator);
    defer self.allocator.free(system_directory);
    var environment = try platform.shellEnvironment(self.allocator);
    defer environment.deinit();
    var launch = try shell.ShellLaunch.resolve(self.allocator, request, system_directory, &environment);
    defer launch.deinit();
    try latest.appendAndSave(self.settings_path, profile);
    const previous = self.custom_shells;
    self.custom_shells = latest;
    latest = previous;
    self.custom_shell_load_error = null;
    self.discoverAvailableShells();
}
pub fn resolveShellLaunch(self: *TerminalApplication, kind: shell.ShellKind) !*const shell.ShellLaunch {
    const custom = self.custom_shells.find(@intFromEnum(kind));
    if (custom) |profile| {
        if (self.launch.matchesProfile(profile.id, profile.name, profile.program, profile.arguments) and (self.launch.programAvailable() catch false)) return &self.launch;
        for (self.shell_launches.items) |cached| {
            if (cached.matchesProfile(profile.id, profile.name, profile.program, profile.arguments) and (cached.programAvailable() catch false)) return cached;
        }
    } else {
        if (kind == self.launch.kind) return &self.launch;
        if (@intFromEnum(kind) >= 256) return error.UnavailableShellSelection;
        for (self.shell_launches.items) |cached| if (cached.kind == kind) return cached;
    }
    try self.shell_launches.ensureUnusedCapacity(self.allocator, 1);
    const system_directory = try platform.systemDirectoryAlloc(self.allocator);
    defer self.allocator.free(system_directory);
    var environment = try platform.shellEnvironment(self.allocator);
    defer environment.deinit();

    const is_wsl = kind == .wsl or (custom != null and std.mem.startsWith(u8, custom.?.program, "wsl:"));
    const default_distribution = if (is_wsl) wsl_registry.readDefaultWslDistributionAlloc(self.allocator) catch null else null;
    defer if (default_distribution) |name| self.allocator.free(name.bytes);
    var request: shell.ShellRequest = undefined;
    if (custom) |profile| {
        request = try profile.request();
        if (default_distribution) |name| request.wsl.distribution = .{ .named = name };
    } else if (kind == .wsl) {
        request = .{ .wsl = .{
            .distribution = if (default_distribution) |name| .{ .named = name } else .automatic,
            .command = .default,
        } };
    } else {
        const profile = kind.builtinProfile() orelse return &self.launch;
        request = .{ .builtin = profile };
    }
    const owned = try self.allocator.create(shell.ShellLaunch);
    errdefer self.allocator.destroy(owned);
    owned.* = try shell.ShellLaunch.resolve(self.allocator, request, system_directory, &environment);
    errdefer owned.deinit();
    if (custom) |profile| try owned.identifyProfile(profile.id, profile.name, profile.program, profile.arguments);
    self.shell_launches.appendAssumeCapacity(owned);
    return owned;
}
