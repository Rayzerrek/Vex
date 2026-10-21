const std = @import("std");
const shell = @import("shell_launch.zig");
const wsl = @import("wsl_paths.zig");
const settings = @import("../app/user_settings.zig");
const arguments = @import("windows_arguments.zig");
const lua = @cImport({
    @cInclude("lua_settings.h");
});

// The native chooser has 64 rows: seven startup/builtin entries plus custom profiles and a settings default.
pub const max_profiles = 56;
pub const max_file_bytes = 64 * 1024;
pub const Profile = struct {
    id: u32,
    name: []const u8,
    program: []const u8,
    arguments: []const u8,

    pub fn validate(self: Profile) !void {
        if (self.id < 256 or self.id == std.math.maxInt(u32)) return error.InvalidShellProfileId;
        if (self.name.len == 0 or self.name.len >= 256 or std.mem.trim(u8, self.name, " \t").len != self.name.len) return error.InvalidShellName;
        for ([_][]const u8{ self.name, self.program, self.arguments }) |text| {
            try arguments.validateShellText(text);
            for (text) |byte| if (byte < 32 or byte == 127) return error.InvalidShellText;
        }
        if (std.mem.startsWith(u8, self.program, "wsl:")) {
            _ = try wsl.LinuxProgram.parse(self.program[4..]);
        } else try shell.validateCustomProgram(self.program);
    }
    fn deinit(self: Profile, allocator: std.mem.Allocator) void {
        allocator.free(self.name);
        allocator.free(self.program);
        allocator.free(self.arguments);
    }
    pub fn displayProgram(self: Profile) []const u8 {
        var end = @min(self.program.len, 255);
        if (end < self.program.len) while (end > 0 and self.program[end] & 0xc0 == 0x80) : (end -= 1) {};
        return self.program[0..end];
    }
    pub fn request(self: Profile) !shell.ShellRequest {
        if (std.mem.startsWith(u8, self.program, "wsl:")) return .{ .wsl = .{
            .distribution = .automatic,
            .command = .{ .program = .{ .path = try wsl.LinuxProgram.parse(self.program[4..]), .arguments = self.arguments } },
        } };
        return .{ .custom = .{ .program = self.program, .arguments = self.arguments } };
    }
};

