const std = @import("std");
const TabId = @import("workspace_model.zig").TabId;

/// Inline edit storage is independent of live OSC titles and workspace reordering.
pub const TabRename = struct {
    tab_id: TabId,
    buf: [64]u8 = undefined,
    len: u8 = 0,
    select_all: bool = true,

    /// Dynamic terminal titles can exceed edit storage; retain complete codepoints only.
    pub fn init(tab_id: TabId, initial: []const u8) TabRename {
        var edit: TabRename = .{ .tab_id = tab_id };
        const prefix = boundedPrefix(initial, edit.buf.len) orelse return edit;
        @memcpy(edit.buf[0..prefix.len], prefix);
        edit.len = @intCast(prefix.len);
        return edit;
    }

    /// Typing and clipboard input share bounds; invalid input must not clear the selected name.
    pub fn append(self: *TabRename, text: []const u8) bool {
        const offset = if (self.select_all) 0 else self.len;
        const prefix = boundedPrefix(text, self.buf.len - offset) orelse return false;
        if (prefix.len == 0) return false;
        @memcpy(self.buf[offset..][0..prefix.len], prefix);
        self.len = @intCast(offset + prefix.len);
        self.select_all = false;
        return true;
    }
};

fn boundedPrefix(text: []const u8, capacity: usize) ?[]const u8 {
    if (!std.unicode.utf8ValidateSlice(text)) return null;
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
