namespace Vex.App.Model;

public sealed class TerminalTheme
{
    public string Name { get; set; } = "";
    public string Background { get; set; } = "";
    public string Foreground { get; set; } = "";
    public string Cursor { get; set; } = "";
    public string SelectionBackground { get; set; } = "";
    
    public string Black { get; set; } = "";
    public string Red { get; set; } = "";
    public string Green { get; set; } = "";
    public string Yellow { get; set; } = "";
    public string Blue { get; set; } = "";
    public string Magenta { get; set; } = "";
    public string Cyan { get; set; } = "";
    public string White { get; set; } = "";
    
    public string BrightBlack { get; set; } = "";
    public string BrightRed { get; set; } = "";
    public string BrightGreen { get; set; } = "";
    public string BrightYellow { get; set; } = "";
    public string BrightBlue { get; set; } = "";
    public string BrightMagenta { get; set; } = "";
    public string BrightCyan { get; set; } = "";
    public string BrightWhite { get; set; } = "";

    /// <summary>True for themes with a dark terminal background. The theme
    /// picker filters on this so only themes matching the app appearance are
    /// offered, and the chrome derives its own light/dark variant from it.</summary>
    public bool IsDark
    {
        get
        {
            // Perceived (gamma-corrected) luminance of the background; the
            // 0.5 threshold cleanly splits every built-in theme.
            var c = FastColor.ParseHex(Background);
            return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 < 0.5;
        }
    }
}

public static class BuiltInThemes
{
    public static readonly TerminalTheme VexDark = new()
    {
        Name = "Vex Dark",
        Background = "#282C34",
        Foreground = "#E4E4E7",
        Cursor = "#E4E4E7",
        SelectionBackground = "#3D4354",
        Black = "#282C34",
        Red = "#FF5A5A",
        Green = "#5AF05A",
        Yellow = "#F0D25A",
        Blue = "#5A5AFF",
        Magenta = "#F05AF0",
        Cyan = "#5AF0F0",
        White = "#E4E4E7",
        BrightBlack = "#3D4354",
        BrightRed = "#FF8C8C",
        BrightGreen = "#8CFF8C",
        BrightYellow = "#FFE68C",
        BrightBlue = "#8C8CFF",
        BrightMagenta = "#FF8CFF",
        BrightCyan = "#8CFFFF",
        BrightWhite = "#FFFFFF"
    };

    public static readonly TerminalTheme OneDark = new()
    {
        Name = "One Dark",
        Background = "#282C34",
        Foreground = "#ABB2BF",
        Cursor = "#528BFF",
        SelectionBackground = "#3E4452",
        Black = "#282C34",
        Red = "#E06C75",
        Green = "#98C379",
        Yellow = "#E5C07B",
        Blue = "#61AFEF",
        Magenta = "#C678DD",
        Cyan = "#56B6C2",
        White = "#ABB2BF",
        BrightBlack = "#5C6370",
        BrightRed = "#E06C75",
        BrightGreen = "#98C379",
        BrightYellow = "#E5C07B",
        BrightBlue = "#61AFEF",
        BrightMagenta = "#C678DD",
        BrightCyan = "#56B6C2",
        BrightWhite = "#FFFFFF"
    };

    public static readonly TerminalTheme SolarizedDark = new()
    {
        Name = "Solarized Dark",
        Background = "#002B36",
        Foreground = "#839496",
        Cursor = "#93A1A1",
        SelectionBackground = "#073642",
        Black = "#073642",
        Red = "#DC322F",
        Green = "#859900",
        Yellow = "#B58900",
        Blue = "#268BD2",
        Magenta = "#D33682",
        Cyan = "#2AA198",
        White = "#EEE8D5",
        BrightBlack = "#002B36",
        BrightRed = "#CB4B16",
        BrightGreen = "#586E75",
        BrightYellow = "#657B83",
        BrightBlue = "#839496",
        BrightMagenta = "#6C71C4",
        BrightCyan = "#93A1A1",
        BrightWhite = "#FDF6E3"
    };

    public static readonly TerminalTheme CatppuccinMocha = new()
    {
        Name = "Catppuccin Mocha",
        Background = "#1E1E2E",
        Foreground = "#CDD6F4",
        Cursor = "#F5E0DC",
        SelectionBackground = "#585B70",
        Black = "#45475A",
        Red = "#F38BA8",
        Green = "#A6E3A1",
        Yellow = "#F9E2AF",
        Blue = "#89B4FA",
        Magenta = "#F5C2E7",
        Cyan = "#94E2D5",
        White = "#BAC2DE",
        BrightBlack = "#585B70",
        BrightRed = "#F38BA8",
        BrightGreen = "#A6E3A1",
        BrightYellow = "#F9E2AF",
        BrightBlue = "#89B4FA",
        BrightMagenta = "#F5C2E7",
        BrightCyan = "#94E2D5",
        BrightWhite = "#A6ADC8"
    };

