const std = @import("std");

/// Built-in terminal color scheme and ANSI 16-color palette with bespoke UI accent.
pub const TerminalTheme = struct {
    name: []const u8,
    subtitle: []const u8,
    is_dark: bool,
    background: u32,
    foreground: u32,
    cursor: u32,
    selection_background: u32,
    accent: u32,
    ansi: [16]u32,

    /// Derives the window chrome palette from this terminal theme.
    pub fn chromePalette(self: TerminalTheme) ChromePalette {
        return ChromePalette.fromTheme(self);
    }
};

/// Derived window chrome colors for top bar, tabs, pane headers, borders, and overlays.
pub const ChromePalette = struct {
    window_bg: u32,
    titlebar_bg: u32,
    border: u32,
    pane_bg: u32,
    pane_header_bg: u32,
    active_tab_bg: u32,
    active_tab_border: u32,
    inactive_tab_hover_bg: u32,
    text_primary: u32,
    text_muted: u32,
    accent: u32,
    is_dark: bool,

    pub fn fromTheme(theme: TerminalTheme) ChromePalette {
        const bg = theme.background;
        const fg = theme.foreground;
        const accent = theme.accent;
        if (theme.is_dark) {
            const win_bg = darken(bg, 0.36);
            return .{
                .window_bg = win_bg,
                .titlebar_bg = mix(win_bg, bg, 0.65),
                .border = mix(bg, fg, 0.15),
                .pane_bg = bg,
                .pane_header_bg = darken(bg, 0.18),
                .active_tab_bg = mix(bg, fg, 0.085),
                .active_tab_border = mix(bg, accent, 0.55),
                .inactive_tab_hover_bg = mix(bg, fg, 0.055),
                .text_primary = fg,
                .text_muted = mix(fg, bg, 0.44),
                .accent = accent,
                .is_dark = true,
            };
        } else {
            const win_bg = mix(bg, fg, 0.065);
            return .{
                .window_bg = win_bg,
                .titlebar_bg = mix(bg, fg, 0.035),
                .border = mix(bg, fg, 0.15),
                .pane_bg = bg,
                .pane_header_bg = mix(bg, fg, 0.04),
                .active_tab_bg = mix(bg, 0xFFFFFF, 0.65),
                .active_tab_border = mix(bg, accent, 0.48),
                .inactive_tab_hover_bg = mix(bg, fg, 0.075),
                .text_primary = darken(fg, 0.08),
                .text_muted = mix(fg, bg, 0.40),
                .accent = accent,
                .is_dark = false,
            };
        }
    }
};

pub fn mix(a: u32, b: u32, t: f32) u32 {
    const clamped = std.math.clamp(t, 0.0, 1.0);
    const ar = @as(f32, @floatFromInt((a >> 16) & 0xFF));
    const ag = @as(f32, @floatFromInt((a >> 8) & 0xFF));
    const ab = @as(f32, @floatFromInt(a & 0xFF));
    const br = @as(f32, @floatFromInt((b >> 16) & 0xFF));
    const bg = @as(f32, @floatFromInt((b >> 8) & 0xFF));
    const bb = @as(f32, @floatFromInt(b & 0xFF));
    const r: u32 = @intFromFloat(@round(std.math.clamp(ar + (br - ar) * clamped, 0.0, 255.0)));
    const g: u32 = @intFromFloat(@round(std.math.clamp(ag + (bg - ag) * clamped, 0.0, 255.0)));
    const b_out: u32 = @intFromFloat(@round(std.math.clamp(ab + (bb - ab) * clamped, 0.0, 255.0)));
    return (r << 16) | (g << 8) | b_out;
}

pub fn darken(rgb: u32, amount: f32) u32 {
    return mix(rgb, 0x000000, amount);
}

pub const default_dark_index: usize = 0;
pub const default_light_index: usize = 6;

