const std = @import("std");

/// Density changes chrome spacing, never terminal typography or Windows caption controls.
/// Terminal padding is shared by all presets; only the surrounding chrome varies.
pub const UiDensity = enum(u32) {
    compact,
    comfortable,
    spacious,

    /// Fixed breathing room between the terminal cells and the pane edges.
    pub const terminal_padding: f32 = 12;

    pub fn metrics(self: UiDensity) Metrics {
        return switch (self) {
            .compact => .{ .pane_header_height = 28, .header_icon = 14, .header_action = 22, .header_inset = 12, .header_edge = 8, .header_title_size = 14.5, .pane_padding = terminal_padding, .workspace_padding = 0, .tab_inset = 8, .tab_max_width = 180, .pane_gap = 0 },
            .comfortable => .{ .pane_header_height = 32, .header_icon = 14, .header_action = 22, .header_inset = 13, .header_edge = 8, .header_title_size = 14.5, .pane_padding = terminal_padding, .workspace_padding = 6, .tab_inset = 5, .tab_max_width = 200, .pane_gap = 6 },
            .spacious => .{ .pane_header_height = 39, .header_icon = 15, .header_action = 23, .header_inset = 14, .header_edge = 9, .header_title_size = 15, .pane_padding = terminal_padding, .workspace_padding = 8, .tab_inset = 3, .tab_max_width = 220, .pane_gap = 6 },
        };
    }
};

pub const Metrics = struct {
    pane_gap: f32,
    pane_header_height: f32,
    /// Program icon edge in the pane header; the title inset follows it.
    header_icon: f32,
    /// Edge of the square header action buttons; hit testing and drawing both derive slots from it.
    header_action: f32,
    /// Space between the header's left edge and the program icon.
    header_inset: f32,
    /// Space between the header's right edge and the right-most action button.
    header_edge: f32,
    /// Pane caption size in DIPs.
    header_title_size: f32,
    pane_padding: f32,
    workspace_padding: f32,
    tab_inset: f32,
    tab_max_width: f32,

    /// Top of the terminal content box after the pane header, rule gap and padding.
    pub fn terminalTop(self: Metrics) f32 {
        return self.pane_header_height + 2 + self.pane_padding;
    }
    /// Top of the action buttons relative to the pane, centered in the header rule's inner area.
    pub fn headerActionTop(self: Metrics) f32 {
        return (self.pane_header_height - self.header_action - 1) / 2;
    }
    /// Space reserved for the two side margins before fitting whole cells.
    pub fn horizontalInset(self: Metrics) f32 {
        return self.pane_padding * 2;
    }
    pub fn verticalInset(self: Metrics) f32 {
        return self.terminalTop() + self.pane_padding;
    }
};

test "density shares terminal padding and varies the space taken by chrome" {
    const compact = UiDensity.compact.metrics();
    const comfortable = UiDensity.comfortable.metrics();
    const spacious = UiDensity.spacious.metrics();
    try std.testing.expectEqual(@as(f32, 46), comfortable.terminalTop());
    try std.testing.expectEqual(@as(f32, 24), comfortable.horizontalInset());
    try std.testing.expectEqual(@as(f32, 58), comfortable.verticalInset());
    try std.testing.expectEqual(@as(f32, 6), comfortable.workspace_padding);
    try std.testing.expectEqual(compact.horizontalInset(), comfortable.horizontalInset());
    try std.testing.expect(compact.verticalInset() < comfortable.verticalInset());
    try std.testing.expect(spacious.verticalInset() > comfortable.verticalInset());
    try std.testing.expect(compact.pane_header_height >= 24);
    try std.testing.expectEqual(@as(f32, 4.5), comfortable.headerActionTop());
    for (std.enums.values(UiDensity)) |value| {
        const metrics = value.metrics();
        try std.testing.expectEqual(@as(f32, 12), metrics.pane_padding);
        try std.testing.expectEqual(metrics.pane_padding * 2, metrics.horizontalInset());
        try std.testing.expectEqual(metrics.terminalTop() + metrics.pane_padding, metrics.verticalInset());
    }
}

test "every density enlarges the pane header relative to the previous layout" {
    // Previous fixed values: header 24/28/36, icon 12, action 20, inset 11, edge 6, title 14.
    const previous = [_]struct { header: f32, icon: f32, action: f32, inset: f32, edge: f32, title: f32 }{
        .{ .header = 24, .icon = 12, .action = 20, .inset = 11, .edge = 6, .title = 14 },
        .{ .header = 28, .icon = 12, .action = 20, .inset = 11, .edge = 6, .title = 14 },
        .{ .header = 36, .icon = 12, .action = 20, .inset = 11, .edge = 6, .title = 14 },
    };
    for (std.enums.values(UiDensity), previous) |value, old| {
        const metrics = value.metrics();
        try std.testing.expect(metrics.pane_header_height > old.header);
        try std.testing.expect(metrics.header_icon > old.icon);
        try std.testing.expect(metrics.header_action > old.action);
        try std.testing.expect(metrics.header_inset > old.inset);
        try std.testing.expect(metrics.header_edge >= old.edge);
        try std.testing.expect(metrics.header_title_size > old.title);
    }
}

test "header actions, icons and caption stay inside the header rule in every density" {
    for (std.enums.values(UiDensity)) |value| {
        const metrics = value.metrics();
        const top = metrics.headerActionTop();
        try std.testing.expect(top >= 0);
        try std.testing.expect(top + metrics.header_action - 1 <= metrics.pane_header_height - 2);
        try std.testing.expect(metrics.header_icon <= metrics.header_action);
        // The caption line is roughly 1.4x its size; it must fit between the inset and the rule.
        try std.testing.expect(metrics.header_title_size * 1.4 <= metrics.pane_header_height - 2);
    }
}
