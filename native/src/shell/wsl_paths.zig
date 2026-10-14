const std = @import("std");

/// Validated WSL distribution name, never a share/path fragment or an option.
pub const WslDistribution = struct {
    bytes: []const u8,

    /// Borrows UTF-8 storage; callers clone it before retaining parsed argv.
    pub fn parse(bytes: []const u8) !WslDistribution {
        if (bytes.len == 0 or bytes.len > 256 or bytes[0] == '-' or std.mem.indexOfAny(u8, bytes, "\\/:\"") != null) return error.InvalidWslDistribution;
        try validateWslPathText(bytes);
        if (std.mem.eql(u8, bytes, ".") or std.mem.eql(u8, bytes, "..")) return error.InvalidWslDistribution;
        return .{ .bytes = bytes };
    }
};

/// Absolute Linux directory with lossless representation in the WSL filesystem view.
pub const LinuxDirectory = struct {
    bytes: []const u8,

    /// Rejects controls, relative paths and backslashes rather than changing a Linux filename.
    pub fn parse(bytes: []const u8) !LinuxDirectory {
        if (bytes.len == 0 or bytes.len > 32767 * 4 or bytes[0] != '/' or std.mem.indexOfScalar(u8, bytes, '\\') != null) return error.InvalidLinuxDirectory;
        try validateWslPathText(bytes);
        return .{ .bytes = bytes };
    }
};

/// Parsed absolute Linux program path; distinct from a directory so the root cannot become an executable.
pub const LinuxProgram = struct {
    bytes: []const u8,

    /// Borrows UTF-8 storage; launch construction encodes it into owned command storage.
    pub fn parse(bytes: []const u8) !LinuxProgram {
        _ = try LinuxDirectory.parse(bytes);
        if (std.mem.eql(u8, bytes, "/")) return error.InvalidWslProgram;
        return .{ .bytes = bytes };
    }
};

fn validateWslPathText(bytes: []const u8) !void {
    if (!std.unicode.utf8ValidateSlice(bytes)) return error.InvalidWslPathText;
    var iterator = std.unicode.Utf8View.initUnchecked(bytes).iterator();
    while (iterator.nextCodepoint()) |scalar| if (scalar < 32 or (scalar >= 127 and scalar <= 159)) return error.InvalidWslPathText;
}

fn wslNetworkPrefix(bytes: []const u8) ?usize {
    for ([_][]const u8{ "\\\\wsl.localhost\\", "\\\\wsl$\\" }) |prefix| {
        if (std.ascii.startsWithIgnoreCase(bytes, prefix)) return prefix.len;
    }
    return null;
}

/// Returns the borrowed distribution for a WSL UNC directory, or null for another Windows path.
pub fn networkDistribution(bytes: []const u8) !?WslDistribution {
    const offset = wslNetworkPrefix(bytes) orelse return null;
    const end = std.mem.indexOfScalarPos(u8, bytes, offset, '\\') orelse bytes.len;
    return try WslDistribution.parse(bytes[offset..end]);
}

/// Converts only a matching WSL UNC path; drive paths are passed unchanged to wsl.exe --cd.
pub fn linuxDirectoryFromNetworkAlloc(allocator: std.mem.Allocator, bytes: []const u8, expected: WslDistribution) ![]u8 {
    const distribution = (try networkDistribution(bytes)) orelse return error.NotWslNetworkDirectory;
    if (!std.ascii.eqlIgnoreCase(distribution.bytes, expected.bytes)) return error.WslDistributionMismatch;
    const offset = (wslNetworkPrefix(bytes) orelse return error.NotWslNetworkDirectory) + distribution.bytes.len;
    const path = try allocator.dupe(u8, if (offset == bytes.len) "/" else bytes[offset..]);
    errdefer allocator.free(path);
    std.mem.replaceScalar(u8, path, '\\', '/');
    _ = try LinuxDirectory.parse(path);
    return path;
}

/// Maps a Linux OSC 7 directory into this pane's distribution, without filesystem probes or Windows normalization.
pub fn networkDirectoryFromLinuxAlloc(allocator: std.mem.Allocator, distribution: WslDistribution, path: LinuxDirectory) ![]u8 {
    const result = try std.fmt.allocPrint(allocator, "\\\\wsl.localhost\\{s}{s}", .{ distribution.bytes, path.bytes });
    std.mem.replaceScalar(u8, result, '/', '\\');
    return result;
}

test "WSL network directories preserve distribution, Unicode and POSIX path spelling" {
    const allocator = std.testing.allocator;
    const distribution = try WslDistribution.parse("Ubuntu");
    for ([_]struct { network: []const u8, linux: []const u8 }{
        .{ .network = "\\\\wsl.localhost\\Ubuntu", .linux = "/" },
        .{ .network = "\\\\WSL$\\Ubuntu\\home\\Żółć O'Brien", .linux = "/home/Żółć O'Brien" },
        .{ .network = "\\\\wsl.localhost\\Ubuntu\\home\\link\\..\\user", .linux = "/home/link/../user" },
    }) |fixture| {
        const linux = try linuxDirectoryFromNetworkAlloc(allocator, fixture.network, distribution);
        defer allocator.free(linux);
        try std.testing.expectEqualStrings(fixture.linux, linux);
        const network = try networkDirectoryFromLinuxAlloc(allocator, distribution, try LinuxDirectory.parse(linux));
        defer allocator.free(network);
        const roundtrip = try linuxDirectoryFromNetworkAlloc(allocator, network, distribution);
        defer allocator.free(roundtrip);
        try std.testing.expectEqualStrings(linux, roundtrip);
    }
    try std.testing.expect((try networkDistribution("C:\\project")) == null);
    try std.testing.expectError(error.WslDistributionMismatch, linuxDirectoryFromNetworkAlloc(allocator, "\\\\wsl$\\Debian\\home", distribution));
}

test "WSL parsers reject corrupt distribution names and lossy or unsafe Linux directories" {
    for ([_][]const u8{ "", "-Ubuntu", ".", "..", "Ubuntu:bad", "Ubuntu\\home", "Ubuntu/home", "Ubuntu\"", "Ubuntu\n", "bad\xff" }) |bytes| {
        if (WslDistribution.parse(bytes)) |_| return error.ExpectedInvalidDistribution else |_| {}
    }
    try std.testing.expectError(error.InvalidWslProgram, LinuxProgram.parse("/"));
    for ([_][]const u8{ "", "relative", "~/home", "/home\\filename", "/bad\x00path", "/bad\x1bpath", "/bad\xc2\x85path", "/bad\xffpath" }) |bytes| {
        if (LinuxDirectory.parse(bytes)) |_| return error.ExpectedInvalidLinuxDirectory else |_| {}
    }
}

fn checkWslPathAllocations(allocator: std.mem.Allocator) !void {
    const distribution = try WslDistribution.parse("Ubuntu");
    const network = try networkDirectoryFromLinuxAlloc(allocator, distribution, try LinuxDirectory.parse("/home/Żółć files"));
    defer allocator.free(network);
    const linux = try linuxDirectoryFromNetworkAlloc(allocator, network, distribution);
    defer allocator.free(linux);
}

test "WSL path allocation failures release every intermediate buffer" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkWslPathAllocations, .{});
}