pub const themes = [_]TerminalTheme{
    .{
        .name = "Vex Obsidian",
        .subtitle = "Carbon / violet",
        .is_dark = true,
        .background = 0x0D0F14,
        .foreground = 0xE6E9F0,
        .cursor = 0x8B7CFF,
        .selection_background = 0x232838,
        .accent = 0x8B7CFF,
        .ansi = .{
            0x151821, 0xFF6B81, 0x5CE0A0, 0xFFD166, 0x70A1FF, 0xB388FF, 0x48DBFB, 0xD8DEE9,
            0x4C5468, 0xFF8797, 0x7BF0B8, 0xFFE08A, 0x91B8FF, 0xCBB2FF, 0x7CE8FF, 0xFFFFFF,
        },
    },
    .{
        .name = "Vex Tidal",
        .subtitle = "Midnight / sea glass",
        .is_dark = true,
        .background = 0x101D28,
        .foreground = 0xD9EBEF,
        .cursor = 0x72D4CE,
        .selection_background = 0x244552,
        .accent = 0x72D4CE,
        .ansi = .{
            0x162B38, 0xEF8891, 0x83CBA9, 0xE6C585, 0x82B9E8, 0xBAA6DC, 0x72D4CE, 0xD9EBEF,
            0x617C8A, 0xFFA3AA, 0xA3E2BF, 0xF5DAA2, 0xA3D0F3, 0xD1BEEE, 0x9BEAE3, 0xF2FAFC,
        },
    },
    .{
        .name = "Vex Grove",
        .subtitle = "Forest / jade",
        .is_dark = true,
        .background = 0x14211E,
        .foreground = 0xDFE9DD,
        .cursor = 0xA6D59B,
        .selection_background = 0x304B3C,
        .accent = 0xA6D59B,
        .ansi = .{
            0x1C3028, 0xE78C84, 0xA6D59B, 0xDECB8B, 0x8CBACB, 0xC2A4CB, 0x79C6B6, 0xDFE9DD,
            0x6B8374, 0xF7A49D, 0xC0E9B5, 0xF1DEA6, 0xA8D2E0, 0xDABEE0, 0x9CE0CF, 0xF3F8EF,
        },
    },
    .{
        .name = "Vex Dusk",
        .subtitle = "Plum / orchid",
        .is_dark = true,
        .background = 0x241C29,
        .foreground = 0xEEE1EC,
        .cursor = 0xD5A4CB,
        .selection_background = 0x4B354F,
        .accent = 0xD5A4CB,
        .ansi = .{
            0x34273B, 0xEE91A5, 0xADCCA0, 0xE6C59B, 0xA4B6E7, 0xD5A4CB, 0x99CFCB, 0xEEE1EC,
            0x8D738F, 0xFFABBB, 0xC6E3B9, 0xF7DDB6, 0xBDCEF7, 0xEDBFE1, 0xB7E5DF, 0xFFF5FC,
        },
    },
    .{
        .name = "Vex Ember",
        .subtitle = "Copper / graphite",
        .is_dark = true,
        .background = 0x261C19,
        .foreground = 0xF0E0D5,
        .cursor = 0xE8AB83,
        .selection_background = 0x50382E,
        .accent = 0xE8AB83,
        .ansi = .{
            0x382820, 0xE58E82, 0xB6CA98, 0xE4C181, 0x98BBCE, 0xC8A0BC, 0x8EC6B6, 0xF0E0D5,
            0x94796A, 0xF6A79A, 0xCEE0AF, 0xF5DA9F, 0xB4D3E2, 0xE0BBD2, 0xABE0CF, 0xFFF4EA,
        },
    },
    .{
        .name = "Vex Glacier",
        .subtitle = "Slate / silver blue",
        .is_dark = true,
        .background = 0x1A202B,
        .foreground = 0xE0E8F3,
        .cursor = 0xA8C6EF,
        .selection_background = 0x34445E,
        .accent = 0xA8C6EF,
        .ansi = .{
            0x253144, 0xE99BA6, 0xA4CFB4, 0xE0C89D, 0xA8C6EF, 0xBDADE2, 0x91CDDA, 0xE0E8F3,
            0x75869F, 0xFFB4BC, 0xBCE5CD, 0xF4DFB8, 0xC3DBFF, 0xD7C7F6, 0xAFE3ED, 0xF5F9FF,
        },
    },
    .{
        .name = "Vex Alabaster",
        .subtitle = "Paper / cobalt",
        .is_dark = false,
        .background = 0xFAF8F5,
        .foreground = 0x1E2024,
        .cursor = 0x3B5BDB,
        .selection_background = 0xDCE4F5,
        .accent = 0x3B5BDB,
        .ansi = .{
            0x1E2024, 0xC92A2A, 0x2B8A3E, 0xB45309, 0x3B5BDB, 0x862E9C, 0x0B7285, 0x6B7280,
            0x868E96, 0xE03131, 0x2F9E44, 0xD97706, 0x4C6EF5, 0x9C36B5, 0x1098AD, 0x111315,
        },
    },
    .{
        .name = "Vex Linen",
        .subtitle = "Linen / ochre",
        .is_dark = false,
        .background = 0xFFFCF0,
        .foreground = 0x100F0F,
        .cursor = 0x94632D,
        .selection_background = 0xE6DDC9,
        .accent = 0x94632D,
        .ansi = .{
            0x302A24, 0xAE493C, 0x527334, 0x94632D, 0x386B99, 0x805B92, 0x34796F, 0x6F695F,
            0x8C8375, 0xBB5345, 0x5B7C3A, 0x9C6B32, 0x4475A5, 0x8B659C, 0x3D8378, 0x29231E,
        },
    },
    .{
        .name = "Vex Mist",
        .subtitle = "Mist / washed blue",
        .is_dark = false,
        .background = 0xEDF3F8,
        .foreground = 0x343B58,
        .cursor = 0x2E7DE9,
        .selection_background = 0xC8D0E8,
        .accent = 0x2E7DE9,
        .ansi = .{
            0x2D4056, 0xB64A59, 0x43744E, 0x8E6826, 0x386DB4, 0x7D599E, 0x287B8B, 0x60758B,
            0x798A9D, 0xC35563, 0x4C7E58, 0x98712C, 0x4278C1, 0x8863AA, 0x328695, 0x243348,
        },
    },
    .{
        .name = "Vex Petal",
        .subtitle = "Petal / mulberry",
        .is_dark = false,
        .background = 0xF8EFF3,
        .foreground = 0x4C4F69,
        .cursor = 0x92466D,
        .selection_background = 0xE6CDDB,
        .accent = 0x92466D,
        .ansi = .{
            0x4F3849, 0xB3415C, 0x477750, 0x91662C, 0x496EA5, 0x92466D, 0x357C80, 0x826B7B,
            0x967F8F, 0xBF4D68, 0x51825B, 0x9C7135, 0x5579B1, 0x9F527A, 0x40878A, 0x402D3B,
        },
    },
    .{
        .name = "Vex Apricot",
        .subtitle = "Peach / terracotta",
        .is_dark = false,
        .background = 0xFCF1E7,
        .foreground = 0x575279,
        .cursor = 0xD7827E,
        .selection_background = 0xE6DDD3,
        .accent = 0xA65A3A,
        .ansi = .{
            0x503D35, 0xB14A44, 0x547745, 0x986528, 0x3D7399, 0x925F87, 0x317E78, 0x887268,
            0xA48C7E, 0xBE554F, 0x5E824F, 0xA16F31, 0x487EA5, 0x9D6A92, 0x3C8983, 0x42312A,
        },
    },
    .{
        .name = "Vex Sage",
        .subtitle = "Sage / eucalyptus",
        .is_dark = false,
        .background = 0xEEF4EC,
        .foreground = 0x304638,
        .cursor = 0x39735B,
        .selection_background = 0xCDDFCE,
        .accent = 0x39735B,
        .ansi = .{
            0x304638, 0xAF4E51, 0x39735B, 0x8B6C2C, 0x426F97, 0x835C8E, 0x337E7A, 0x657C6C,
            0x819887, 0xBB595C, 0x447E66, 0x957637, 0x4D7BA3, 0x8E679A, 0x3E8985, 0x253B2D,
        },
    },
};

