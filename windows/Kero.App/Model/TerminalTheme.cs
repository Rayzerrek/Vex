namespace Kero.App.Model;

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
}

public static class BuiltInThemes
{
    public static readonly TerminalTheme KeroDark = new()
    {
        Name = "Kero Dark",
        Background = "#1F1F24",
        Foreground = "#E4E4E7",
        Cursor = "#E4E4E7",
        SelectionBackground = "#3D4354",
        Black = "#1F1F24",
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

    public static readonly TerminalTheme[] All = { KeroDark, OneDark, Dracula, SolarizedDark, Nord };
}
