const std = @import("std");
const command_line = @import("command_line.zig");

/// Parsed components of a sanitized OSC 0 / OSC 2 title, never a source of program status.
pub const ParsedTitle = struct {
    title: []const u8 = "",
    app_name: ?[]const u8 = null,
    is_modified: bool = false,
};

/// Inline owner of a sanitized display title; no application-name allowlist or heap storage.
pub const TerminalTitle = struct {
    bytes: [max_title_bytes]u8 = undefined,
    len: usize = 0,
    app_bytes: [max_app_bytes]u8 = undefined,
    app_len: usize = 0,
    generation: u64 = 0,
    pub const max_raw_bytes = 512;
    pub const max_title_bytes = 192;
    pub const max_app_bytes = 64;

    /// Borrows the current display title until the next report/reset.
    pub fn slice(self: *const TerminalTitle) []const u8 {
        return self.bytes[0..self.len];
    }
    /// Borrows a syntactically identified application token, not a trusted process identity.
    pub fn appSlice(self: *const TerminalTitle) []const u8 {
        return self.app_bytes[0..self.app_len];
    }
    /// Clears display metadata on RIS or when the shell restores its caption.
    pub fn reset(self: *TerminalTitle) void {
        if (self.len == 0 and self.app_len == 0) return;
        self.len = 0;
        self.app_len = 0;
        self.generation +%= 1;
    }
    /// Rejects oversized/malformed input before mutating the title; strips invisible controls.
    pub fn applyReport(self: *TerminalTitle, raw: []const u8) !void {
        var scratch: [max_raw_bytes]u8 = undefined;
        const parsed = try parseTitle(raw, &scratch);
        var next: [max_title_bytes]u8 = undefined;
        const base = utf8Prefix(parsed.title, max_title_bytes - @as(usize, @intFromBool(parsed.is_modified)));
        @memcpy(next[0..base.len], base);
        const len = base.len + @intFromBool(parsed.is_modified);
        if (parsed.is_modified) next[base.len] = '*';
        const app = utf8Prefix(parsed.app_name orelse "", max_app_bytes);
        if (std.mem.eql(u8, self.slice(), next[0..len]) and std.mem.eql(u8, self.appSlice(), app)) return;
        @memcpy(self.bytes[0..len], next[0..len]);
        self.len = len;
        @memcpy(self.app_bytes[0..app.len], app);
        self.app_len = app.len;
        self.generation +%= 1;
    }
};

const whitespace = " \t\r\n";
const shell_captions = [_][]const u8{ "cmd", "pwsh", "powershell", "Windows PowerShell", "Command Prompt", "bash", "sh", "zsh", "fish", "nu", "nushell", "wsl" };

