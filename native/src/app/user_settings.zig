const std = @import("std");
const themes = @import("../workspace/terminal_theme.zig");

/// A persisted shell preset; custom CLI launches remain startup-owned, never serialized as commands.
pub const DefaultShell = enum(u32) { startup = 0, cmd = 1, powershell = 2, pwsh = 3, nu = 4, gitbash = 5, wsl = 7 };

/// Validated terminal preferences. Loading never executes a command or changes an existing session.
pub const UserSettings = struct {
    theme_index: u8 = themes.default_dark_index,
    font_size: u8 = 14,
    font_family: [128]u8 = default_font,
    cursor_blink: bool = true,
    default_shell: DefaultShell = .startup,

    pub fn fontFamily(self: *const UserSettings) []const u8 {
        return std.mem.sliceTo(&self.font_family, 0);
    }

    pub fn validate(self: UserSettings) !void {
        const family = self.fontFamily();
        if (family.len == 0 or family.len >= self.font_family.len or !std.unicode.utf8ValidateSlice(family)) return error.InvalidUserSettings;
        for (family) |byte| if (byte < 32 or byte == 127) return error.InvalidUserSettings;
        if (self.theme_index >= themes.themes.len or self.font_size < 9 or self.font_size > 32) return error.InvalidUserSettings;
    }

    /// Parses a bounded, versioned settings document into validated runtime preferences.
    pub fn decode(allocator: std.mem.Allocator, bytes: []const u8) !UserSettings {
        if (bytes.len > 4096) return error.SettingsFileTooLarge;
        const parsed = try std.json.parseFromSlice(Document, allocator, bytes, .{});
        defer parsed.deinit();
        if (parsed.value.version != 1) return error.UnsupportedSettingsVersion;
        var index: ?u8 = null;
        for (themes.themes, 0..) |theme, i| {
            if (std.mem.eql(u8, theme.name, parsed.value.theme) or std.mem.eql(u8, legacy_theme_names[i], parsed.value.theme)) index = @intCast(i);
        }
        if (parsed.value.font_family.len == 0 or parsed.value.font_family.len >= 128 or std.mem.indexOfScalar(u8, parsed.value.font_family, 0) != null) return error.InvalidUserSettings;
        var family = [_]u8{0} ** 128;
        @memcpy(family[0..parsed.value.font_family.len], parsed.value.font_family);
        const result: UserSettings = .{
            .theme_index = index orelse return error.UnknownSettingsTheme,
            .font_size = parsed.value.font_size,
            .font_family = family,
            .cursor_blink = parsed.value.cursor_blink,
            .default_shell = parsed.value.default_shell,
        };
        try result.validate();
        return result;
    }

    /// Serializes stable theme names, not palette indices; the caller owns the returned UTF-8.
    pub fn encodeAlloc(self: UserSettings, allocator: std.mem.Allocator) ![]u8 {
        try self.validate();
        return std.json.Stringify.valueAlloc(allocator, Document{
            .theme = themes.themes[self.theme_index].name,
            .font_size = self.font_size,
            .font_family = self.fontFamily(),
            .cursor_blink = self.cursor_blink,
            .default_shell = self.default_shell,
        }, .{ .whitespace = .indent_2 });
    }
};

// Preserve existing native profiles while replacing the original third-party collection.
const legacy_theme_names = [_][]const u8{
    "Vex Obsidian",  "Tokyo Night Storm", "Kanagawa Wave",   "Catppuccin Mocha", "Rosé Pine",      "Gruvbox Material",
    "Vex Alabaster", "Flexoki Paper",     "Tokyo Night Day", "Catppuccin Latte", "Rosé Pine Dawn", "Kanagawa Lotus",
};

test "legacy theme names migrate to the matching appearance and encode the new collection" {
    for (legacy_theme_names, 0..) |name, i| {
        const bytes = try std.json.Stringify.valueAlloc(std.testing.allocator, Document{ .theme = name }, .{});
        defer std.testing.allocator.free(bytes);
        const settings = try UserSettings.decode(std.testing.allocator, bytes);
        try std.testing.expectEqual(i, settings.theme_index);
        const encoded = try settings.encodeAlloc(std.testing.allocator);
        defer std.testing.allocator.free(encoded);
        try std.testing.expect(std.mem.indexOf(u8, encoded, themes.themes[i].name) != null);
    }
}

pub const default_font: [128]u8 = "Cascadia Mono".* ++ [_]u8{0} ** (128 - "Cascadia Mono".len);

const Document = struct {
    version: u8 = 1,
    theme: []const u8,
    font_family: []const u8 = "Cascadia Mono",
    font_size: u8 = 14,
    cursor_blink: bool = true,
    default_shell: DefaultShell = .startup,
};

/// Loads missing settings as defaults; malformed or unreadable documents remain explicit errors.
pub fn loadUserSettings(allocator: std.mem.Allocator, path: []const u8) !UserSettings {
    const bytes = std.fs.cwd().readFileAlloc(allocator, path, 4096) catch |err| switch (err) {
        error.FileNotFound => return .{},
        else => return err,
    };
    defer allocator.free(bytes);
    return UserSettings.decode(allocator, bytes);
}

