namespace Vex.App.Model;

public class TerminalTheme
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

    public static readonly TerminalTheme Dracula = new()
    {
        Name = "Dracula",
        Background = "#282A36",
        Foreground = "#F8F8F2",
        Cursor = "#F8F8F2",
        SelectionBackground = "#44475A",
        Black = "#21222C",
        Red = "#FF5555",
        Green = "#50FA7B",
        Yellow = "#F1FA8C",
        Blue = "#BD93F9",
        Magenta = "#FF79C6",
        Cyan = "#8BE9FD",
        White = "#F8F8F2",
        BrightBlack = "#6272A4",
        BrightRed = "#FF6E6E",
        BrightGreen = "#69FF94",
        BrightYellow = "#FFFFA5",
        BrightBlue = "#D6ACFF",
        BrightMagenta = "#FF92DF",
        BrightCyan = "#A4FFFF",
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

    public static readonly TerminalTheme Nord = new()
    {
        Name = "Nord",
        Background = "#2E3440",
        Foreground = "#D8DEE9",
        Cursor = "#D8DEE9",
        SelectionBackground = "#434C5E",
        Black = "#3B4252",
        Red = "#BF616A",
        Green = "#A3BE8C",
        Yellow = "#EBCB8B",
        Blue = "#81A1C1",
        Magenta = "#B48EAD",
        Cyan = "#88C0D0",
        White = "#E5E9F0",
        BrightBlack = "#4C566A",
        BrightRed = "#BF616A",
        BrightGreen = "#A3BE8C",
        BrightYellow = "#EBCB8B",
        BrightBlue = "#81A1C1",
        BrightMagenta = "#B48EAD",
        BrightCyan = "#8FBCBB",
        BrightWhite = "#ECEFF4"
    };

    public static readonly TerminalTheme TokyoNight = new()
    {
        Name = "Tokyo Night",
        Background = "#1A1B26",
        Foreground = "#A9B1D6",
        Cursor = "#C0CAF5",
        SelectionBackground = "#33467C",
        Black = "#15161E",
        Red = "#F7768E",
        Green = "#9ECE6A",
        Yellow = "#E0AF68",
        Blue = "#7AA2F7",
        Magenta = "#BB9AF7",
        Cyan = "#7DCFFF",
        White = "#A9B1D6",
        BrightBlack = "#414868",
        BrightRed = "#F7768E",
        BrightGreen = "#9ECE6A",
        BrightYellow = "#E0AF68",
        BrightBlue = "#7AA2F7",
        BrightMagenta = "#BB9AF7",
        BrightCyan = "#7DCFFF",
        BrightWhite = "#C0CAF5"
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

    public static readonly TerminalTheme GruvboxDark = new()
    {
        Name = "Gruvbox Dark",
        Background = "#282828",
        Foreground = "#EBDBB2",
        Cursor = "#EBDBB2",
        SelectionBackground = "#504945",
        Black = "#282828",
        Red = "#CC241D",
        Green = "#98971A",
        Yellow = "#D79921",
        Blue = "#458588",
        Magenta = "#B16286",
        Cyan = "#689D6A",
        White = "#A89984",
        BrightBlack = "#928374",
        BrightRed = "#FB4934",
        BrightGreen = "#B8BB26",
        BrightYellow = "#FABD2F",
        BrightBlue = "#83A598",
        BrightMagenta = "#D3869B",
        BrightCyan = "#8EC07C",
        BrightWhite = "#EBDBB2"
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

    public static readonly TerminalTheme Kanagawa = new()
    {
        Name = "Kanagawa",
        Background = "#1F1F28",
        Foreground = "#DCDFE4",
        Cursor = "#C8C093",
        SelectionBackground = "#2D2D3E",
        Black = "#090618",
        Red = "#C34043",
        Green = "#76946A",
        Yellow = "#C0A36E",
        Blue = "#7E9CD8",
        Magenta = "#957FB8",
        Cyan = "#6A9589",
        White = "#C8C093",
        BrightBlack = "#727169",
        BrightRed = "#E82424",
        BrightGreen = "#98BB6C",
        BrightYellow = "#E6C384",
        BrightBlue = "#7FB4CA",
        BrightMagenta = "#938AA9",
        BrightCyan = "#7AA89F",
        BrightWhite = "#DCDFE4"
    };

    public static readonly TerminalTheme Everforest = new()
    {
        Name = "Everforest",
        Background = "#2D353B",
        Foreground = "#D3C6AA",
        Cursor = "#D3C6AA",
        SelectionBackground = "#475258",
        Black = "#343F44",
        Red = "#E67E80",
        Green = "#A7C080",
        Yellow = "#DBBC7F",
        Blue = "#7FBBB3",
        Magenta = "#D699B6",
        Cyan = "#83C092",
        White = "#D3C6AA",
        BrightBlack = "#475258",
        BrightRed = "#E67E80",
        BrightGreen = "#A7C080",
        BrightYellow = "#DBBC7F",
        BrightBlue = "#7FBBB3",
        BrightMagenta = "#D699B6",
        BrightCyan = "#83C092",
        BrightWhite = "#D3C6AA"
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
        VexDark, OneDark, Dracula, SolarizedDark, Nord, TokyoNight,
        CatppuccinMocha, GruvboxDark, RoséPine, Kanagawa, Everforest,
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
        if (name == "Custom")
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
