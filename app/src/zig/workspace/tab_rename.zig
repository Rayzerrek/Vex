const std = @import("std");
const TabId = @import("workspace_model.zig").TabId;

/// Inline edit storage is independent of live OSC titles and workspace reordering.
pub const TabRename = struct {
    tab_id: TabId,
    buf: [64]u8 = undefined,
    len: u8 = 0,
    cursor: u8 = 0,
    select_all: bool = true,

    /// Dynamic terminal titles can exceed edit storage; retain complete codepoints only.
    pub fn init(tab_id: TabId, initial: []const u8) TabRename {
        var edit: TabRename = .{ .tab_id = tab_id };
        const prefix = boundedPrefix(initial, edit.buf.len) orelse return edit;
        @memcpy(edit.buf[0..prefix.len], prefix);
        edit.len = @intCast(prefix.len);
        edit.cursor = edit.len;
        return edit;
    }

    /// Borrowed tab rename text; cursor and length always lie on UTF-8 boundaries.
    pub fn slice(self: *const TabRename) []const u8 {
        return self.buf[0..self.len];
    }

    /// Typing and clipboard input share bounds; invalid input must not clear the selected name.
    pub fn append(self: *TabRename, text: []const u8) bool {
        if (self.select_all) {
            const prefix = boundedPrefix(text, self.buf.len) orelse return false;
            if (prefix.len == 0) return false;
            @memcpy(self.buf[0..prefix.len], prefix);
            self.len = @intCast(prefix.len);
            self.cursor = self.len;
            self.select_all = false;
            return true;
        }
        const pos: usize = @min(@as(usize, self.cursor), @as(usize, self.len));
        const prefix = boundedPrefix(text, self.buf.len - self.len) orelse return false;
        if (prefix.len == 0) return false;
        if (pos < self.len) {
            std.mem.copyBackwards(u8, self.buf[pos + prefix.len .. self.len + prefix.len], self.buf[pos..self.len]);
        }
        @memcpy(self.buf[pos..][0..prefix.len], prefix);
        self.len = @intCast(@as(usize, self.len) + prefix.len);
        self.cursor = @intCast(pos + prefix.len);
        return true;
    }

    pub fn selectAll(self: *TabRename) bool {
        const had_selection = self.select_all;
        self.cursor = self.len;
        self.select_all = self.len > 0;
        return had_selection != self.select_all;
    }

    pub fn moveHome(self: *TabRename) bool {
        const changed = self.select_all or self.cursor != 0;
        self.select_all = false;
        self.cursor = 0;
        return changed;
    }

    pub fn moveEnd(self: *TabRename) bool {
        const changed = self.select_all or self.cursor != self.len;
        self.select_all = false;
        self.cursor = self.len;
        return changed;
    }

    pub fn moveLeft(self: *TabRename, by_word: bool) bool {
        if (self.select_all) {
            self.select_all = false;
            self.cursor = 0;
            return true;
        }
        if (self.cursor == 0) return false;
        self.cursor = prevBoundary(self.buf[0..self.len], self.cursor, by_word);
        return true;
    }

    pub fn moveRight(self: *TabRename, by_word: bool) bool {
        if (self.select_all) {
            self.select_all = false;
            self.cursor = self.len;
            return true;
        }
        if (self.cursor >= self.len) return false;
        self.cursor = nextBoundary(self.buf[0..self.len], self.cursor, by_word);
        return true;
    }

    pub fn deleteBackward(self: *TabRename, by_word: bool) bool {
        if (self.select_all) {
            const changed = self.len > 0 or self.select_all;
            self.len = 0;
            self.cursor = 0;
            self.select_all = false;
            return changed;
        }
        if (self.cursor == 0) return false;
        const start = prevBoundary(self.buf[0..self.len], self.cursor, by_word);
        const tail = self.len - self.cursor;
        std.mem.copyForwards(u8, self.buf[start .. start + tail], self.buf[self.cursor..self.len]);
        self.len = @intCast(start + tail);
        self.cursor = start;
        return true;
    }

    pub fn deleteForward(self: *TabRename, by_word: bool) bool {
        if (self.select_all) {
            const changed = self.len > 0 or self.select_all;
            self.len = 0;
            self.cursor = 0;
            self.select_all = false;
            return changed;
        }
        if (self.cursor >= self.len) return false;
        const end = nextBoundary(self.buf[0..self.len], self.cursor, by_word);
        const tail = self.len - end;
        std.mem.copyForwards(u8, self.buf[self.cursor .. self.cursor + tail], self.buf[end..self.len]);
        self.len = @intCast(self.cursor + tail);
        return true;
    }
};

fn isWordSeparator(byte: u8) bool {
    return switch (byte) {
        ' ', '\t', '-', '_', '/', '\\', '.' => true,
        else => false,
    };
}

fn prevCodepoint(text: []const u8, pos: u8) u8 {
    var idx = @min(pos, @as(u8, @intCast(text.len)));
    if (idx == 0) return 0;
    idx -= 1;
    while (idx > 0 and (text[idx] & 0xC0) == 0x80) : (idx -= 1) {}
    return idx;
}