/// Owns dynamic profile strings independently of sessions; launched sessions own their resolved metadata.
pub const Catalog = struct {
    allocator: std.mem.Allocator,
    profiles: std.ArrayList(Profile) = .empty,

    pub fn init(allocator: std.mem.Allocator) Catalog {
        return .{ .allocator = allocator };
    }
    pub fn deinit(self: *Catalog) void {
        for (self.profiles.items) |profile| profile.deinit(self.allocator);
        self.profiles.deinit(self.allocator);
        self.* = init(self.allocator);
    }
    pub fn find(self: *const Catalog, id: u32) ?Profile {
        for (self.profiles.items) |profile| if (profile.id == id) return profile;
        return null;
    }
    pub fn nextId(self: *const Catalog) !u32 {
        var next: u32 = 256;
        for (self.profiles.items) |profile| next = @max(next, try std.math.add(u32, profile.id, 1));
        if (next == std.math.maxInt(u32)) return error.ShellProfileIdExhausted;
        return next;
    }
    pub fn append(self: *Catalog, profile: Profile) !void {
        try profile.validate();
        if (self.profiles.items.len >= max_profiles) return error.TooManyShellProfiles;
        for (self.profiles.items) |existing| {
            if (existing.id == profile.id or std.ascii.eqlIgnoreCase(existing.name, profile.name)) return error.DuplicateShellProfile;
        }
        const name = try self.allocator.dupe(u8, profile.name);
        errdefer self.allocator.free(name);
        const program = try self.allocator.dupe(u8, profile.program);
        errdefer self.allocator.free(program);
        const args = try self.allocator.dupe(u8, profile.arguments);
        errdefer self.allocator.free(args);
        try self.profiles.append(self.allocator, .{ .id = profile.id, .name = name, .program = program, .arguments = args });
    }
    /// Separate lock file survives atomic replacement; OS locks are released even after a crash.
    pub fn editLock(allocator: std.mem.Allocator, config_path: []const u8) !std.fs.File {
        const profile_path = try settings.companionPathAlloc(allocator, config_path, "shells");
        defer allocator.free(profile_path);
        const path = try std.fmt.allocPrint(allocator, "{s}.lock", .{profile_path});
        defer allocator.free(path);
        try std.fs.cwd().makePath(std.fs.path.dirname(path) orelse return error.InvalidSettingsPath);
        return std.fs.cwd().createFile(path, .{ .truncate = false, .read = true, .lock = .exclusive, .lock_nonblocking = true }) catch |err| switch (err) {
            error.WouldBlock => error.ProfileStoreBusy,
            else => err,
        };
    }
    pub fn appendAndSave(self: *Catalog, config_path: []const u8, profile: Profile) !void {
        try self.append(profile);
        errdefer self.profiles.pop().?.deinit(self.allocator);
        try self.save(config_path);
    }
    pub fn encodeAlloc(self: *const Catalog) ![]u8 {
        var bytes: std.ArrayList(u8) = .empty;
        errdefer bytes.deinit(self.allocator);
        try bytes.appendSlice(self.allocator, "-- Custom shell profiles managed by Vex Settings. IDs remain stable.\n");
        for (self.profiles.items) |profile| {
            const name = try settings.quoteLuaString(self.allocator, profile.name);
            defer self.allocator.free(name);
            const program = try settings.quoteLuaString(self.allocator, profile.program);
            defer self.allocator.free(program);
            const args = try settings.quoteLuaString(self.allocator, profile.arguments);
            defer self.allocator.free(args);
            const line = try std.fmt.allocPrint(self.allocator, "vex.add_shell({d}, {s}, {s}, {s})\n", .{ profile.id, name, program, args });
            defer self.allocator.free(line);
            if (line.len > max_file_bytes - bytes.items.len) return error.SettingsFileTooLarge;
            try bytes.appendSlice(self.allocator, line);
        }
        return bytes.toOwnedSlice(self.allocator);
    }
    pub fn save(self: *const Catalog, config_path: []const u8) !void {
        const path = try settings.companionPathAlloc(self.allocator, config_path, "shells");
        defer self.allocator.free(path);
        const parent = std.fs.path.dirname(path) orelse return error.InvalidSettingsPath;
        const bytes = try self.encodeAlloc();
        defer self.allocator.free(bytes);
        try std.fs.cwd().makePath(parent);
        var dir = try std.fs.cwd().openDir(parent, .{});
        defer dir.close();
        var file = try dir.atomicFile(std.fs.path.basename(path), .{ .write_buffer = &.{} });
        defer file.deinit();
        try file.file_writer.file.writeAll(bytes);
        try file.finish();
    }
    pub fn decode(allocator: std.mem.Allocator, source: []const u8, path: []const u8, diagnostic: *settings.SettingsDiagnostic) !Catalog {
        if (source.len > max_file_bytes) return error.SettingsFileTooLarge;
        var catalog = init(allocator);
        errdefer catalog.deinit();
        var context: DecodeContext = .{ .catalog = &catalog };
        const zpath = try allocator.dupeZ(u8, path);
        defer allocator.free(zpath);
        diagnostic.* = .{};
        const bytes = if (std.mem.startsWith(u8, source, "\xef\xbb\xbf")) source[3..] else source;
        const result = lua.vex_lua_shells_decode(bytes.ptr, bytes.len, zpath.ptr, &context, DecodeContext.add, &diagnostic.bytes, diagnostic.bytes.len);
        if (context.failure) |err| return err;
        if (result != 0) return if (result == 2) error.LuaMemoryLimit else error.InvalidLuaShellProfiles;
        return catalog;
    }
    pub fn load(allocator: std.mem.Allocator, config_path: []const u8, diagnostic: *settings.SettingsDiagnostic) !Catalog {
        const path = try settings.companionPathAlloc(allocator, config_path, "shells");
        defer allocator.free(path);
        const bytes = std.fs.cwd().readFileAlloc(allocator, path, max_file_bytes) catch |err| switch (err) {
            error.FileNotFound => return init(allocator),
            else => return err,
        };
        defer allocator.free(bytes);
        return decode(allocator, bytes, path, diagnostic);
    }
};
const DecodeContext = struct {
    catalog: *Catalog,
    failure: ?anyerror = null,
    fn add(context_pointer: ?*anyopaque, id: c_uint, name: [*c]const u8, name_len: usize, program: [*c]const u8, program_len: usize, args: [*c]const u8, args_len: usize) callconv(.c) c_int {
        const self: *DecodeContext = @ptrCast(@alignCast(context_pointer.?));
        self.catalog.append(.{ .id = id, .name = name[0..name_len], .program = program[0..program_len], .arguments = args[0..args_len] }) catch |err| {
            self.failure = err;
            return 1;
        };
        return 0;
    }
};

