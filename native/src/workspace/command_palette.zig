const std = @import("std");

/// Native command-palette query snapshot; selection indices address the full result list.
pub const CommandPalette = struct {
    query_buf: [128]u8 = undefined,
    query_len: usize = 0,
    selected_index: usize = 0,

    /// Rejects malformed/control input and retains only complete UTF-8 codepoints without allocation.
    pub fn appendQuery(self: *CommandPalette, text: []const u8) bool {
        if (!std.unicode.utf8ValidateSlice(text)) return false;
        for (text) |byte| if (byte < 32 or byte == 127) return false;
        var len = @min(text.len, self.query_buf.len - self.query_len);
        if (len < text.len) while (len > 0 and text[len] & 0xc0 == 0x80) : (len -= 1) {};
        if (len == 0) return false;
        @memcpy(self.query_buf[self.query_len..][0..len], text[0..len]);
        self.query_len += len;
        self.selected_index = 0;
        return true;
    }
    /// Native controls own editing; each query event replaces the previous snapshot.
    pub fn clearQuery(self: *CommandPalette) void {
        self.query_len = 0;
        self.selected_index = 0;
    }
};

/// Scores all query tokens as ordered subsequences; contiguous/word-prefix matches outrank scattered bytes.
pub fn commandMatchScore(title: []const u8, shortcut: []const u8, query: []const u8) ?i32 {
    var score: i32 = 0;
    var tokens = std.mem.tokenizeAny(u8, query, " \t");
    while (tokens.next()) |token| {
        const title_score = tokenScore(title, token);
        const key_score = tokenScore(shortcut, token);
        if (title_score) |value| score += if (key_score) |key| @max(value, key) else value else if (key_score) |value| score += value else return null;
    }
    return score;
}
fn tokenScore(text: []const u8, query: []const u8) ?i32 {
    var at: usize = 0;
    var previous: ?usize = null;
    var score: i32 = 0;
    for (query) |byte| {
        while (at < text.len and std.ascii.toLower(text[at]) != std.ascii.toLower(byte)) : (at += 1) {}
        if (at == text.len) return null;
        score += 8;
        if (at == 0 or !std.ascii.isAlphanumeric(text[at - 1])) score += 12;
        if (previous) |prev| {
            if (at == prev + 1) score += 16;
        }
        previous = at;
        at += 1;
    }
    if (std.ascii.eqlIgnoreCase(text, query)) score += 200;
    return score;
}

test "palette input preserves UTF-8 and fuzzy matching ranks exact shortcuts" {
    var palette: CommandPalette = .{};
    try std.testing.expect(palette.appendQuery("😀"));
    palette.selected_index = 4;
    palette.clearQuery();
    try std.testing.expectEqual(@as(usize, 0), palette.query_len);
    try std.testing.expectEqual(@as(usize, 0), palette.selected_index);
    const fill = [_]u8{'x'} ** 127;
    try std.testing.expect(palette.appendQuery(&fill));
    try std.testing.expect(!palette.appendQuery("😀"));
    try std.testing.expect(!palette.appendQuery("\xff"));
    try std.testing.expect(!palette.appendQuery("a\nb"));
    try std.testing.expect(commandMatchScore("Pane: Split Right", "Ctrl+Shift+R", "splr") != null);
    try std.testing.expect(commandMatchScore("Pane: Split Right", "Ctrl+Shift+R", "Ctrl+Shift+R").? > commandMatchScore("Pane: Split Right", "", "spr").?);
    try std.testing.expect(commandMatchScore("New Tab", "", "zzzz") == null);
}