/// Atomically replaces preferences in an already selected user-owned directory.
pub fn saveUserSettings(allocator: std.mem.Allocator, path: []const u8, settings: UserSettings) !void {
    const parent = std.fs.path.dirname(path) orelse return error.InvalidSettingsPath;
    try std.fs.cwd().makePath(parent);
    var dir = try std.fs.cwd().openDir(parent, .{});
    defer dir.close();
    const bytes = try settings.encodeAlloc(allocator);
    defer allocator.free(bytes);
    var file = try dir.atomicFile(std.fs.path.basename(path), .{ .write_buffer = &.{} });
    defer file.deinit();
    try file.file_writer.file.writeAll(bytes);
    try file.finish();
}

/// LOCALAPPDATA is read only at startup; selftests may select an isolated file without touching real preferences.
pub fn userSettingsPathAlloc(allocator: std.mem.Allocator) ![]u8 {
    const selftest = std.process.getEnvVarOwned(allocator, "VEX_NATIVE_SELFTEST") catch |err| switch (err) {
        error.EnvironmentVariableNotFound => null,
        else => return err,
    };
    defer if (selftest) |value| allocator.free(value);
    if (selftest) |value| if (std.mem.eql(u8, value, "1")) {
        const override = std.process.getEnvVarOwned(allocator, "VEX_NATIVE_SETTINGS_PATH") catch |err| switch (err) {
            error.EnvironmentVariableNotFound => return error.MissingSelftestSettingsPath,
            else => return err,
        };
        if (!std.fs.path.isAbsolute(override)) {
            allocator.free(override);
            return error.InvalidSettingsPath;
        }
        return override;
    };
    const local = try std.process.getEnvVarOwned(allocator, "LOCALAPPDATA");
    defer allocator.free(local);
    if (!std.fs.path.isAbsolute(local)) return error.InvalidSettingsPath;
    return std.fs.path.join(allocator, &.{ local, "Vex", "settings.json" });
}

test "settings decode validates version, theme, size and shell; serialization round trips" {
    const input: UserSettings = .{ .theme_index = 2, .font_size = 19, .cursor_blink = false, .default_shell = .pwsh };
    const encoded = try input.encodeAlloc(std.testing.allocator);
    defer std.testing.allocator.free(encoded);
    try std.testing.expectEqualDeep(input, try UserSettings.decode(std.testing.allocator, encoded));
    try std.testing.expectError(error.InvalidUserSettings, UserSettings.decode(std.testing.allocator, "{\"theme\":\"Vex Obsidian\",\"font_size\":0}"));
    try std.testing.expectError(error.UnknownSettingsTheme, UserSettings.decode(std.testing.allocator, "{\"theme\":\"unknown\"}"));
    try std.testing.expectError(error.UnsupportedSettingsVersion, UserSettings.decode(std.testing.allocator, "{\"version\":2,\"theme\":\"Tokyo Night\"}"));
}

test "font preferences migrate, round trip and reject invalid families" {
    const defaults = try UserSettings.decode(std.testing.allocator, "{\"theme\":\"Vex Obsidian\"}");
    try std.testing.expectEqualStrings("Cascadia Mono", defaults.fontFamily());
    var settings = defaults;
    settings.font_family = [_]u8{0} ** 128;
    @memcpy(settings.font_family[0..8], "Consolas");
    const bytes = try settings.encodeAlloc(std.testing.allocator);
    defer std.testing.allocator.free(bytes);
    try std.testing.expectEqualDeep(settings, try UserSettings.decode(std.testing.allocator, bytes));
    settings.font_family = [_]u8{'x'} ** 128;
    try std.testing.expectError(error.InvalidUserSettings, settings.validate());
    try std.testing.expectError(error.InvalidUserSettings, UserSettings.decode(std.testing.allocator, "{\"theme\":\"Vex Obsidian\",\"font_family\":\"\"}"));
}

test "settings save is replaceable and malformed storage is not silently replaced" {
    var temp = std.testing.tmpDir(.{});
    defer temp.cleanup();
    const root = try temp.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(root);
    const path = try std.fs.path.join(std.testing.allocator, &.{ root, "preferences.json" });
    defer std.testing.allocator.free(path);
    try std.testing.expectEqualDeep(UserSettings{}, try loadUserSettings(std.testing.allocator, path));
    const next: UserSettings = .{ .font_size = 21, .cursor_blink = false };
    try saveUserSettings(std.testing.allocator, path, next);
    try std.testing.expectEqualDeep(next, try loadUserSettings(std.testing.allocator, path));
    try saveUserSettings(std.testing.allocator, path, .{});
    try std.testing.expectEqualDeep(UserSettings{}, try loadUserSettings(std.testing.allocator, path));
    try temp.dir.writeFile(.{ .sub_path = "preferences.json", .data = "{broken" });
    try std.testing.expectError(error.SyntaxError, loadUserSettings(std.testing.allocator, path));
    const unchanged = try temp.dir.readFileAlloc(std.testing.allocator, "preferences.json", 4096);
    defer std.testing.allocator.free(unchanged);
    try std.testing.expectEqualStrings("{broken", unchanged);
}

test "settings codec allocation failures release parsed and encoded owners" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkSettingsCodecAllocations, .{});
}
fn checkSettingsCodecAllocations(allocator: std.mem.Allocator) !void {
    const settings: UserSettings = .{ .theme_index = 3, .font_size = 20, .default_shell = .nu };
    const bytes = try settings.encodeAlloc(allocator);
    defer allocator.free(bytes);
    try std.testing.expectEqualDeep(settings, try UserSettings.decode(allocator, bytes));
}
