const std = @import("std");
const windows = std.os.windows;
const registry = windows.advapi32;
const wsl = @import("../shell/wsl_paths.zig");

const lxss_path = std.unicode.utf8ToUtf16LeStringLiteral("Software\\Microsoft\\Windows\\CurrentVersion\\Lxss");
const default_value = std.unicode.utf8ToUtf16LeStringLiteral("DefaultDistribution");
const name_value = std.unicode.utf8ToUtf16LeStringLiteral("DistributionName");
const reg_sz = 1;
const registry_string_capacity = 1024;

/// Reads the current user's default WSL distribution without starting WSL; caller frees the parsed name's bytes.
pub fn readDefaultWslDistributionAlloc(allocator: std.mem.Allocator) !wsl.WslDistribution {
    const root = try RegistryKey.open(windows.HKEY_CURRENT_USER, lxss_path);
    defer root.deinit();
    return readDefaultDistributionAtKeyAlloc(allocator, root.handle);
}

const RegistryKey = struct {
    handle: windows.HKEY,

    fn open(parent: windows.HKEY, path: [:0]const u16) !RegistryKey {
        var handle: windows.HKEY = undefined;
        try checkRegistryResult(registry.RegOpenKeyExW(parent, path.ptr, 0, windows.KEY_QUERY_VALUE, &handle));
        return .{ .handle = handle };
    }
    fn deinit(self: RegistryKey) void {
        _ = registry.RegCloseKey(self.handle);
    }
};

fn checkRegistryResult(result: windows.LSTATUS) !void {
    return switch (result) {
        @intFromEnum(windows.Win32Error.SUCCESS) => {},
        @intFromEnum(windows.Win32Error.FILE_NOT_FOUND), @intFromEnum(windows.Win32Error.PATH_NOT_FOUND) => error.WslDefaultDistributionUnavailable,
        @intFromEnum(windows.Win32Error.ACCESS_DENIED) => error.WslRegistryAccessDenied,
        @intFromEnum(windows.Win32Error.MORE_DATA) => error.InvalidWslRegistryString,
        else => error.WslRegistryReadFailed,
    };
}

fn queryRegistryString(key: windows.HKEY, value: [:0]const u16, buffer: *[registry_string_capacity]u8) ![]const u8 {
    var length: windows.DWORD = buffer.len;
    var value_type: windows.DWORD = 0;
    try checkRegistryResult(registry.RegQueryValueExW(key, value.ptr, null, &value_type, &buffer[0], &length));
    // REG_SZ is not guaranteed terminated or even UTF-16; validate the raw byte contract before decoding.
    if (value_type != reg_sz or length < 2 or length > buffer.len or length % 2 != 0) return error.InvalidWslRegistryString;
    if (buffer[length - 2] != 0 or buffer[length - 1] != 0) return error.InvalidWslRegistryString;
    const bytes = buffer[0 .. length - 2];
    var offset: usize = 0;
    while (offset < bytes.len) : (offset += 2) if (std.mem.readInt(u16, bytes[offset..][0..2], .little) == 0) return error.InvalidWslRegistryString;
    return bytes;
}

fn decodeRegistryGuid(bytes: []const u8) ![38:0]u16 {
    if (bytes.len != 38 * 2) return error.InvalidWslRegistryGuid;
    var guid: [38:0]u16 = undefined;
    for (0..38) |index| {
        const scalar = std.mem.readInt(u16, bytes[index * 2 ..][0..2], .little);
        const valid = switch (index) {
            0 => scalar == '{',
            37 => scalar == '}',
            9, 14, 19, 24 => scalar == '-',
            else => scalar <= 127 and std.ascii.isHex(@intCast(scalar)),
        };
        if (!valid) return error.InvalidWslRegistryGuid;
        guid[index] = scalar;
    }
    guid[38] = 0;
    return guid;
}

