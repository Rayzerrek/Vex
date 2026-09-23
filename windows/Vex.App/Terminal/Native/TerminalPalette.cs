using System.Windows.Media;
using Vex.App.Model;
using Vex.Libghostty;
using Color = System.Windows.Media.Color;

namespace Vex.App.Terminal.Native;

/// <summary>
/// Resolves libghostty cell colors (tagged palette/RGB values) to WPF
/// brushes. The theme supplies the first 16 palette entries plus the default
/// foreground/background/cursor/selection; indices 16–255 follow the
/// standard xterm 256-color table. Brushes are frozen and cached.
/// </summary>
public sealed class TerminalPalette
{
    private readonly Brush[] _ansi = new Brush[256];
    private readonly Dictionary<int, Brush> _rgbCache = new();
    private readonly Dictionary<int, Brush> _dimCache = new();

    private readonly GhosttyColorRgb[] _paletteRgb = new GhosttyColorRgb[256];

    public Brush Foreground { get; }
    public Brush Background { get; }
    public Brush Cursor { get; }
    public Brush Selection { get; }
    public Brush Link { get; }

    public TerminalPalette(TerminalTheme theme)
    {
        Foreground = Freeze(Parse(theme.Foreground));
        // Keep the terminal surface opaque even when the window chrome uses
        // acrylic. DWM can retain stale WPF glyph tiles when translucent row
        // visuals are recomposed after Alt+Tab; opaque terminal pixels give it
        // a coherent backing store, matching Windows Terminal's own surface.
        var background = Parse(theme.Background);
        Background = Freeze(Color.FromRgb(background.R, background.G, background.B));
        Cursor = Freeze(Parse(theme.Cursor));
        Selection = Freeze(Parse(theme.SelectionBackground, alpha: 0xA0));
        Link = Freeze(Parse(theme.Blue));

        string[] theme16 =
        {
            theme.Black, theme.Red, theme.Green, theme.Yellow,
            theme.Blue, theme.Magenta, theme.Cyan, theme.White,
            theme.BrightBlack, theme.BrightRed, theme.BrightGreen, theme.BrightYellow,
            theme.BrightBlue, theme.BrightMagenta, theme.BrightCyan, theme.BrightWhite,
        };
        for (var i = 0; i < 16; i++)
            _ansi[i] = Freeze(Parse(theme16[i]));

        // xterm 256-color table: 6x6x6 cube then 24 grayscale steps.
        for (var i = 16; i < 232; i++)
        {
            var n = i - 16;
            var r = n / 36 % 6;
            var g = n / 6 % 6;
            var b = n % 6;
            _ansi[i] = Freeze(Color.FromRgb(
                (byte)(r == 0 ? 0 : 55 + r * 40),
                (byte)(g == 0 ? 0 : 55 + g * 40),
                (byte)(b == 0 ? 0 : 55 + b * 40)));
        }
        for (var i = 232; i < 256; i++)
        {
            var v = (byte)(8 + (i - 232) * 10);
            _ansi[i] = Freeze(Color.FromRgb(v, v, v));
        }

        for (var i = 0; i < 256; i++)
        {
            var c = ((SolidColorBrush)_ansi[i]).Color;
            _paletteRgb[i] = new GhosttyColorRgb(c.R, c.G, c.B);
        }
    }

    /// <summary>The palette handed to libghostty for ANSI color resolution.</summary>
    public GhosttyColorRgb[] PaletteRgb() => _paletteRgb;

    private static Color Parse(string hex, byte alpha = 0xFF)
        => FastColor.ParseHex(hex, alpha);

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private Brush ResolveColor(ColorTag tag, int value, bool bold)
    {
        switch (tag)
        {
            case ColorTag.Palette:
                var index = value;
                // Classic xterm convention: BOLD bumps the base 16 ANSI colors
                // to their bright variants (0-7 -> 8-15). Shells like nushell
                // rely on this for their default prompt/status colors.
                if (bold && index < 8)
                    index += 8;
                return _ansi[Math.Clamp(index, 0, 255)];
            case ColorTag.Rgb:
                if (!_rgbCache.TryGetValue(value, out var rgb))
                    _rgbCache[value] = rgb = Freeze(Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value));
                return rgb;
            default:
                return Background;
        }
    }

    /// <summary>
    /// Resolves a cell's tagged fg/bg colors into render-ready brushes. A
    /// null background means "default": the row already paints the theme
    /// background, so nothing needs drawing. INVERSE swaps (with xterm's
    /// default-swap semantics); INVISIBLE drops the foreground.
    /// </summary>
    public void Resolve(ColorTag fgTag, int fgValue, ColorTag bgTag, int bgValue,
        CellFlags flags, out Brush? foreground, out Brush? background)
    {
        var bold = (flags & CellFlags.Bold) != 0;

        if ((flags & CellFlags.Inverse) != 0)
        {
            (fgTag, bgTag) = (bgTag, fgTag);
            (fgValue, bgValue) = (bgValue, fgValue);
            // xterm reverse video: a default color on either side becomes
            // the other side's default.
            if (fgTag == ColorTag.None)
                (fgTag, fgValue) = (ColorTag.Rgb, RgbOf(Background));
            if (bgTag == ColorTag.None)
                (bgTag, bgValue) = (ColorTag.Rgb, RgbOf(Foreground));
        }

        background = bgTag == ColorTag.None ? null : ResolveColor(bgTag, bgValue, bold);

        if ((flags & CellFlags.Invisible) != 0)
        {
            // Same color as the background keeps selection+search readable-ish
            // while staying invisible on the plain surface.
            foreground = background ?? Background;
            return;
        }

        foreground = fgTag == ColorTag.None ? Foreground : ResolveColor(fgTag, fgValue, bold);

        // ConsoleColor-based Windows/.NET applications emit the 16 indexed
        // ANSI colors rather than RGB. Some use Black/White as if those names
        // always implied contrast; that becomes black-on-black in dark themes
        // or white-on-white in light themes. Preserve explicit RGB and every
        // readable indexed color, but replace a nearly invisible indexed
        // foreground with the theme's default foreground when it is clearer.
        if (fgTag == ColorTag.Palette && foreground is SolidColorBrush indexed
            && (background ?? Background) is SolidColorBrush effectiveBackground
            && Foreground is SolidColorBrush defaultForeground
            && ContrastRatio(indexed.Color, effectiveBackground.Color) < 1.8
            && ContrastRatio(defaultForeground.Color, effectiveBackground.Color)
                > ContrastRatio(indexed.Color, effectiveBackground.Color))
        {
            foreground = Foreground;
        }

        if ((flags & CellFlags.Faint) != 0 && foreground is SolidColorBrush solid)
        {
            if (!_dimCache.TryGetValue(fgValue, out var dim))
            {
                var c = solid.Color;
                dim = Freeze(Color.FromArgb((byte)(c.A * 0.6), c.R, c.G, c.B));
                _dimCache[fgValue] = dim;
            }
            foreground = dim;
        }
    }

    private readonly Dictionary<Brush, Pen> _penCache = new();

    public Pen GetPen(Brush brush)
    {
        if (!_penCache.TryGetValue(brush, out var pen))
        {
            pen = new Pen(brush, 1);
            pen.Freeze();
            _penCache[brush] = pen;
        }
        return pen;
    }

    private static int RgbOf(Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        return (c.R << 16) | (c.G << 8) | c.B;
    }

    private static double ContrastRatio(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        }

        var first = Luminance(a);
        var second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
}
