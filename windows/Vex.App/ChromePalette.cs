using System.Windows;
using System.Windows.Media;
using Vex.App.Model;

namespace Vex.App;

/// <summary>
/// Derives the app's chrome palette (window, sidebar, tab strip, pane chrome,
/// accents, overlay surfaces) from the active terminal theme, so switching a
/// theme tints the whole application like upstream — not just the ANSI grid.
/// Every color is computed in one of two variants, dark or light, chosen by
/// the theme's own background luminance, so a light terminal theme yields a
/// fully light chrome with readable contrast everywhere (no light text on
/// light backgrounds). The brush and gradient resources in App.xaml bind
/// their colors to these keys through DynamicResource; <see cref="Apply"/>
/// additionally pushes each value straight into its brush, because that
/// indirection silently stops propagating on live appearance flips.
/// </summary>
public static class ChromePalette
{
    public static void Apply(string? themeName)
    {
        Apply(BuiltInThemes.Resolve(themeName));
    }

    public static void Apply(TerminalTheme theme)
    {
        var res = Application.Current.Resources;
        var bg = Parse(theme.Background);
        var fg = Parse(theme.Foreground);
        var blue = Parse(theme.Blue);
        var magenta = Parse(theme.Magenta);

        var accent = Mix(Mix(blue, magenta, 0.5), bg, 0.35);

        if (theme.IsDark)
            ApplyDark(res, bg, fg, blue, magenta);
        else
            ApplyLight(res, bg, fg, blue, magenta);

        // Variant-independent accents come straight from the terminal theme.
        Set(res, "VexAccentBlueColor", blue);
        Set(res, "VexAccentPurpleColor", magenta);

        SyncBrushes(res);
    }

    /// <summary>Brushes in App.xaml bind their Color to a Color key through
    /// DynamicResource. That indirection stops propagating on live appearance
    /// flips: the color key updates while the brush keeps painting the previous
    /// variant (observed on VexSidebar/VexTabStrip), leaving half the chrome in
    /// the old theme until a restart. Push values into the brushes directly —
    /// an unfrozen shared brush invalidates all consumers on write.</summary>
    private static void SyncBrushes(ResourceDictionary res)
    {
        foreach (var (brushKey, colorKey) in BrushColorPairs)
        {
            if (res[brushKey] is SolidColorBrush solid && !solid.IsFrozen
                && res[colorKey] is Color c && solid.Color != c)
                solid.Color = c;
        }

        SyncGradient(res, "VexBackground", "VexBackgroundTopColor", "VexBackgroundBottomColor");
        SyncGradient(res, "VexTabSelected", "VexTabSelectedStartColor", "VexTabSelectedEndColor");
        SyncGradient(res, "VexAccentGradient", "VexAccentGradientStartColor", "VexAccentGradientEndColor");
    }

    private static void SyncGradient(ResourceDictionary res, string brushKey,
        string startColorKey, string endColorKey)
    {
        if (res[brushKey] is not LinearGradientBrush gradient || gradient.IsFrozen
            || res[startColorKey] is not Color start || res[endColorKey] is not Color end)
            return;
        var stops = gradient.GradientStops;
        if (stops.Count > 0 && stops[0].Color != start) stops[0].Color = start;
        if (stops.Count > 1 && stops[^1].Color != end) stops[^1].Color = end;
    }

    private static readonly (string Brush, string Color)[] BrushColorPairs =
    {
        ("VexSurface", "VexSurfaceColor"),
        ("VexHover", "VexHoverColor"),
        ("VexBorder", "VexBorderColor"),
        ("VexText", "VexTextColor"),
        ("VexTextDim", "VexTextDimColor"),
        ("VexAccent", "VexAccentColor"),
        ("VexFocusBorder", "VexFocusBorderColor"),
        ("VexSidebar", "VexSidebarColor"),
        ("VexPaneTitleBar", "VexPaneTitleBarColor"),
        ("VexPaneTitleBarFocused", "VexPaneTitleBarFocusedColor"),
        ("VexTabStrip", "VexTabStripColor"),
        ("VexTabHover", "VexTabHoverColor"),
        ("VexBackdropScrim", "VexBackdropScrimColor"),
        ("VexPressFill", "VexPressFillColor"),
        ("VexSidebarToggleBg", "VexSidebarToggleBgColor"),
        ("VexSidebarToggleBorder", "VexSidebarToggleBorderColor"),
        ("VexSplitPreview", "VexSplitPreviewColor"),
        ("VexSplitPreviewBorder", "VexSplitPreviewBorderColor"),
        ("VexPaneExited", "VexPaneExitedColor"),
        ("VexCard", "VexCardColor"),
        ("VexCardBorder", "VexCardBorderColor"),
        ("VexTrack", "VexTrackColor"),
        ("VexInput", "VexInputColor"),
        ("VexToggleThumb", "VexToggleThumbColor"),
        ("VexOnAccent", "VexOnAccentColor"),
        ("VexAccentBlue", "VexAccentBlueColor"),
        ("VexAccentPurple", "VexAccentPurpleColor"),
    };