fn readDefaultDistributionAtKeyAlloc(allocator: std.mem.Allocator, root: windows.HKEY) !wsl.WslDistribution {
    var buffer: [registry_string_capacity]u8 = undefined;
    const guid = try decodeRegistryGuid(try queryRegistryString(root, default_value, &buffer));
    const distribution_key = try RegistryKey.open(root, &guid);
    defer distribution_key.deinit();
    const bytes = try queryRegistryString(distribution_key.handle, name_value, &buffer);
    var units: [registry_string_capacity / 2]u16 = undefined;
    for (0..bytes.len / 2) |index| units[index] = std.mem.readInt(u16, bytes[index * 2 ..][0..2], .little);
    const name = std.unicode.utf16LeToUtf8Alloc(allocator, units[0 .. bytes.len / 2]) catch |err| switch (err) {
        error.DanglingSurrogateHalf, error.ExpectedSecondSurrogateHalf, error.UnexpectedSecondSurrogateHalf => return error.InvalidWslRegistryString,
        error.OutOfMemory => return err,
    };
    errdefer allocator.free(name);
    return wsl.WslDistribution.parse(name) catch return error.InvalidWslRegistryDistribution;
}

const TestRegistry = struct {
    allocator: std.mem.Allocator,
    handle: windows.HKEY,
    path: [:0]u16,

    const guid = std.unicode.utf8ToUtf16LeStringLiteral("{12345678-1234-5678-9abc-123456789abc}");

    extern "advapi32" fn RegCreateKeyExW(windows.HKEY, windows.LPCWSTR, windows.DWORD, ?windows.LPWSTR, windows.DWORD, windows.REGSAM, ?*const windows.SECURITY_ATTRIBUTES, *windows.HKEY, ?*windows.DWORD) callconv(.winapi) windows.LSTATUS;
    extern "advapi32" fn RegSetValueExW(windows.HKEY, windows.LPCWSTR, windows.DWORD, windows.DWORD, [*]const u8, windows.DWORD) callconv(.winapi) windows.LSTATUS;
    extern "advapi32" fn RegDeleteTreeW(windows.HKEY, ?windows.LPCWSTR) callconv(.winapi) windows.LSTATUS;
    extern "advapi32" fn RegDeleteKeyW(windows.HKEY, windows.LPCWSTR) callconv(.winapi) windows.LSTATUS;
    extern "kernel32" fn GetProcessHandleCount(windows.HANDLE, *windows.DWORD) callconv(.winapi) windows.BOOL;

    fn handleCount() !windows.DWORD {
        var count: windows.DWORD = 0;
        if (GetProcessHandleCount(windows.GetCurrentProcess(), &count) == 0) return error.TestHandleCountFailed;
        return count;
    }
    fn init(allocator: std.mem.Allocator) !TestRegistry {
        var random: [16]u8 = undefined;
        std.crypto.random.bytes(&random);
        const text = try std.fmt.allocPrint(allocator, "Software\\Vex.Native.Tests.{s}", .{std.fmt.bytesToHex(&random, .lower)});
        defer allocator.free(text);
        const path = try std.unicode.utf8ToUtf16LeAllocZ(allocator, text);
        errdefer allocator.free(path);
        const handle = try create(windows.HKEY_CURRENT_USER, path);
        return .{ .allocator = allocator, .handle = handle, .path = path };
    }
    fn create(parent: windows.HKEY, path: [:0]const u16) !windows.HKEY {
        var handle: windows.HKEY = undefined;
        var disposition: windows.DWORD = 0;
        // Tests create only unique volatile keys, never the user's WSL configuration.
        try checkRegistryResult(RegCreateKeyExW(parent, path.ptr, 0, null, 1, windows.KEY_ALL_ACCESS, null, &handle, &disposition));
        if (disposition != 1) {
            _ = registry.RegCloseKey(handle);
            return error.TestRegistryAlreadyExists;
        }
        return handle;
    }
    fn deinit(self: *TestRegistry) void {
        const removed_tree = RegDeleteTreeW(self.handle, null);
        _ = registry.RegCloseKey(self.handle);
        const removed_key = RegDeleteKeyW(windows.HKEY_CURRENT_USER, self.path.ptr);
        self.allocator.free(self.path);
        std.debug.assert(removed_tree == 0 and removed_key == 0);
    }
    fn setString(self: *TestRegistry, key: windows.HKEY, value: [:0]const u16, text: []const u8) !void {
        const units = try std.unicode.utf8ToUtf16LeAllocZ(self.allocator, text);
        defer self.allocator.free(units);
        try setBytes(key, value, reg_sz, std.mem.sliceAsBytes(units[0 .. units.len + 1]));
    }
    fn setBytes(key: windows.HKEY, value: [:0]const u16, value_type: windows.DWORD, bytes: []const u8) !void {
        try checkRegistryResult(RegSetValueExW(key, value.ptr, 0, value_type, bytes.ptr, @intCast(bytes.len)));
    }
};