    public static readonly TerminalTheme RoséPine = new()
    {
        Name = "Rosé Pine",
        Background = "#191724",
        Foreground = "#E0DEF4",
        Cursor = "#E0DEF4",
        SelectionBackground = "#403D52",
        Black = "#26233A",
        Red = "#EB6F92",
        Green = "#31748F",
        Yellow = "#F6C177",
        Blue = "#9CCFD8",
        Magenta = "#C4A7E7",
        Cyan = "#EBBCBA",
        White = "#E0DEF4",
        BrightBlack = "#6E6A86",
        BrightRed = "#EB6F92",
        BrightGreen = "#31748F",
        BrightYellow = "#F6C177",
        BrightBlue = "#9CCFD8",
        BrightMagenta = "#C4A7E7",
        BrightCyan = "#EBBCBA",
        BrightWhite = "#E0DEF4"
    };

    public static readonly TerminalTheme VexLight = new()
    {
        Name = "Vex Light",
        Background = "#F7F8FA",
        Foreground = "#31353B",
        Cursor = "#3B6FF0",
        SelectionBackground = "#D5DCE8",
        Black = "#F7F8FA",
        Red = "#D13438",
        Green = "#1E8A3C",
        Yellow = "#B7791F",
        Blue = "#2B5FD9",
        Magenta = "#A626A4",
        Cyan = "#0B7285",
        White = "#31353B",
        BrightBlack = "#9199A3",
        BrightRed = "#E5534B",
        BrightGreen = "#2DA44E",
        BrightYellow = "#C98A2B",
        BrightBlue = "#4A72E8",
        BrightMagenta = "#BC4CB8",
        BrightCyan = "#3198AD",
        BrightWhite = "#1F2328"
    };

    public static readonly TerminalTheme OneLight = new()
    {
        Name = "One Light",
        Background = "#FAFAFA",
        Foreground = "#383A42",
        Cursor = "#526FFF",
        SelectionBackground = "#DFE3EB",
        Black = "#FAFAFA",
        Red = "#E45649",
        Green = "#50A14F",
        Yellow = "#C18401",
        Blue = "#4078F2",
        Magenta = "#A626A4",
        Cyan = "#0184BC",
        White = "#383A42",
        BrightBlack = "#A0A1A7",
        BrightRed = "#E45649",
        BrightGreen = "#50A14F",
        BrightYellow = "#C18401",
        BrightBlue = "#4078F2",
        BrightMagenta = "#A626A4",
        BrightCyan = "#0184BC",
        BrightWhite = "#090A0B"
    };

    public static readonly TerminalTheme SolarizedLight = new()
    {
        Name = "Solarized Light",
        Background = "#FDF6E3",
        Foreground = "#657B83",
        Cursor = "#586E75",
        SelectionBackground = "#EEE8D5",
        Black = "#FDF6E3",
        Red = "#DC322F",
        Green = "#859900",
        Yellow = "#B58900",
        Blue = "#268BD2",
        Magenta = "#D33682",
        Cyan = "#2AA198",
        White = "#839496",
        BrightBlack = "#EEE8D5",
        BrightRed = "#CB4B16",
        BrightGreen = "#586E75",
        BrightYellow = "#657B83",
        BrightBlue = "#839496",
        BrightMagenta = "#6C71C4",
        BrightCyan = "#93A1A1",
        BrightWhite = "#073642"
    };

    public static readonly TerminalTheme GitHubLight = new()
    {
        Name = "GitHub Light",
        Background = "#FFFFFF",
        Foreground = "#24292F",
        Cursor = "#0969DA",
        SelectionBackground = "#DBEDFF",
        Black = "#FFFFFF",
        Red = "#CF222E",
        Green = "#116329",
        Yellow = "#9A6700",
        Blue = "#0969DA",
        Magenta = "#8250DF",
        Cyan = "#1B7C83",
        White = "#57606A",
        BrightBlack = "#8C959F",
        BrightRed = "#A40E26",
        BrightGreen = "#1A7F37",
        BrightYellow = "#BF8700",
        BrightBlue = "#218BFF",
        BrightMagenta = "#A475F9",
        BrightCyan = "#3192AA",
        BrightWhite = "#24292F"
    };