    // ---- Dark variant ------------------------------------------------------

    private static void ApplyDark(ResourceDictionary res, Color bg, Color fg, Color blue, Color magenta)
    {
        Set(res, "VexBackgroundTopColor", Darken(bg, 0.62));
        Set(res, "VexBackgroundBottomColor", Darken(bg, 0.45));
        Set(res, "VexSurfaceColor", WithAlpha(bg, 0xE6));
        Set(res, "VexHoverColor", Lighten(bg, 0.12));
        // Borders read clearly against the tinted chrome and shift with the
        // theme instead of disappearing into it.
        Set(res, "VexBorderColor", Mix(bg, fg, 0.30));
        Set(res, "VexTextColor", fg);
        Set(res, "VexTextDimColor", Mix(fg, bg, 0.45));
        Set(res, "VexAccentColor", Mix(Mix(blue, magenta, 0.5), bg, 0.35));
        Set(res, "VexFocusBorderColor", Mix(bg, blue, 0.50));
        Set(res, "VexSidebarColor", WithAlpha(bg, 0x30));
        Set(res, "VexTabStripColor", WithAlpha(bg, 0x40));
        // The pane title bar carries the terminal's own background, so each
        // pane reads as one tinted block; focus lifts it slightly and the
        // accent border marks the active pane.
        Set(res, "VexPaneTitleBarColor", bg);
        Set(res, "VexPaneTitleBarFocusedColor", Lighten(bg, 0.10));
        Set(res, "VexTabHoverColor", WithAlpha(fg, 0x12));
        Set(res, "VexTabSelectedStartColor", WithAlpha(blue, 0x30));
        Set(res, "VexTabSelectedEndColor", WithAlpha(magenta, 0x30));
        Set(res, "VexAccentGradientStartColor", WithAlpha(blue, 0x66));
        Set(res, "VexAccentGradientEndColor", WithAlpha(magenta, 0x66));

        // Overlay chrome: scrims, pressed states, drag previews, settings
        // surfaces. Derived from the theme so overlays never clash with it.
        Set(res, "VexBackdropScrimColor", FromArgb(0x66, 0x00, 0x00, 0x00));
        Set(res, "VexPressFillColor", Mix(bg, fg, 0.32));
        Set(res, "VexSidebarToggleBgColor", WithAlpha(Darken(bg, 0.70), 0x2E));
        Set(res, "VexSidebarToggleBorderColor", WithAlpha(Mix(bg, fg, 0.35), 0x46));
        Set(res, "VexSplitPreviewColor", WithAlpha(fg, 0x26));
        Set(res, "VexSplitPreviewBorderColor", WithAlpha(fg, 0x55));
        Set(res, "VexPaneExitedColor", FromArgb(0xFF, 0x6B, 0x4B, 0x4B));

        var panelBase = Darken(bg, 0.62);
        Set(res, "VexSettingsPanelColor", WithAlpha(panelBase, 0xF2));
        Set(res, "VexSettingsSidebarColor", WithAlpha(Darken(bg, 0.70), 0xF0));
        Set(res, "VexCardColor", Mix(panelBase, bg, 0.45));
        Set(res, "VexCardBorderColor", Mix(bg, fg, 0.20));
        Set(res, "VexTrackColor", Mix(bg, fg, 0.35));
        Set(res, "VexNavHoverColor", Mix(panelBase, fg, 0.08));
        Set(res, "VexNavSelectedColor", Mix(bg, fg, 0.18));
        Set(res, "VexToggleThumbColor", FromHex("#EDEFF3"));
        Set(res, "VexOnAccentColor", FromHex("#EDEFF2"));
    }

    // ---- Light variant -----------------------------------------------------
    //
    // The dark chrome leans on the DWM blur behind translucent surfaces; the
    // light chrome deliberately does not. Every surface here is fully opaque,
    // so the look never depends on backdrop recomposition and bright text can
    // never sit on a bright seam. Hovers, borders and pressed states go
    // DARKER than their surface (the inverse of the dark variant), and text
    // is darkened below the theme foreground for comfortable reading on
    // white.