/// Collects indices into `themes` matching the requested dark/light appearance.
pub fn indicesForAppearance(is_dark: bool, out: *[themes.len]usize) []const usize {
    var count: usize = 0;
    for (themes, 0..) |theme, idx| {
        if (theme.is_dark == is_dark) {
            out[count] = idx;
            count += 1;
        }
    }
    return out[0..count];
}

test "built-in themes partition into 6 dark and 6 light themes and derive chrome palettes" {
    var dark_buf: [themes.len]usize = undefined;
    var light_buf: [themes.len]usize = undefined;
    const dark = indicesForAppearance(true, &dark_buf);
    const light = indicesForAppearance(false, &light_buf);
    try std.testing.expectEqual(@as(usize, 6), dark.len);
    try std.testing.expectEqual(@as(usize, 6), light.len);
    for (dark, 0..) |index, i| {
        try std.testing.expect(themes[index].is_dark);
        if (i > 0) try std.testing.expect(index > dark[i - 1]);
    }
    for (light, 0..) |index, i| {
        try std.testing.expect(!themes[index].is_dark);
        if (i > 0) try std.testing.expect(index > light[i - 1]);
    }
    try std.testing.expectEqual(themes.len, dark.len + light.len);
    try std.testing.expect(themes[default_dark_index].is_dark);
    try std.testing.expect(!themes[default_light_index].is_dark);
    const vex_dark_chrome = themes[default_dark_index].chromePalette();
    try std.testing.expect(vex_dark_chrome.is_dark);
    try std.testing.expectEqual(@as(u32, 0x0D0F14), vex_dark_chrome.pane_bg);
    const vex_light_chrome = themes[default_light_index].chromePalette();
    try std.testing.expect(!vex_light_chrome.is_dark);
    try std.testing.expectEqual(@as(u32, 0xFAF8F5), vex_light_chrome.pane_bg);
}
