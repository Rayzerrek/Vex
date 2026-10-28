const std = @import("std");

/// Stationary cursor refreshes must not undo a keyboard-selected card.
pub const SelectionInput = struct {
    point: ?struct { x: f32, y: f32 } = null,
    keyboard: bool = false,

    pub fn fromKeyboard(self: *SelectionInput) void {
        self.keyboard = true;
    }
    pub fn hover(self: *SelectionInput, x: f32, y: f32) bool {
        const moved = if (self.point) |point| point.x != x or point.y != y else false;
        const accept = moved or (self.point == null and !self.keyboard);
        self.point = .{ .x = x, .y = y };
        if (moved) self.keyboard = false;
        return accept;
    }
    pub fn press(self: *SelectionInput, x: f32, y: f32) void {
        self.point = .{ .x = x, .y = y };
        self.keyboard = false;
    }
};

test "keyboard selection survives repeated hover until the pointer moves or clicks" {
    var input: SelectionInput = .{};
    try std.testing.expect(input.hover(100, 200));
    input.fromKeyboard();
    for (0..20) |_| try std.testing.expect(!input.hover(100, 200));
    try std.testing.expect(input.hover(101, 200));
    input.fromKeyboard();
    input.press(101, 200);
    try std.testing.expect(!input.keyboard);
    var unseen: SelectionInput = .{};
    unseen.fromKeyboard();
    try std.testing.expect(!unseen.hover(10, 20));
    try std.testing.expect(unseen.hover(10, 21));
}
