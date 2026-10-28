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
            // Keep the structural layer close to the terminal; crushed black
            // gutters make tinted themes feel like unrelated floating panels.
            const win_bg = darken(mix(bg, accent, 0.025), 0.18);
            return .{
                .window_bg = win_bg,
                .titlebar_bg = mix(bg, accent, 0.035),
                .border = mix(bg, fg, 0.18),
                .pane_bg = bg,
                .pane_header_bg = mix(bg, accent, 0.045),
                .active_tab_bg = mix(mix(bg, accent, 0.065), fg, 0.055),
                .active_tab_border = mix(bg, accent, 0.62),
                .inactive_tab_hover_bg = mix(bg, fg, 0.055),
                .text_primary = fg,
                .text_muted = mix(fg, bg, 0.28),
                .accent = accent,
                .is_dark = true,
            };
        } else {
            const win_bg = mix(mix(bg, accent, 0.025), fg, 0.045);
            return .{
                .window_bg = win_bg,
                .titlebar_bg = mix(bg, accent, 0.035),
                .border = mix(bg, fg, 0.18),
                .pane_bg = bg,
                .pane_header_bg = mix(bg, accent, 0.045),
                .active_tab_bg = mix(bg, 0xFFFFFF, 0.52),
                .active_tab_border = mix(bg, accent, 0.62),
                .inactive_tab_hover_bg = mix(bg, accent, 0.085),
                .text_primary = darken(fg, 0.08),
                .text_muted = mix(fg, bg, 0.20),
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
        .subtitle = "Smoked quartz / iris",
        .is_dark = true,
        .background = 0x17181F,
        .foreground = 0xE3E1EB,
        .cursor = 0xB1A4E3,
        .selection_background = 0x343044,
        .accent = 0xB1A4E3,
        .ansi = .{
            0x22232C, 0xE695A5, 0x9FC7A4, 0xDDC18D, 0x98B5DB, 0xB9A4DB, 0x8EC9CC, 0xD9D7E2,
            0x858294, 0xF0ADBA, 0xB4D9B7, 0xEDD4A6, 0xB0C9EA, 0xCEBDEB, 0xA9DDDF, 0xF4F1F8,
        },
    },
    .{
        .name = "Vex Tidal",
        .subtitle = "Deep petrol / sea glass",
        .is_dark = true,
        .background = 0x142126,
        .foreground = 0xDCE9E7,
        .cursor = 0x8ACCC4,
        .selection_background = 0x2B464B,
        .accent = 0x8ACCC4,
        .ansi = .{
            0x203037, 0xDC9298, 0x96C7AF, 0xD8C291, 0x91B8D3, 0xBBA9D3, 0x8ACCC4, 0xDCE9E7,
            0x78928F, 0xEEAAB0, 0xAFDDC5, 0xEBD6AA, 0xABCFE5, 0xD1BFE5, 0xA5E0D8, 0xF0F8F5,
        },
    },
    .{
        .name = "Vex Grove",
        .subtitle = "Evergreen / soft jade",
        .is_dark = true,
        .background = 0x19221F,
        .foreground = 0xDFE7DE,
        .cursor = 0xA8C9A2,
        .selection_background = 0x34483B,
        .accent = 0xA8C9A2,
        .ansi = .{
            0x26342D, 0xDB9689, 0xA8C9A2, 0xD3C18F, 0x95B8C8, 0xC1A6C6, 0x8AC6B5, 0xDFE7DE,
            0x80917F, 0xECAE9F, 0xBEDDB7, 0xE7D6A7, 0xAFCEDB, 0xD6BED9, 0xA4DACA, 0xF1F6EC,
        },
    },
    .{
        .name = "Vex Dusk",
        .subtitle = "Aubergine / rose quartz",
        .is_dark = true,
        .background = 0x251F2B,
        .foreground = 0xEAE0EA,
        .cursor = 0xCBA8CE,
        .selection_background = 0x49384F,
        .accent = 0xCBA8CE,
        .ansi = .{
            0x362B3D, 0xE39BB0, 0xB1C6A5, 0xDBC099, 0xA7B2D9, 0xCBA8CE, 0x9DC8C8, 0xEAE0EA,
            0x99859D, 0xF1B3C3, 0xC8DBBD, 0xEED6B2, 0xBEC9EC, 0xE0C0E2, 0xB5DEDB, 0xF9EFF8,
        },
    },
    .{
        .name = "Vex Ember",
        .subtitle = "Warm charcoal / burnished copper",
        .is_dark = true,
        .background = 0x28211E,
        .foreground = 0xEBDDD2,
        .cursor = 0xD9AD8D,
        .selection_background = 0x4E3C31,
        .accent = 0xD9AD8D,
        .ansi = .{
            0x392E27, 0xDF9988, 0xB7C29B, 0xD9BA87, 0x9DB8C8, 0xC5A6BE, 0x95C2B2, 0xEBDDD2,
            0x9B8776, 0xEFB1A1, 0xCDD7B4, 0xEBD0A0, 0xB7CFDC, 0xDCC0D4, 0xADD8C9, 0xF9EFE4,
        },
    },
    .{
        .name = "Vex Glacier",
        .subtitle = "Blue graphite / glacial silver",
        .is_dark = true,
        .background = 0x1D242E,
        .foreground = 0xDEE6EF,
        .cursor = 0xAAC2DE,
        .selection_background = 0x35465B,
        .accent = 0xAAC2DE,
        .ansi = .{
            0x2A3543, 0xDC9DA9, 0xA6C6B4, 0xD4C39F, 0xAAC2DE, 0xB7AED1, 0x96C5D1, 0xDEE6EF,
            0x8595AA, 0xEDB4BE, 0xBDDCCB, 0xE8D8B7, 0xC1D7EE, 0xCDC4E5, 0xAFDBE5, 0xF1F6FC,
        },
    },
    .{
        .name = "Vex Alabaster",
        .subtitle = "Warm porcelain / ink blue",
        .is_dark = false,
        .background = 0xF4F2EE,
        .foreground = 0x30343B,
        .cursor = 0x526B95,
        .selection_background = 0xD9E1ED,
        .accent = 0x526B95,
        .ansi = .{
            0x30343B, 0x973C47, 0x3A6748, 0x795A22, 0x4B6289, 0x764D79, 0x25636E, 0x626974,
            0x7A7F88, 0xA34651, 0x427151, 0x82632A, 0x556D95, 0x825784, 0x2D6D79, 0x242A32,
        },
    },
    .{
        .name = "Vex Linen",
        .subtitle = "Natural linen / toasted amber",
        .is_dark = false,
        .background = 0xF6F0E5,
        .foreground = 0x3F3932,
        .cursor = 0x886130,
        .selection_background = 0xE4D7C1,
        .accent = 0x886130,
        .ansi = .{
            0x3F3932, 0x974234, 0x506732, 0x7D592C, 0x48627F, 0x725075, 0x366860, 0x706658,
            0x8B8070, 0xA34C3E, 0x587239, 0x856333, 0x526D8B, 0x7D5B80, 0x3E7269, 0x302A24,
        },
    },
    .{
        .name = "Vex Mist",
        .subtitle = "Pearl grey / coastal blue",
        .is_dark = false,
        .background = 0xEDF2F5,
        .foreground = 0x334250,
        .cursor = 0x3C6F92,
        .selection_background = 0xCFDFE9,
        .accent = 0x3C6F92,
        .ansi = .{
            0x334250, 0x973E4D, 0x386751, 0x745D29, 0x376686, 0x6D5483, 0x286876, 0x5E7181,
            0x7D8C99, 0xA24858, 0x41715A, 0x7D6630, 0x407090, 0x785E8D, 0x32737E, 0x293744,
        },
    },
    .{
        .name = "Vex Petal",
        .subtitle = "Blush porcelain / wild rose",
        .is_dark = false,
        .background = 0xF5EEF1,
        .foreground = 0x4B3847,
        .cursor = 0x8B4D70,
        .selection_background = 0xE5D2DE,
        .accent = 0x8B4D70,
        .ansi = .{
            0x4B3847, 0x973852, 0x406749, 0x7B5928, 0x4A6288, 0x804767, 0x38656D, 0x7E6476,
            0x94808E, 0xA2435C, 0x4A7152, 0x846330, 0x546C92, 0x8C5173, 0x416F76, 0x3C2C39,
        },
    },
    .{
        .name = "Vex Apricot",
        .subtitle = "Warm clay / terracotta ink",
        .is_dark = false,
        .background = 0xF8EFE6,
        .foreground = 0x4D3B34,
        .cursor = 0x9A5539,
        .selection_background = 0xE9D5C5,
        .accent = 0x9A5539,
        .ansi = .{
            0x4D3B34, 0x953F37, 0x53683A, 0x7E5929, 0x446580, 0x7A516F, 0x336661, 0x7E685B,
            0x998272, 0xA14A41, 0x5C7242, 0x886331, 0x4F708B, 0x855C7A, 0x3D716A, 0x3E2E28,
        },
    },
    .{
        .name = "Vex Sage",
        .subtitle = "Celadon / eucalyptus ink",
        .is_dark = false,
        .background = 0xEFF3EB,
        .foreground = 0x344538,
        .cursor = 0x3E7159,
        .selection_background = 0xD2E0CE,
        .accent = 0x3E7159,
        .ansi = .{
            0x344538, 0x934247, 0x396852, 0x725E28, 0x456481, 0x715579, 0x2E6667, 0x657461,
            0x81907C, 0x9E4C51, 0x42725B, 0x7B6830, 0x4F6F8B, 0x7D5F84, 0x377170, 0x29392C,
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
    try std.testing.expectEqual(@as(u32, 0x17181F), vex_dark_chrome.pane_bg);
    const vex_light_chrome = themes[default_light_index].chromePalette();
    try std.testing.expect(!vex_light_chrome.is_dark);
    try std.testing.expectEqual(@as(u32, 0xF4F2EE), vex_light_chrome.pane_bg);
}

fn luminance(rgb: u32) f64 {
    var result: f64 = 0;
    for ([_]u5{ 16, 8, 0 }, [_]f64{ 0.2126, 0.7152, 0.0722 }) |shift, weight| {
        const value = @as(f64, @floatFromInt((rgb >> shift) & 255)) / 255;
        result += weight * (if (value <= 0.04045) value / 12.92 else std.math.pow(f64, (value + 0.055) / 1.055, 2.4));
    }
    return result;
}

fn contrast(a: u32, b: u32) f64 {
    const x = luminance(a);
    const y = luminance(b);
    return (@max(x, y) + 0.05) / (@min(x, y) + 0.05);
}

test "bespoke materials retain readable text in both appearances" {
    for (themes) |theme| {
        const chrome = theme.chromePalette();
        try std.testing.expect(contrast(theme.foreground, theme.background) >= 7);
        try std.testing.expect(contrast(theme.foreground, theme.selection_background) >= 4.5);
        try std.testing.expect(contrast(theme.cursor, theme.background) >= 3);
        for ([_]usize{ 1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 14 }) |index| {
            try std.testing.expect(contrast(theme.ansi[index], theme.background) >= 4.5);
        }
        const surfaces = [_]u32{ chrome.titlebar_bg, chrome.pane_header_bg, chrome.active_tab_bg, chrome.inactive_tab_hover_bg };
        for (surfaces) |base| {
            // Include the brightest part of the material, not just its base.
            const top = mix(base, 0xffffff, if (theme.is_dark) 0.045 else 0.42);
            for ([_]u32{ base, top }) |surface| {
                try std.testing.expect(contrast(chrome.text_primary, surface) >= 7);
                try std.testing.expect(contrast(chrome.text_muted, surface) >= 4.5);
                try std.testing.expect(contrast(chrome.accent, surface) >= 3);
            }
        }
    }
}
