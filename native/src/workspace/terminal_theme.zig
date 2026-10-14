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

    /// Relative luminance check (< 0.5 is dark).
    pub fn isDark(self: TerminalTheme) bool {
        return self.is_dark;
    }

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
    git_branch: u32,
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
                .git_branch = theme.ansi[6],
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
                .git_branch = theme.ansi[6],
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

pub fn lighten(rgb: u32, amount: f32) u32 {
    return mix(rgb, 0xFFFFFF, amount);
}

pub const default_dark_index: usize = 0;
pub const default_light_index: usize = 6;

pub const themes = [_]TerminalTheme{
    .{
        .name = "Vex Obsidian",
        .subtitle = "Deep carbon & electric violet",
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
        .name = "Tokyo Night Storm",
        .subtitle = "Shibuya indigo & storm blue",
        .is_dark = true,
        .background = 0x1A1B26,
        .foreground = 0xC0CAF5,
        .cursor = 0x7AA2F7,
        .selection_background = 0x283457,
        .accent = 0x7AA2F7,
        .ansi = .{
            0x15161E, 0xF7768E, 0x9ECE6A, 0xE0AF68, 0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xA9B1D6,
            0x414868, 0xFF899D, 0xB9F27C, 0xFF9E64, 0x8DB0FF, 0xC7A9FF, 0xA4DAFF, 0xC0CAF5,
        },
    },
    .{
        .name = "Kanagawa Wave",
        .subtitle = "Sumi ink & wave aqua",
        .is_dark = true,
        .background = 0x1F1F28,
        .foreground = 0xDCD7BA,
        .cursor = 0xE6C384,
        .selection_background = 0x2D4F67,
        .accent = 0x7E9CD8,
        .ansi = .{
            0x16161D, 0xC34043, 0x76946A, 0xC0A36E, 0x7E9CD8, 0x957FB8, 0x6A9589, 0xC8C093,
            0x727169, 0xE82424, 0x98BB6C, 0xE6C384, 0x7FB4CA, 0x938AA9, 0x7AA89F, 0xDCD7BA,
        },
    },
    .{
        .name = "Catppuccin Mocha",
        .subtitle = "Velvet espresso & pastel mauve",
        .is_dark = true,
        .background = 0x1E1E2E,
        .foreground = 0xCDD6F4,
        .cursor = 0xF5E0DC,
        .selection_background = 0x38394E,
        .accent = 0xCBA6F7,
        .ansi = .{
            0x45475A, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xCBA6F7, 0x94E2D5, 0xBAC2DE,
            0x585B70, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xFFFFFF,
        },
    },
    .{
        .name = "Rosé Pine",
        .subtitle = "Soho pine, muted rose & iris",
        .is_dark = true,
        .background = 0x191724,
        .foreground = 0xE0DEF4,
        .cursor = 0xEBBCBA,
        .selection_background = 0x312E43,
        .accent = 0xC4A7E7,
        .ansi = .{
            0x26233A, 0xEB6F92, 0x31748F, 0xF6C177, 0x9CCFD8, 0xC4A7E7, 0xEBBCBA, 0xE0DEF4,
            0x6E6A86, 0xEB6F92, 0x3E8FB0, 0xF6C177, 0x9CCFD8, 0xC4A7E7, 0xEBBCBA, 0xFFFFFF,
        },
    },
    .{
        .name = "Gruvbox Material",
        .subtitle = "Warm retro charcoal & amber",
        .is_dark = true,
        .background = 0x1D2021,
        .foreground = 0xD4BE98,
        .cursor = 0xE78A4E,
        .selection_background = 0x3C3836,
        .accent = 0xE78A4E,
        .ansi = .{
            0x282828, 0xEA6962, 0xA9B665, 0xD8A657, 0x7DAEA3, 0xD3869B, 0x89B482, 0xD4BE98,
            0x5A524C, 0xEA6962, 0xA9B665, 0xE78A4E, 0x7DAEA3, 0xD3869B, 0x89B482, 0xEBDBB2,
        },
    },
    .{
        .name = "Vex Alabaster",
        .subtitle = "Warm gallery paper & cobalt ink",
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
        .name = "Flexoki Paper",
        .subtitle = "Fountain pen ink on warm cream",
        .is_dark = false,
        .background = 0xFFFCF0,
        .foreground = 0x100F0F,
        .cursor = 0x205EA6,
        .selection_background = 0xE6E4D9,
        .accent = 0x205EA6,
        .ansi = .{
            0x100F0F, 0xAF3029, 0x66800B, 0xAD8301, 0x205EA6, 0x5E409D, 0x24837B, 0x6F6E69,
            0x878580, 0xD14D41, 0x879A39, 0xD0A215, 0x4385BE, 0x8B7EC8, 0x3AA99F, 0x100F0F,
        },
    },
    .{
        .name = "Tokyo Night Day",
        .subtitle = "Cool daylight slate & azure",
        .is_dark = false,
        .background = 0xE6E7ED,
        .foreground = 0x343B58,
        .cursor = 0x2E7DE9,
        .selection_background = 0xC8D0E8,
        .accent = 0x2E7DE9,
        .ansi = .{
            0x343B58, 0xC64343, 0x485E30, 0x8F5E15, 0x2E7DE9, 0x7847BD, 0x007197, 0x6172B0,
            0x68709A, 0xF52A65, 0x587539, 0x9E6B1A, 0x3888F5, 0x9854F1, 0x0F87AF, 0x1A1B26,
        },
    },
    .{
        .name = "Catppuccin Latte",
        .subtitle = "Sunlit latte & berry mauve",
        .is_dark = false,
        .background = 0xEFF1F5,
        .foreground = 0x4C4F69,
        .cursor = 0x8839EF,
        .selection_background = 0xCCD0DA,
        .accent = 0x8839EF,
        .ansi = .{
            0x5C5F77, 0xD20F39, 0x40A02B, 0xDF8E1D, 0x1E66F5, 0x8839EF, 0x179299, 0x6C6F85,
            0x7C7F93, 0xE64553, 0x40A02B, 0xFE640B, 0x209FB5, 0xEA76CB, 0x04A5E5, 0x4C4F69,
        },
    },
    .{
        .name = "Rosé Pine Dawn",
        .subtitle = "Morning parchment, rose & pine",
        .is_dark = false,
        .background = 0xFAF4ED,
        .foreground = 0x575279,
        .cursor = 0xD7827E,
        .selection_background = 0xE6DDD3,
        .accent = 0x907AA9,
        .ansi = .{
            0x575279, 0xB4637A, 0x286983, 0xEA9D34, 0x56949F, 0x907AA9, 0xD7827E, 0x797593,
            0x9893A5, 0xB4637A, 0x286983, 0xEA9D34, 0x56949F, 0x907AA9, 0xD7827E, 0x575279,
        },
    },
    .{
        .name = "Kanagawa Lotus",
        .subtitle = "Washi paper, indigo & vermilion",
        .is_dark = false,
        .background = 0xF2ECBC,
        .foreground = 0x43436C,
        .cursor = 0xC84053,
        .selection_background = 0xD7D1A3,
        .accent = 0x4D699B,
        .ansi = .{
            0x43436C, 0xC84053, 0x6F894E, 0x77713F, 0x4D699B, 0x624C83, 0x597B75, 0x716E61,
            0x8A8980, 0xD7474B, 0x6E915F, 0x836F4A, 0x6693BF, 0x766B90, 0x5E857A, 0x2E2E4E,
        },
    },
};

pub const built_in = themes;

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
    try std.testing.expect(themes[default_dark_index].isDark());
    try std.testing.expect(!themes[default_light_index].isDark());
    const vex_dark_chrome = themes[default_dark_index].chromePalette();
    try std.testing.expect(vex_dark_chrome.is_dark);
    try std.testing.expectEqual(@as(u32, 0x0D0F14), vex_dark_chrome.pane_bg);
    const vex_light_chrome = themes[default_light_index].chromePalette();
    try std.testing.expect(!vex_light_chrome.is_dark);
    try std.testing.expectEqual(@as(u32, 0xFAF8F5), vex_light_chrome.pane_bg);
}