test "custom shell catalogs preserve arbitrary names, quoting and stable IDs through Lua" {
    var catalog = Catalog.init(std.testing.allocator);
    defer catalog.deinit();
    try catalog.append(.{ .id = 256, .name = "Future shell", .program = "future-shell.exe", .arguments = "--interactive \"hello world\"" });
    try catalog.append(.{ .id = 900, .name = "Fish (WSL)", .program = "wsl:/usr/bin/fish", .arguments = "-l" });
    const source = try catalog.encodeAlloc();
    defer std.testing.allocator.free(source);
    var diagnostic: settings.SettingsDiagnostic = .{};
    var decoded = try Catalog.decode(std.testing.allocator, source, "shells.lua", &diagnostic);
    defer decoded.deinit();
    try std.testing.expectEqualStrings(catalog.find(256).?.arguments, decoded.find(256).?.arguments);
    try std.testing.expectEqualStrings("wsl:/usr/bin/fish", decoded.find(900).?.program);
    try std.testing.expectEqual(@as(u32, 901), try decoded.nextId());
    try std.testing.expectError(error.DuplicateShellProfile, decoded.append(catalog.find(256).?));
}

test "invalid shell definitions and failed saves preserve all existing profiles" {
    var catalog = Catalog.init(std.testing.allocator);
    defer catalog.deinit();
    const profile: Profile = .{ .id = 257, .name = "Another", .program = "future.exe", .arguments = "" };
    try catalog.append(.{ .id = 256, .name = "Future", .program = "future.exe", .arguments = "" });
    try std.testing.expectError(error.InvalidShellProgram, catalog.append(.{ .id = 256, .name = "Future", .program = "relative/path.exe", .arguments = "" }));
    var temp = std.testing.tmpDir(.{});
    defer temp.cleanup();
    const root = try temp.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(root);
    try temp.dir.writeFile(.{ .sub_path = "blocked", .data = "keep" });
    const path = try std.fs.path.join(std.testing.allocator, &.{ root, "blocked", "init.lua" });
    defer std.testing.allocator.free(path);
    if (catalog.appendAndSave(path, profile)) |_| return error.ExpectedSaveFailure else |_| {}
    try std.testing.expectEqual(@as(usize, 1), catalog.profiles.items.len);
    try std.testing.expectEqualStrings("Future", catalog.find(256).?.name);
}

fn checkCatalogAllocations(allocator: std.mem.Allocator) !void {
    var catalog = Catalog.init(allocator);
    defer catalog.deinit();
    try catalog.append(.{ .id = 256, .name = "Future 音", .program = "future-shell.exe", .arguments = "--login" });
    const source = try catalog.encodeAlloc();
    defer allocator.free(source);
    var diagnostic: settings.SettingsDiagnostic = .{};
    var decoded = try Catalog.decode(allocator, source, "shells.lua", &diagnostic);
    defer decoded.deinit();
    try std.testing.expectEqualStrings("Future 音", decoded.find(256).?.name);
}

