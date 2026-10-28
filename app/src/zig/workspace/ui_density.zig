const std = @import("std");

/// Density changes chrome spacing, never terminal typography or Windows caption controls.
pub const UiDensity = enum(u32) {
    compact,
    comfortable,
    spacious,

    pub fn metrics(self: UiDensity) Metrics {
        return switch (self) {
            .compact => .{ .pane_header_height = 24, .pane_padding = 4, .workspace_padding = 0, .tab_inset = 8, .tab_max_width = 180, .pane_gap = 0 },
            .comfortable => .{ .pane_header_height = 28, .pane_padding = 6, .workspace_padding = 0, .tab_inset = 5, .tab_max_width = 200, .pane_gap = 6 },
            .spacious => .{ .pane_header_height = 36, .pane_padding = 10, .workspace_padding = 8, .tab_inset = 3, .tab_max_width = 220, .pane_gap = 6 },
        };
    }
};

pub const Metrics = struct {
    pane_gap: f32,
    pane_header_height: f32,
    pane_padding: f32,
    workspace_padding: f32,
    tab_inset: f32,
    tab_max_width: f32,

    pub fn gridTop(self: Metrics) f32 {
        return self.pane_header_height + 2;
    }
    pub fn horizontalInset(self: Metrics) f32 {
        return self.pane_padding * 2 + 12;
    }
    pub fn verticalInset(self: Metrics) f32 {
        return self.gridTop() + self.pane_padding;
    }
};

test "density preserves the existing default and gains terminal space in compact mode" {
    const compact = UiDensity.compact.metrics();
    const comfortable = UiDensity.comfortable.metrics();
    const spacious = UiDensity.spacious.metrics();
    try std.testing.expectEqual(@as(f32, 30), comfortable.gridTop());
    try std.testing.expectEqual(@as(f32, 24), comfortable.horizontalInset());
    try std.testing.expectEqual(@as(f32, 36), comfortable.verticalInset());
    try std.testing.expect(compact.horizontalInset() < comfortable.horizontalInset());
    try std.testing.expect(compact.verticalInset() < comfortable.verticalInset());
    try std.testing.expect(spacious.verticalInset() > comfortable.verticalInset());
    try std.testing.expect(compact.pane_header_height >= 24);
}
