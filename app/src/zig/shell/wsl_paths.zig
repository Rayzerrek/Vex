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

    /// Rejects paths that cannot roundtrip through WSL's Windows filesystem view.
    pub fn parse(bytes: []const u8) !LinuxDirectory {
        if (bytes.len == 0 or bytes.len > 32767 * 4 or bytes[0] != '/' or std.mem.indexOfScalar(u8, bytes, '\\') != null) return error.InvalidLinuxDirectory;
        try validateWslPathText(bytes);
        var scalars = std.unicode.Utf8View.initUnchecked(bytes).iterator();
        while (scalars.nextCodepoint()) |scalar| {
            // Literal U+F0xx names collide with the provider's reserved-character
            // escapes; treating them as Linux text would silently change the path.
            if (decodeNetworkCharacter(scalar) != scalar) return error.InvalidLinuxDirectory;
        }
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
    const path = try linuxPathFromNetworkSuffixAlloc(allocator, if (offset == bytes.len) "/" else bytes[offset..]);
    errdefer allocator.free(path);
    _ = try LinuxDirectory.parse(path);
    return path;
}

pub fn linuxPathFromNetworkSuffixAlloc(allocator: std.mem.Allocator, suffix: []const u8) ![]u8 {
    var path: std.ArrayList(u8) = .empty;
    errdefer path.deinit(allocator);
    var scalars = (try std.unicode.Utf8View.init(suffix)).iterator();
    while (scalars.nextCodepoint()) |scalar| {
        const decoded = decodeNetworkCharacter(scalar);
        if (scalar == '\\') {
            try path.append(allocator, '/');
        } else {
            var encoded: [4]u8 = undefined;
            const len = try std.unicode.utf8Encode(decoded, &encoded);
            try path.appendSlice(allocator, encoded[0..len]);
        }
    }
    try validateWslPathText(path.items);
    if (std.mem.indexOfScalar(u8, path.items, '\\') != null) return error.InvalidLinuxDirectory;
    return path.toOwnedSlice(allocator);
}

/// Maps a Linux OSC 7 directory into this pane's distribution, without filesystem probes or Windows normalization.
pub fn networkDirectoryFromLinuxAlloc(allocator: std.mem.Allocator, distribution: WslDistribution, path: LinuxDirectory) ![]u8 {
    _ = try LinuxDirectory.parse(path.bytes);
    var result: std.ArrayList(u8) = .empty;
    errdefer result.deinit(allocator);
    try result.appendSlice(allocator, "\\\\wsl.localhost\\");
    try result.appendSlice(allocator, distribution.bytes);
    for (path.bytes) |byte| {
        if (byte == '/') {
            try result.append(allocator, '\\');
        } else if (isNetworkEscapedByte(byte)) {
            // WSL's UNC provider exposes Windows-reserved ASCII as U+F0xx.
            var encoded: [4]u8 = undefined;
            const len = try std.unicode.utf8Encode(@as(u21, 0xf000) + byte, &encoded);
            try result.appendSlice(allocator, encoded[0..len]);
        } else {
            try result.append(allocator, byte);
        }
    }
    return result.toOwnedSlice(allocator);
}

fn isNetworkEscapedByte(byte: u8) bool {
    return byte < 0x20 or switch (byte) {
        '"', '*', ':', '<', '>', '?', '\\', '|' => true,
        else => false,
    };
}

fn decodeNetworkCharacter(scalar: u21) u21 {
    if (scalar >= 0xf000 and scalar <= 0xf0ff) {
        const byte: u8 = @intCast(scalar - 0xf000);
        if (isNetworkEscapedByte(byte)) return byte;
    }
    return scalar;
}

pub fn networkNameNeedsDecoding(bytes: []const u8) bool {
    var scalars = (std.unicode.Utf8View.init(bytes) catch return false).iterator();
    while (scalars.nextCodepoint()) |scalar| {
        if (scalar == '\\' or decodeNetworkCharacter(scalar) != scalar) return true;
    }
    return false;
}

/// Controls cannot be inserted into a terminal, and Linux backslashes are not
/// supported by the directory contract. Do not offer those lossy UNC names.
pub fn networkNameIsUsable(bytes: []const u8) bool {
    var scalars = (std.unicode.Utf8View.init(bytes) catch return false).iterator();
    while (scalars.nextCodepoint()) |scalar| {
        const decoded = decodeNetworkCharacter(scalar);
        if (decoded < 0x20 or (decoded >= 0x7f and decoded <= 0x9f) or (decoded == '\\' and scalar != '\\')) return false;
    }
    return true;
}

test "WSL network escaping roundtrips reserved Linux filename characters" {
    const allocator = std.testing.allocator;
    const distro = try WslDistribution.parse("NixOS");
    const linux = "/tmp/colon:name/question? star* quote\" pipe| less< greater>";
    const network = try networkDirectoryFromLinuxAlloc(allocator, distro, try LinuxDirectory.parse(linux));
    defer allocator.free(network);
    try std.testing.expect(std.mem.indexOf(u8, network, "\xef\x80\xba") != null);
    try std.testing.expect(networkNameIsUsable(network));
    const decoded = try linuxDirectoryFromNetworkAlloc(allocator, network, distro);
    defer allocator.free(decoded);
    try std.testing.expectEqualStrings(linux, decoded);
    for ([_][]const u8{ "bad\xef\x80\x8aname", "bad\xef\x81\x9cname" }) |unsafe| {
        try std.testing.expect(!networkNameIsUsable(unsafe));
    }
    try std.testing.checkAllAllocationFailures(allocator, checkEscapedWslPathAllocations, .{});
}

fn checkEscapedWslPathAllocations(allocator: std.mem.Allocator) !void {
    const distro = try WslDistribution.parse("NixOS");
    const network = try networkDirectoryFromLinuxAlloc(allocator, distro, try LinuxDirectory.parse("/tmp/colon:name/star*"));
    defer allocator.free(network);
    const decoded = try linuxDirectoryFromNetworkAlloc(allocator, network, distro);
    defer allocator.free(decoded);
    try std.testing.expectEqualStrings("/tmp/colon:name/star*", decoded);
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
    for ([_][]const u8{ "", "relative", "~/home", "/home\\filename", "/bad\x00path", "/bad\x1bpath", "/bad\xc2\x85path", "/bad\xffpath", "/literal\xef\x80\xbaescape" }) |bytes| {
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