    private static void ApplyLight(ResourceDictionary res, Color bg, Color fg, Color blue, Color magenta)
    {
        var ink = Darken(fg, 0.20);
        var dim = Mix(fg, bg, 0.25);

        Set(res, "VexBackgroundTopColor", Lighten(bg, 0.35));
        Set(res, "VexBackgroundBottomColor", Darken(bg, 0.035));
        Set(res, "VexSurfaceColor", FromHex("#FFFFFF"));
        Set(res, "VexHoverColor", Darken(bg, 0.05));
        Set(res, "VexBorderColor", Mix(bg, fg, 0.18));
        Set(res, "VexTextColor", ink);
        Set(res, "VexTextDimColor", dim);
        Set(res, "VexAccentColor", Mix(Mix(blue, magenta, 0.5), bg, 0.20));
        Set(res, "VexFocusBorderColor", Mix(blue, bg, 0.15));
        // Sidebar sits a touch darker than the window behind it and the tab
        // strip matches the window top — both opaque.
        Set(res, "VexSidebarColor", Mix(bg, fg, 0.045));
        Set(res, "VexTabStripColor", Lighten(bg, 0.35));
        Set(res, "VexPaneTitleBarColor", bg);
        Set(res, "VexPaneTitleBarFocusedColor", Darken(bg, 0.03));
        Set(res, "VexTabHoverColor", WithAlpha(fg, 0x08));
        Set(res, "VexTabSelectedStartColor", WithAlpha(blue, 0x22));
        Set(res, "VexTabSelectedEndColor", WithAlpha(magenta, 0x22));
        Set(res, "VexAccentGradientStartColor", WithAlpha(blue, 0x50));
        Set(res, "VexAccentGradientEndColor", WithAlpha(magenta, 0x50));

        // Overlay chrome: scrims, pressed states, drag previews, settings
        // surfaces. All opaque or near-opaque so overlays read crisply.
        Set(res, "VexBackdropScrimColor", FromArgb(0x28, 0x14, 0x16, 0x1A));
        Set(res, "VexPressFillColor", Darken(bg, 0.09));
        Set(res, "VexSidebarToggleBgColor", WithAlpha(fg, 0x06));
        Set(res, "VexSidebarToggleBorderColor", WithAlpha(fg, 0x1E));
        Set(res, "VexSplitPreviewColor", WithAlpha(blue, 0x1A));
        Set(res, "VexSplitPreviewBorderColor", WithAlpha(blue, 0x55));
        Set(res, "VexPaneExitedColor", FromHex("#D93025"));

        Set(res, "VexSettingsPanelColor", FromHex("#FFFFFF"));
        Set(res, "VexSettingsSidebarColor", FromHex("#F3F4F6"));
        Set(res, "VexCardColor", FromHex("#FFFFFF"));
        Set(res, "VexCardBorderColor", Mix(bg, fg, 0.14));
        Set(res, "VexTrackColor", Darken(bg, 0.08));
        Set(res, "VexNavHoverColor", Darken(bg, 0.04));
        Set(res, "VexNavSelectedColor", Darken(bg, 0.075));
        Set(res, "VexInputColor", FromHex("#F5F6F8"));
        Set(res, "VexToggleThumbColor", Colors.White);
        Set(res, "VexOnAccentColor", FromHex("#1B1E24"));

        // Unused on the opaque light chrome; kept consistent anyway.
        Set(res, "VexAccentBlueColor", blue);
        Set(res, "VexAccentPurpleColor", magenta);
    }

    /// <summary>Tint color for the acrylic window backdrop; pushed dark so the
    /// chrome stays readable behind the blur. The light appearance does not
    /// use the backdrop at all (opaque chrome), so only dark reaches this.</summary>
    public static Color BackdropTint(string? themeName)
    {
        var theme = BuiltInThemes.Resolve(themeName);
        var bg = Parse(theme.Background);
        return theme.IsDark ? Darken(bg, 0.62) : Lighten(bg, 0.20);
    }

    private static void Set(ResourceDictionary res, string key, Color color)
        => res[key] = color;

    private static Color Parse(string hex)
        => FastColor.ParseHex(hex);

    private static Color FromHex(string hex) => FastColor.ParseHex(hex);

    private static Color FromArgb(byte a, byte r, byte g, byte b)
        => Color.FromArgb(a, r, g, b);

    private static Color WithAlpha(Color c, byte a)
        => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);
    private static Color Darken(Color c, double t) => Mix(c, Colors.Black, t);
}
