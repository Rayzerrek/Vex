const std = @import("std");

/// A fully qualified UTF-8 Windows directory; parsing does not probe the filesystem.
pub const DirectoryPath = struct {
    bytes: []const u8,
    pub const max_bytes = 32767 * 4;

    /// Reject relative/control paths before passing a directory to CreateProcessW.
    pub fn parse(bytes: []const u8) !DirectoryPath {
        if (bytes.len < 3 or bytes.len > max_bytes or !std.unicode.utf8ValidateSlice(bytes)) return error.InvalidDirectoryPath;
        var codepoints = std.unicode.Utf8View.initUnchecked(bytes).iterator();
        while (codepoints.nextCodepoint()) |codepoint| if (codepoint < 32 or (codepoint >= 127 and codepoint <= 159)) return error.InvalidDirectoryPath;
        const drive = std.ascii.isAlphabetic(bytes[0]) and bytes[1] == ':' and (bytes[2] == '\\' or bytes[2] == '/');
        const unc = std.mem.startsWith(u8, bytes, "\\\\") and bytes.len > 4 and std.mem.indexOfScalarPos(u8, bytes, 2, '\\') != null;
        if (!drive and !unc) return error.InvalidDirectoryPath;
        if (std.ascii.startsWithIgnoreCase(bytes, "\\\\wsl.localhost\\") or std.ascii.startsWithIgnoreCase(bytes, "\\\\wsl$\\")) return error.WslDirectoryUnsupported;
        return .{ .bytes = bytes };
    }
};

/// Owns a pane's launch directory and last valid local OSC 7 report. Never copy a live owner.
pub const PaneDirectory = struct {
    allocator: std.mem.Allocator,
    launch: []u8,
    reported: ?[]u8 = null,
    last_report: std.ArrayList(u8) = .empty,

    /// Clones a parsed launch directory so sibling panes never borrow each other's storage.
    pub fn init(allocator: std.mem.Allocator, path: DirectoryPath) !PaneDirectory {
        return .{ .allocator = allocator, .launch = try allocator.dupe(u8, path.bytes) };
    }
    /// Releases both path owners and the raw-report cache.
    pub fn deinit(self: *PaneDirectory) void {
        self.allocator.free(self.launch);
        if (self.reported) |path| self.allocator.free(path);
        self.last_report.deinit(self.allocator);
    }
    /// Returns the last valid report, otherwise the original launch directory.
    pub fn current(self: *const PaneDirectory) DirectoryPath {
        return .{ .bytes = self.reported orelse self.launch };
    }
    /// Invalid/remote/empty reports preserve the last local path, matching the WPF feed contract.
    pub fn applyReport(self: *PaneDirectory, bytes: []const u8, machine_name: []const u8) !void {
        if (bytes.len > DirectoryPath.max_bytes or std.mem.eql(u8, bytes, self.last_report.items)) return;
        const path = decodeLocalDirectory(self.allocator, bytes, machine_name) catch |err| switch (err) {
            error.InvalidDirectoryReport => null,
            else => return err,
        };
        errdefer if (path) |owned| self.allocator.free(owned);
        // Reserve before replacing either owner; OOM must preserve the previous path/cache.
        try self.last_report.ensureTotalCapacity(self.allocator, bytes.len);
        self.last_report.clearRetainingCapacity();
        self.last_report.appendSliceAssumeCapacity(bytes);
        if (path) |owned| {
            if (self.reported) |previous| self.allocator.free(previous);
            self.reported = owned;
        }
    }
};