test "all custom profile codec allocation failures release partial owners and Lua callbacks" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkCatalogAllocations, .{});
}

test "profile display bounds, capacity and writer locking fail without corrupting the catalog" {
    var catalog = Catalog.init(std.testing.allocator);
    defer catalog.deinit();
    const long_program = "x" ** 254 ++ "音.exe";
    const profile: Profile = .{ .id = 256, .name = "Future", .program = long_program, .arguments = "" };
    try std.testing.expectEqual(@as(usize, 254), profile.displayProgram().len);
    try std.testing.expect(std.unicode.utf8ValidateSlice(profile.displayProgram()));
    for (0..max_profiles) |i| {
        var name: [32]u8 = undefined;
        try catalog.append(.{ .id = @intCast(256 + i), .name = try std.fmt.bufPrint(&name, "Future {d}", .{i}), .program = "future.exe", .arguments = "" });
    }
    try std.testing.expectError(error.TooManyShellProfiles, catalog.append(.{ .id = 1000, .name = "Overflow", .program = "future.exe", .arguments = "" }));
    try std.testing.expectEqual(@as(usize, max_profiles), catalog.profiles.items.len);
    var temp = std.testing.tmpDir(.{});
    defer temp.cleanup();
    const root = try temp.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(root);
    const config = try std.fs.path.join(std.testing.allocator, &.{ root, "init.lua" });
    defer std.testing.allocator.free(config);
    {
        const lock = try Catalog.editLock(std.testing.allocator, config);
        defer lock.close();
        try std.testing.expectError(error.ProfileStoreBusy, Catalog.editLock(std.testing.allocator, config));
    }
    const lock = try Catalog.editLock(std.testing.allocator, config);
    lock.close();
}

test "profile storage is isolated from user Lua and reload keeps stable identities" {
    var temp = std.testing.tmpDir(.{});
    defer temp.cleanup();
    const allocator = std.testing.allocator;
    const root = try temp.dir.realpathAlloc(allocator, ".");
    defer allocator.free(root);
    const path = try std.fs.path.join(allocator, &.{ root, "my.lua" });
    defer allocator.free(path);
    try temp.dir.writeFile(.{ .sub_path = "my.lua", .data = "-- keep my settings" });
    var catalog = Catalog.init(allocator);
    defer catalog.deinit();
    try catalog.appendAndSave(path, .{ .id = 256, .name = "Future", .program = "future.exe", .arguments = "" });
    var diagnostic: settings.SettingsDiagnostic = .{};
    var loaded = try Catalog.load(allocator, path, &diagnostic);
    defer loaded.deinit();
    try std.testing.expectEqualStrings("Future", loaded.find(256).?.name);
    const unchanged = try temp.dir.readFileAlloc(allocator, "my.lua", 1024);
    defer allocator.free(unchanged);
    try std.testing.expectEqualStrings("-- keep my settings", unchanged);
    const prefs: settings.UserSettings = .{ .default_shell = @enumFromInt(256) };
    const encoded = try prefs.encodeAlloc(allocator);
    defer allocator.free(encoded);
    try std.testing.expectEqual(prefs.default_shell, (try settings.UserSettings.decode(allocator, encoded)).default_shell);
    for ([_][]const u8{ "vex.add_shell(1, 'name', 'shell.exe', '')", "vex.add_shell(256, 'name', '../bad.exe', '')", "vex.add_shell(256, 'name', 'shell.exe', '\\0')", "while true do end" }) |bad| {
        if (Catalog.decode(allocator, bad, "shells.lua", &diagnostic)) |value| {
            var unexpected = value;
            unexpected.deinit();
            return error.ExpectedProfileFailure;
        } else |_| {}
    }
}