fn nextCodepoint(text: []const u8, pos: u8) u8 {
    const len: u8 = @intCast(text.len);
    var idx = @min(pos, len);
    if (idx >= len) return len;
    idx += 1;
    while (idx < len and (text[idx] & 0xC0) == 0x80) : (idx += 1) {}
    return idx;
}

fn prevBoundary(text: []const u8, pos: u8, by_word: bool) u8 {
    if (!by_word) return prevCodepoint(text, pos);
    var idx = @min(pos, @as(u8, @intCast(text.len)));
    while (idx > 0 and isWordSeparator(text[idx - 1])) : (idx -= 1) {}
    while (idx > 0 and !isWordSeparator(text[idx - 1])) {
        idx = prevCodepoint(text, idx);
    }
    return idx;
}

fn nextBoundary(text: []const u8, pos: u8, by_word: bool) u8 {
    if (!by_word) return nextCodepoint(text, pos);
    const len: u8 = @intCast(text.len);
    var idx = @min(pos, len);
    while (idx < len and isWordSeparator(text[idx])) : (idx += 1) {}
    while (idx < len and !isWordSeparator(text[idx])) {
        idx = nextCodepoint(text, idx);
    }
    return idx;
}

fn boundedPrefix(text: []const u8, capacity: usize) ?[]const u8 {
    if (!std.unicode.utf8ValidateSlice(text)) return null;
    for (text) |byte| if (byte < 0x20 or byte == 0x7f) return null;
    var len = @min(text.len, capacity);
    if (len < text.len) while (len > 0 and text[len] & 0xc0 == 0x80) : (len -= 1) {};
    return text[0..len];
}

test "rename initialization and paste never split a UTF-8 codepoint" {
    const text = "x" ** 63 ++ "😀suffix";
    var edit = TabRename.init(@enumFromInt(1), text);
    try std.testing.expect(std.unicode.utf8ValidateSlice(edit.buf[0..edit.len]));
    try std.testing.expectEqualStrings("x" ** 63, edit.buf[0..edit.len]);
    try std.testing.expect(edit.select_all);
    try std.testing.expect(edit.append(text));
    try std.testing.expect(std.unicode.utf8ValidateSlice(edit.buf[0..edit.len]));
    try std.testing.expectEqualStrings("x" ** 63, edit.buf[0..edit.len]);
    try std.testing.expect(!edit.select_all);
    try std.testing.expect(!edit.append("😀"));
    try std.testing.expect(edit.append("y"));
    try std.testing.expectEqual(@as(u8, 64), edit.len);
}

test "rename rejects malformed input without changing selection or existing text" {
    var edit = TabRename.init(@enumFromInt(7), "original");
    try std.testing.expect(!edit.append("\xff"));
    try std.testing.expect(!edit.append("line\nbreak"));
    try std.testing.expect(!edit.append("embedded\x00nul"));
    try std.testing.expect(!edit.append(""));
    try std.testing.expect(edit.select_all);
    try std.testing.expectEqualStrings("original", edit.buf[0..edit.len]);
    try std.testing.expect(edit.append("音"));
    try std.testing.expect(!edit.select_all);
    try std.testing.expect(!edit.append("\xf0\x9f"));
    try std.testing.expectEqualStrings("音", edit.buf[0..edit.len]);
    try std.testing.expectEqual(@as(TabId, @enumFromInt(7)), edit.tab_id);
    const invalid = TabRename.init(@enumFromInt(7), "\xff");
    try std.testing.expectEqual(@as(u8, 0), invalid.len);
}

test "rename cursor navigation, mid-string UTF-8 insertion, and word deletion preserve codepoints" {
    var edit = TabRename.init(@enumFromInt(3), "api server");
    try std.testing.expect(edit.select_all);
    try std.testing.expectEqual(@as(u8, 10), edit.cursor);

    // Left from select_all collapses selection to the start.
    try std.testing.expect(edit.moveLeft(false));
    try std.testing.expect(!edit.select_all);
    try std.testing.expectEqual(@as(u8, 0), edit.cursor);
    try std.testing.expect(edit.append("v2-"));
    try std.testing.expectEqualStrings("v2-api server", edit.slice());

    // Word jump right moves past "api", then Delete removes the space and "server" by word.
    try std.testing.expect(edit.moveRight(true));
    try std.testing.expectEqualStrings("v2-api", edit.slice()[0..edit.cursor]);
    try std.testing.expect(edit.deleteForward(true));
    try std.testing.expectEqualStrings("v2-api", edit.slice());

    // End, multi-byte append, left by codepoint, and backward delete.
    _ = edit.moveEnd();
    try std.testing.expect(edit.append("🚀!"));
    try std.testing.expectEqualStrings("v2-api🚀!", edit.slice());
    try std.testing.expect(edit.moveLeft(false));
    try std.testing.expect(edit.deleteBackward(false));
    try std.testing.expectEqualStrings("v2-api!", edit.slice());
    try std.testing.expect(edit.deleteBackward(true));
    try std.testing.expectEqualStrings("v2-!", edit.slice());
    try std.testing.expect(edit.selectAll());
    try std.testing.expect(edit.select_all);
    try std.testing.expect(edit.deleteBackward(false));
    try std.testing.expectEqualStrings("", edit.slice());
}
