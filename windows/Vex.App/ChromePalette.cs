using System.Windows;
using System.Windows.Media;
using Vex.App.Model;

namespace Vex.App;

/// <summary>
/// Derives the app's chrome palette (window, sidebar, tab strip, pane chrome,
/// accents) from the active terminal theme, so switching a theme tints the
/// whole application like upstream — not just the ANSI grid. The brush and
/// gradient resources in App.xaml bind their colors to these keys through
/// DynamicResource, so updating a key re-colors the live UI.
/// </summary>
public static class ChromePalette
{
    public static void Apply(string? themeName)
    {
        var theme = BuiltInThemes.All.FirstOrDefault(t => t.Name == themeName) ?? BuiltInThemes.VexDark;
        Apply(theme);
    }

    public static void Apply(TerminalTheme theme)
    {
        var res = Application.Current.Resources;
        var bg = Parse(theme.Background);
        var fg = Parse(theme.Foreground);
        var blue = Parse(theme.Blue);
        var magenta = Parse(theme.Magenta);

        var accent = Mix(Mix(blue, magenta, 0.5), bg, 0.35);

        Set(res, "VexBackgroundTopColor", Darken(bg, 0.62));
        Set(res, "VexBackgroundBottomColor", Darken(bg, 0.45));
        Set(res, "VexSurfaceColor", WithAlpha(bg, 0xE6));
        Set(res, "VexHoverColor", Lighten(bg, 0.12));
        Set(res, "VexBorderColor", Mix(bg, fg, 0.20));
        Set(res, "VexTextColor", fg);
        Set(res, "VexTextDimColor", Mix(fg, bg, 0.45));
        Set(res, "VexAccentColor", accent);
        Set(res, "VexFocusBorderColor", Mix(bg, blue, 0.35));
        Set(res, "VexSidebarColor", WithAlpha(bg, 0x30));
        Set(res, "VexTabStripColor", WithAlpha(bg, 0x40));
        Set(res, "VexPaneTitleBarColor", WithAlpha(fg, 0x14));
        Set(res, "VexPaneTitleBarFocusedColor", WithAlpha(fg, 0x24));
        Set(res, "VexTabHoverColor", WithAlpha(fg, 0x12));
        Set(res, "VexTabSelectedStartColor", WithAlpha(blue, 0x30));
        Set(res, "VexTabSelectedEndColor", WithAlpha(magenta, 0x30));
        Set(res, "VexAccentBlueColor", blue);
        Set(res, "VexAccentPurpleColor", magenta);
        Set(res, "VexAccentGradientStartColor", WithAlpha(blue, 0x66));
        Set(res, "VexAccentGradientEndColor", WithAlpha(magenta, 0x66));
    }

    /// <summary>Tint color for the acrylic window backdrop; the theme's
    /// background pushed dark so the chrome stays readable behind the blur.</summary>
    public static Color BackdropTint(string? themeName)
    {
        var theme = BuiltInThemes.All.FirstOrDefault(t => t.Name == themeName) ?? BuiltInThemes.VexDark;
        return Darken(Parse(theme.Background), 0.62);
    }

    private static void Set(ResourceDictionary res, string key, Color color)
        => res[key] = color;

    private static Color Parse(string hex)
        => (Color)ColorConverter.ConvertFromString(hex);

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