/// Parses generic file/app separator conventions; free-form labels remain intact for unknown programs.
pub fn parseTitle(raw: []const u8, scratch: *[TerminalTitle.max_raw_bytes]u8) !ParsedTitle {
    if (raw.len > scratch.len) return error.InvalidTerminalTitle;
    const sanitized = sanitizeDisplayText(raw, scratch) orelse return error.InvalidTerminalTitle;
    var text: []const u8 = std.mem.trim(u8, sanitized, whitespace);
    const admin = "Administrator: ";
    if (std.ascii.startsWithIgnoreCase(text, admin)) text = text[admin.len..];
    if (isShellCaption(text)) return .{};
    if (std.mem.indexOf(u8, text, " - ")) |index| {
        if (isShellCaption(text[0..index])) text = text[index + 3 ..];
    }
    if (text.len == 0 or isShellCaption(text)) return .{};

    for ([_][]const u8{ " - ", " : ", " | " }) |separator| {
        const index = std.mem.lastIndexOf(u8, text, separator) orelse continue;
        const left = stripModified(std.mem.trim(u8, text[0..index], whitespace));
        const right = stripModified(std.mem.trim(u8, text[index + separator.len ..], whitespace));
        if (left.title.len == 0 or right.title.len == 0) continue;
        if (isAppToken(right.title) and (left.is_modified or looksLikeFile(left.title))) {
            return .{ .title = fileLabel(left.title), .app_name = command_line.executableStem(right.title), .is_modified = left.is_modified };
        }
        if (isAppToken(left.title)) {
            return .{ .title = right.title, .app_name = command_line.executableStem(left.title), .is_modified = right.is_modified };
        }
    }
    const modified = stripModified(text);
    if (modified.is_modified) return .{ .title = fileLabel(modified.title), .is_modified = true };
    const rooted = text[0] == '/' or text[0] == '\\' or std.mem.startsWith(u8, text, "~/") or
        (text.len > 2 and text[1] == ':' and (text[2] == '\\' or text[2] == '/'));
    if (rooted and !isExecutablePath(text)) return .{ .title = fileLabel(text) };
    const first_end = std.mem.indexOfAny(u8, text, " \t") orelse text.len;
    if (isExecutablePath(text)) {
        const app = command_line.executableStem(text);
        return .{ .title = app, .app_name = app };
    }
    if (command_line.isCommandHost(text[0..first_end])) {
        if (command_line.parseCommandLineApp(text)) |app| return .{ .title = app, .app_name = app };
    }
    return .{ .title = text, .app_name = if (isAppToken(text)) text else null };
}
/// Copies a UTF-8-safe single-line prefix without controls or invisible formatting; malformed metadata is absent.
pub fn sanitizeDisplayText(raw: []const u8, buffer: []u8) ?[]u8 {
    const view = std.unicode.Utf8View.init(raw) catch return null;
    var iter = view.iterator();
    var len: usize = 0;
    while (iter.nextCodepointSlice()) |bytes| {
        const cp = std.unicode.utf8Decode(bytes) catch return null;
        if (cp < 32 or (cp >= 127 and cp <= 159) or isFormatScalar(cp)) continue;
        if (bytes.len > buffer.len - len) break;
        @memcpy(buffer[len..][0..bytes.len], bytes);
        len += bytes.len;
    }
    return buffer[0..len];
}
fn isShellCaption(text: []const u8) bool {
    const stem = command_line.executableStem(text);
    for (shell_captions) |caption| if (std.ascii.eqlIgnoreCase(text, caption) or std.ascii.eqlIgnoreCase(stem, caption)) return true;
    return false;
}
fn isAppToken(text: []const u8) bool {
    if (text.len == 0) return false;
    // Pi prints its brand glyph instead of its name, so the glyph names the same application.
    if (std.mem.eql(u8, text, "π")) return true;
    for (text) |byte| if (!std.ascii.isAlphanumeric(byte) and std.mem.indexOfScalar(u8, "_.+-", byte) == null) return false;
    return true;
}
fn looksLikeFile(text: []const u8) bool {
    const end = std.mem.indexOf(u8, text, " (") orelse text.len;
    const stem = text[0..end];
    return std.mem.indexOfScalar(u8, stem, '.') != null or std.mem.indexOfAny(u8, stem, "/\\") != null;
}
fn isExecutablePath(text: []const u8) bool {
    for ([_][]const u8{ ".exe", ".com", ".cmd", ".bat", ".ps1", ".js", ".mjs", ".py", ".sh" }) |ext| {
        if (std.ascii.endsWithIgnoreCase(text, ext)) return true;
    }
    return false;
}
fn fileLabel(raw: []const u8) []const u8 {
    const end = std.mem.indexOf(u8, raw, " (") orelse raw.len;
    const text = std.mem.trimRight(u8, raw[0..end], "/\\");
    if (text.len == 0 or (text.len == 2 and text[1] == ':')) return raw[0..end];
    const sep = std.mem.lastIndexOfAny(u8, text, "/\\") orelse return text;
    return text[sep + 1 ..];
}
fn stripModified(text: []const u8) ParsedTitle {
    for ([_][]const u8{ "[+]", "[*]", "[modified]", "(modified)" }) |marker| {
        if (std.ascii.endsWithIgnoreCase(text, marker)) return .{ .title = std.mem.trimRight(u8, text[0 .. text.len - marker.len], whitespace), .is_modified = true };
    }
    if (std.mem.endsWith(u8, text, "*") and looksLikeFile(text)) return .{ .title = text[0 .. text.len - 1], .is_modified = true };
    return .{ .title = text };
}
fn utf8Prefix(text: []const u8, capacity: usize) []const u8 {
    var len = @min(text.len, capacity);
    if (len < text.len) while (len > 0 and text[len] & 0xc0 == 0x80) : (len -= 1) {};
    return text[0..len];
}
fn isFormatScalar(cp: u21) bool {
    return switch (cp) {
        0xad, 0x61c, 0x180e, 0x200b...0x200f, 0x202a...0x202e, 0x2060...0x206f, 0xfeff, 0xfff9...0xfffb, 0x1bca0...0x1bca3, 0x1d173...0x1d17a, 0xe0001, 0xe0020...0xe007f => true,
        else => false,
    };
}

test "titles treat known and future applications alike without guessing status from free text" {
    var scratch: [TerminalTitle.max_raw_bytes]u8 = undefined;
    for ([_][]const u8{ "new-agent", "claude", "opencode", "pi-future", "π" }) |app| {
        var raw: [128]u8 = undefined;
        const parsed = try parseTitle(try std.fmt.bufPrint(&raw, "{s} - Fix rendering", .{app}), &scratch);
        try std.testing.expectEqualStrings("Fix rendering", parsed.title);
        try std.testing.expectEqualStrings(app, parsed.app_name orelse return error.MissingTitleApp);
    }
    try std.testing.expectEqualStrings("Fix authentication flow", (try parseTitle("Fix authentication flow", &scratch)).title);
    try std.testing.expectEqualStrings("future", (try parseTitle("C:\\code\\future", &scratch)).title);
    try std.testing.expectEqualStrings("future tool", (try parseTitle("C:\\My Tools\\future tool.exe", &scratch)).title);
    const file = try parseTitle("main.zig [+] - future-editor", &scratch);
    try std.testing.expectEqualStrings("main.zig", file.title);
    try std.testing.expectEqualStrings("future-editor", file.app_name orelse return error.MissingTitleApp);
    try std.testing.expect(file.is_modified);
    try std.testing.expectEqualStrings("main.rs", (try parseTitle("src/main.rs (~/project) - nvim", &scratch)).title);
    try std.testing.expectEqualStrings("", (try parseTitle("Administrator: Windows PowerShell", &scratch)).title);
    try std.testing.expectEqualStrings("nvim", (try parseTitle("pwsh - nvim", &scratch)).title);
    var title: TerminalTitle = .{};
    try title.applyReport("\x07\x1b\u{200b}main.zig [+] - future-editor\u{202e}");
    try std.testing.expectEqualStrings("main.zig*", title.slice());
    try std.testing.expectError(error.InvalidTerminalTitle, title.applyReport("\xff"));
    try std.testing.expectEqualStrings("main.zig*", title.slice());
    title.reset();
    try std.testing.expectEqualStrings("", title.slice());
    var tiny: [5]u8 = undefined;
    try std.testing.expectEqualStrings("a音", sanitizeDisplayText("a\u{202e}音音", &tiny) orelse return error.MissingDisplayText);
    try std.testing.expect(sanitizeDisplayText("\xff", &tiny) == null);
}
