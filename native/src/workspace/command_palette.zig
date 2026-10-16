const std = @import("std");

/// Inline command-palette query and viewport; selection indices always address the full result list.
pub const CommandPalette = struct {
    query_buf: [128]u8 = undefined,
    query_len: usize = 0,
    selected_index: usize = 0,
    first_visible: usize = 0,
    hovered_index: ?usize = null,

    /// Rejects malformed/control input and retains only complete UTF-8 codepoints without allocation.
    pub fn appendQuery(self: *CommandPalette, text: []const u8) bool {
        if (!std.unicode.utf8ValidateSlice(text)) return false;
        for (text) |byte| if (byte < 32 or byte == 127) return false;
        var len = @min(text.len, self.query_buf.len - self.query_len);
        if (len < text.len) while (len > 0 and text[len] & 0xc0 == 0x80) : (len -= 1) {};
        if (len == 0) return false;
        @memcpy(self.query_buf[self.query_len..][0..len], text[0..len]);
        self.query_len += len;
        self.first_visible = 0;
        self.selected_index = 0;
        self.hovered_index = null;
        return true;
    }
    /// Backspace removes one codepoint; clear resets the entire query and pointer selection.
    pub fn eraseQuery(self: *CommandPalette, clear: bool) void {
        if (clear) self.query_len = 0 else if (self.query_len > 0) {
            self.query_len -= 1;
            while (self.query_len > 0 and self.query_buf[self.query_len] & 0xc0 == 0x80) : (self.query_len -= 1) {}
        }
        self.selected_index = 0;
        self.first_visible = 0;
        self.hovered_index = null;
    }
    /// Wraps through every command, not merely the currently painted rows.
    pub fn moveSelection(self: *CommandPalette, delta: isize, count: usize) void {
        self.hovered_index = null;
        self.selected_index = if (count == 0) 0 else @intCast(@mod(@as(isize, @intCast(self.selected_index)) + delta, @as(isize, @intCast(count))));
    }
    /// Derives a bounded scrolling window from the selected command without retaining stale result indices.
    pub fn firstVisible(self: *CommandPalette, rows: usize, count: usize) usize {
        if (rows == 0 or count <= rows) {
            self.first_visible = 0;
        } else {
            const selected = @min(self.selected_index, count - 1);
            if (selected < self.first_visible) self.first_visible = selected;
            if (selected >= self.first_visible + rows) self.first_visible = selected - rows + 1;
            self.first_visible = @min(self.first_visible, count - rows);
        }
        return self.first_visible;
    }
};

/// Palette paint and hit-test geometry in DIPs, including small-window row limits.
pub const PaletteLayout = struct {
    x: f32,
    y: f32,
    width: f32,
    height: f32,
    rows: usize,
    pub const row_height: f32 = 34;
    pub const list_offset: f32 = 66;

    pub fn init(width: f32, height: f32, count: usize) PaletteLayout {
        const w = @min(@as(f32, 600), @max(@as(f32, 300), width - 48));
        const available = @max(@as(f32, 1), @floor((height - 58 - list_offset - 40) / row_height));
        const rows = @min(@as(usize, @intFromFloat(available)), @min(count, 9));
        return .{ .x = @round((width - w) * 0.5), .y = 58, .width = w, .height = list_offset + @as(f32, @floatFromInt(@max(rows, 1))) * row_height + 34, .rows = rows };
    }
    pub fn hitRow(self: PaletteLayout, x: f32, y: f32) ?usize {
        if (x < self.x + 10 or x >= self.x + self.width - 10 or y < self.y + list_offset) return null;
        const row = @floor((y - self.y - list_offset) / row_height);
        return if (row < @as(f32, @floatFromInt(self.rows))) @intFromFloat(row) else null;
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

test "palette reaches every result, scrolls hit rows, and bounds small-window geometry" {
    var palette: CommandPalette = .{};
    palette.moveSelection(-1, 20);
    try std.testing.expectEqual(@as(usize, 19), palette.selected_index);
    try std.testing.expectEqual(@as(usize, 11), palette.firstVisible(9, 20));
    palette.selected_index = 12;
    try std.testing.expectEqual(@as(usize, 11), palette.firstVisible(9, 20));
    palette.selected_index = 19;
    palette.moveSelection(1, 20);
    try std.testing.expectEqual(@as(usize, 0), palette.selected_index);
    const layout = PaletteLayout.init(640, 400, 20);
    try std.testing.expect(layout.rows < 9);
    try std.testing.expect(layout.y + layout.height <= 400);
    try std.testing.expectEqual(@as(?usize, 0), layout.hitRow(layout.x + 12, layout.y + PaletteLayout.list_offset));
    try std.testing.expectEqual(@as(?usize, null), layout.hitRow(layout.x + 12, layout.y + layout.height - 5));
}
test "palette input preserves UTF-8 and fuzzy matching ranks exact shortcuts" {
    var palette: CommandPalette = .{};
    try std.testing.expect(palette.appendQuery("😀"));
    palette.eraseQuery(false);
    try std.testing.expectEqual(@as(usize, 0), palette.query_len);
    const fill = [_]u8{'x'} ** 127;
    try std.testing.expect(palette.appendQuery(&fill));
    try std.testing.expect(!palette.appendQuery("😀"));
    try std.testing.expect(!palette.appendQuery("\xff"));
    try std.testing.expect(!palette.appendQuery("a\nb"));
    try std.testing.expect(commandMatchScore("Pane: Split Right", "Ctrl+Shift+R", "splr") != null);
    try std.testing.expect(commandMatchScore("Pane: Split Right", "Ctrl+Shift+R", "Ctrl+Shift+R").? > commandMatchScore("Pane: Split Right", "", "spr").?);
    try std.testing.expect(commandMatchScore("New Tab", "", "zzzz") == null);
}
