const std = @import("std");

/// Checks UTF-8/NUL and the Windows command line bound in UTF-16 units, excluding its terminator.
pub fn validateShellText(bytes: []const u8) !void {
    if (!std.unicode.utf8ValidateSlice(bytes) or std.mem.indexOfScalar(u8, bytes, 0) != null) return error.InvalidShellText;
    var units: usize = 0;
    var iterator = std.unicode.Utf8View.initUnchecked(bytes).iterator();
    while (iterator.nextCodepoint()) |scalar| units += if (scalar > 0xffff) @as(usize, 2) else 1;
    if (units >= 32767) return error.ShellCommandLineTooLong;
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

test "Windows quoting handles empty/space/Unicode/quote and trailing backslash arguments" {
    for ([_]struct { input: []const u8, output: []const u8 }{
        .{ .input = "", .output = "\"\"" },
        .{ .input = "a b", .output = "\"a b\"" },
        .{ .input = "Żółć", .output = "\"Żółć\"" },
        .{ .input = "a\"b", .output = "\"a\\\"b\"" },
        .{ .input = "C:\\folder\\", .output = "\"C:\\folder\\\\\"" },
    }) |fixture| {
        const quoted = try quoteWindowsArgument(std.testing.allocator, fixture.input);
        defer std.testing.allocator.free(quoted);
        try std.testing.expectEqualStrings(fixture.output, quoted);
    }
}
