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

    public Brush Foreground { get; }
    public Brush Background { get; }
    public Brush Cursor { get; }
    public Brush Selection { get; }

    public TerminalPalette(TerminalTheme theme)
    {
        Foreground = Freeze(Parse(theme.Foreground));
        // Translucent so the window's acrylic backdrop shows through for a
        // frosted surface. The base fill covers the whole control uniformly
        // and text runs draw straight on top, so there is no double-darkening
        // where cells re-fill the default background.
        Background = Freeze(Parse(theme.Background, alpha: 0xE6));
        Cursor = Freeze(Parse(theme.Cursor));
        Selection = Freeze(Parse(theme.SelectionBackground, alpha: 0xA0));

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
    }

    /// <summary>The palette handed to libghostty for ANSI color resolution.</summary>
    public GhosttyColorRgb[] PaletteRgb()
    {
        var palette = new GhosttyColorRgb[256];
        for (var i = 0; i < 256; i++)
        {
            var c = ((SolidColorBrush)_ansi[i]).Color;
            palette[i] = new GhosttyColorRgb(c.R, c.G, c.B);
        }
        return palette;
    }

    private static Color Parse(string hex, byte alpha = 0xFF)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        color.A = alpha;
        return color;
    }

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
        var bold = flags.HasFlag(CellFlags.Bold);

        if (flags.HasFlag(CellFlags.Inverse))
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

        if (flags.HasFlag(CellFlags.Invisible))
        {
            // Same color as the background keeps selection+search readable-ish
            // while staying invisible on the plain surface.
            foreground = background ?? Background;
            return;
        }

        foreground = fgTag == ColorTag.None ? Foreground : ResolveColor(fgTag, fgValue, bold);

        if (flags.HasFlag(CellFlags.Faint) && foreground is SolidColorBrush solid)
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

    private static int RgbOf(Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        return (c.R << 16) | (c.G << 8) | c.B;
    }
}