    public static readonly TerminalTheme CatppuccinLatte = new()
    {
        Name = "Catppuccin Latte",
        Background = "#EFF1F5",
        Foreground = "#4C4F69",
        Cursor = "#DC8A78",
        SelectionBackground = "#BCC0CC",
        Black = "#5C5F77",
        Red = "#D20F39",
        Green = "#40A02B",
        Yellow = "#DF8E1D",
        Blue = "#1E66F5",
        Magenta = "#EA76CB",
        Cyan = "#179299",
        White = "#ACB0BE",
        BrightBlack = "#6C6F85",
        BrightRed = "#D20F39",
        BrightGreen = "#40A02B",
        BrightYellow = "#DF8E1D",
        BrightBlue = "#1E66F5",
        BrightMagenta = "#EA76CB",
        BrightCyan = "#179299",
        BrightWhite = "#4C4F69"
    };

    public static readonly TerminalTheme RoséPineDawn = new()
    {
        Name = "Rosé Pine Dawn",
        Background = "#FAF4ED",
        Foreground = "#575279",
        Cursor = "#575279",
        SelectionBackground = "#EDE3DA",
        Black = "#F2E9E1",
        Red = "#B4637A",
        Green = "#286983",
        Yellow = "#EA9D34",
        Blue = "#56949F",
        Magenta = "#907AA9",
        Cyan = "#D7827E",
        White = "#575279",
        BrightBlack = "#9893A5",
        BrightRed = "#B4637A",
        BrightGreen = "#286983",
        BrightYellow = "#EA9D34",
        BrightBlue = "#56949F",
        BrightMagenta = "#907AA9",
        BrightCyan = "#D7827E",
        BrightWhite = "#575279"
    };

    /// <summary>A mutable theme that the built-in editor writes into. Not in
    /// <see cref="All"/> (so it never appears as a card), but lookup sites
    /// fall back to it when <c>ThemeName</c> is "Custom".</summary>
    public static TerminalTheme Custom { get; } = Clone(VexDark);

    public static TerminalTheme[] All => _all;

    private static readonly TerminalTheme[] _all =
    {
        // Dark appearance
        VexDark, OneDark, SolarizedDark, CatppuccinMocha, RoséPine,
        // Light appearance
        VexLight, OneLight, SolarizedLight, GitHubLight, CatppuccinLatte, RoséPineDawn,
    };

    /// <summary>Themes matching the given appearance, in card order. The
    /// theme picker shows only these so a light app never offers a dark
    /// terminal theme (and vice versa).</summary>
    public static TerminalTheme[] ForAppearance(bool dark)
        => All.Where(t => t.IsDark == dark).ToArray();

    /// <summary>Resolves a theme by name, falling back to <see cref="Custom"/>
    /// when the name is "Custom", and to <see cref="VexDark"/> as a last
    /// resort.</summary>
    public static TerminalTheme Resolve(string? name)
    {
        if (name == "Custom")
            return Custom;
        return All.FirstOrDefault(t => t.Name == name) ?? VexDark;
    }

    /// <summary>Resolves a theme by name within one appearance. A stored name
    /// from the other appearance (or unknown) falls back to that appearance's
    /// first theme, so a stale setting can never re-tint the app dark.</summary>
    public static TerminalTheme ResolveInAppearance(string? name, bool dark)
    {
        if (name == "Custom" && Custom.IsDark == dark)
            return Custom;
        var matches = ForAppearance(dark);
        return matches.FirstOrDefault(t => t.Name == name) ?? matches[0];
    }

    public static TerminalTheme Clone(TerminalTheme source) => new()
    {
        Name = source.Name,
        Background = source.Background,
        Foreground = source.Foreground,
        Cursor = source.Cursor,
        SelectionBackground = source.SelectionBackground,
        Black = source.Black,
        Red = source.Red,
        Green = source.Green,
        Yellow = source.Yellow,
        Blue = source.Blue,
        Magenta = source.Magenta,
        Cyan = source.Cyan,
        White = source.White,
        BrightBlack = source.BrightBlack,
        BrightRed = source.BrightRed,
        BrightGreen = source.BrightGreen,
        BrightYellow = source.BrightYellow,
        BrightBlue = source.BrightBlue,
        BrightMagenta = source.BrightMagenta,
        BrightCyan = source.BrightCyan,
        BrightWhite = source.BrightWhite,
    };
}