fn checkRegistryAllocations(allocator: std.mem.Allocator, key: windows.HKEY) !void {
    const distribution = try readDefaultDistributionAtKeyAlloc(allocator, key);
    defer allocator.free(distribution.bytes);
    try std.testing.expectEqualStrings("Żółć Ubuntu Test", distribution.bytes);
}

test "WSL default registry read decodes Unicode and releases allocations/native handles on failure" {
    var fixture = try TestRegistry.init(std.testing.allocator);
    defer fixture.deinit();
    try fixture.setString(fixture.handle, default_value, "{12345678-1234-5678-9abc-123456789abc}");
    const child = try TestRegistry.create(fixture.handle, TestRegistry.guid);
    defer _ = registry.RegCloseKey(child);
    try fixture.setString(child, name_value, "Żółć Ubuntu Test");
    try checkRegistryAllocations(std.testing.allocator, fixture.handle);
    const handles_before = try TestRegistry.handleCount();
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkRegistryAllocations, .{fixture.handle});
    try std.testing.expectEqual(handles_before, try TestRegistry.handleCount());
}

test "WSL default registry read rejects missing defaults, invalid GUIDs and missing distribution keys" {
    var fixture = try TestRegistry.init(std.testing.allocator);
    defer fixture.deinit();
    try std.testing.expectError(error.WslDefaultDistributionUnavailable, readDefaultDistributionAtKeyAlloc(std.testing.allocator, fixture.handle));
    for ([_][]const u8{ "", "..\\another-key", "{12345678-1234-5678-9abc-123456789abz}", "12345678-1234-5678-9abc-123456789abc" }) |guid| {
        try fixture.setString(fixture.handle, default_value, guid);
        try std.testing.expectError(error.InvalidWslRegistryGuid, readDefaultDistributionAtKeyAlloc(std.testing.allocator, fixture.handle));
    }
    try fixture.setString(fixture.handle, default_value, "{12345678-1234-5678-9abc-123456789abc}");
    try std.testing.expectError(error.WslDefaultDistributionUnavailable, readDefaultDistributionAtKeyAlloc(std.testing.allocator, fixture.handle));
}

test "WSL registry strings reject wrong types, odd lengths, missing/interior terminators and invalid UTF-16" {
    var fixture = try TestRegistry.init(std.testing.allocator);
    defer fixture.deinit();
    try fixture.setString(fixture.handle, default_value, "{12345678-1234-5678-9abc-123456789abc}");
    const child = try TestRegistry.create(fixture.handle, TestRegistry.guid);
    defer _ = registry.RegCloseKey(child);
    for ([_][]const u8{ &.{}, &.{1}, &.{ 65, 0 }, &.{ 65, 0, 0 }, &.{ 65, 0, 0, 0, 66, 0, 0, 0 }, &.{ 0, 0xd8, 0, 0 }, &.{ 0, 0xdc, 0, 0 }, &.{ 0, 0xd8, 65, 0, 0, 0 } }) |bytes| {
        try TestRegistry.setBytes(child, name_value, reg_sz, bytes);
        try std.testing.expectError(error.InvalidWslRegistryString, readDefaultDistributionAtKeyAlloc(std.testing.allocator, fixture.handle));
    }
    try TestRegistry.setBytes(child, name_value, 4, &.{ 65, 0, 0, 0 });
    try std.testing.expectError(error.InvalidWslRegistryString, readDefaultDistributionAtKeyAlloc(std.testing.allocator, fixture.handle));
    for ([_][]const u8{ "", "Ubuntu\\home", "Ubuntu\n", "-Ubuntu" }) |name| {
        try fixture.setString(child, name_value, name);
        try std.testing.expectError(error.InvalidWslRegistryDistribution, readDefaultDistributionAtKeyAlloc(std.testing.allocator, fixture.handle));
    }
    const oversized = [_]u8{'x'} ** registry_string_capacity;
    try fixture.setString(child, name_value, &oversized);
    try std.testing.expectError(error.InvalidWslRegistryString, readDefaultDistributionAtKeyAlloc(std.testing.allocator, fixture.handle));
}