fn decodeLocalDirectory(allocator: std.mem.Allocator, report: []const u8, machine_name: []const u8) ![]u8 {
    const uri = std.Uri.parse(report) catch return error.InvalidDirectoryReport;
    if (!std.ascii.eqlIgnoreCase(uri.scheme, "file") or uri.user != null or uri.password != null or uri.port != null) return error.InvalidDirectoryReport;
    if (uri.host) |component| {
        const host = switch (component) {
            .raw => |value| value,
            .percent_encoded => |value| value,
        };
        if (host.len != 0 and !std.ascii.eqlIgnoreCase(host, "localhost") and !std.ascii.eqlIgnoreCase(host, machine_name)) return error.InvalidDirectoryReport;
    }
    const encoded = switch (uri.path) {
        .raw => |value| value,
        .percent_encoded => |value| value,
    };
    const decoded = try allocator.alloc(u8, encoded.len);
    errdefer allocator.free(decoded);
    var read_index: usize = 0;
    var written: usize = 0;
    while (read_index < encoded.len) : (written += 1) {
        if (encoded[read_index] == '%') {
            if (encoded.len - read_index < 3) return error.InvalidDirectoryReport;
            decoded[written] = std.fmt.parseInt(u8, encoded[read_index + 1 ..][0..2], 16) catch return error.InvalidDirectoryReport;
            read_index += 3;
        } else {
            decoded[written] = encoded[read_index];
            read_index += 1;
        }
    }
    const offset: usize = if (written >= 4 and decoded[0] == '/' and std.ascii.isAlphabetic(decoded[1]) and decoded[2] == ':') 1 else 0;
    std.mem.copyForwards(u8, decoded[0 .. written - offset], decoded[offset..written]);
    written -= offset;
    std.mem.replaceScalar(u8, decoded[0..written], '/', '\\');
    const path = DirectoryPath.parse(decoded[0..written]) catch return error.InvalidDirectoryReport;
    // Local reports must not be turned into network shares or device paths.
    if (!std.ascii.isAlphabetic(path.bytes[0]) or path.bytes[1] != ':') return error.InvalidDirectoryReport;
    const owned = try std.fs.path.resolveWindows(allocator, &.{path.bytes});
    allocator.free(decoded);
    return owned;
}

test "local OSC 7 paths decode Unicode/space escapes and local hostnames" {
    var pane = try PaneDirectory.init(std.testing.allocator, try DirectoryPath.parse("C:\\launch"));
    defer pane.deinit();
    for ([_][]const u8{ "file:///C:/My%20files/%C5%BB%C3%B3%C5%82%C4%87", "file://localhost/C:/My%20files/%C5%BB%C3%B3%C5%82%C4%87", "file://MY-PC/C:/My%20files/%C5%BB%C3%B3%C5%82%C4%87" }) |report| {
        try pane.applyReport(report, "my-pc");
        try std.testing.expectEqualStrings("C:\\My files\\Żółć", pane.current().bytes);
    }
}

test "rejected and empty reports preserve the last valid directory and isolated owners" {
    var pane = try PaneDirectory.init(std.testing.allocator, try DirectoryPath.parse("C:\\launch"));
    defer pane.deinit();
    try pane.applyReport("file:///C:/project", "my-pc");
    var sibling = try PaneDirectory.init(std.testing.allocator, pane.current());
    defer sibling.deinit();
    for ([_][]const u8{ "", "invalid", "https://example.org", "file://remote-host/C:/other", "file:///C:/bad%0Apath", "file:///C:/bad%00path", "file:///C:/bad%1Bpath", "file:///C:/bad%C2%85path", "file:///C:/bad%FFpath", "file:///C:/bad%GGpath", "file:///home/user", "file:////server/share", "file://user@localhost/C:/x" }) |report| {
        try pane.applyReport(report, "my-pc");
        try std.testing.expectEqualStrings("C:\\project", pane.current().bytes);
    }
    try pane.applyReport("file:///D:/first/../other/./", "my-pc");
    try std.testing.expectEqualStrings("C:\\project", sibling.current().bytes);
    try std.testing.expectEqualStrings("D:\\other", pane.current().bytes);
}

fn checkDirectoryAllocations(allocator: std.mem.Allocator) !void {
    var pane = try PaneDirectory.init(allocator, try DirectoryPath.parse("C:\\launch"));
    defer pane.deinit();
    try pane.applyReport("file:///C:/My%20files", "my-pc");
    const storage = pane.reported;
    try pane.applyReport("file:///C:/My%20files", "my-pc");
    try std.testing.expectEqual(storage, pane.reported);
    pane.applyReport("file:///D:/next", "my-pc") catch |err| {
        try std.testing.expectEqualStrings("C:\\My files", pane.current().bytes);
        try std.testing.expectEqualStrings("file:///C:/My%20files", pane.last_report.items);
        return err;
    };
}

test "directory allocation failures roll back path/cache and free every buffer" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkDirectoryAllocations, .{});
}

test "launch paths reject relative/control/WSL inputs before spawning a Windows shell" {
    for ([_][]const u8{ "", "C:relative", "relative", "C:\\bad\npath" }) |path| try std.testing.expectError(error.InvalidDirectoryPath, DirectoryPath.parse(path));
    try std.testing.expectError(error.WslDirectoryUnsupported, DirectoryPath.parse("\\\\wsl.localhost\\Ubuntu\\home\\user"));
    try std.testing.expectEqualStrings("\\\\server\\share", (try DirectoryPath.parse("\\\\server\\share")).bytes);
}
