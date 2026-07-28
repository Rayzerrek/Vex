using System.Windows.Media;
using Kero.App.Model;
using XtermSharp;
using Color = System.Windows.Media.Color;

namespace Kero.App.Terminal.Native;

/// <summary>
/// Resolves packed xterm cell attributes ((flags &lt;&lt; 18) | (fg &lt;&lt; 9) | bg)
/// to WPF brushes. The theme supplies the first 16 palette entries plus the
/// default foreground/background/cursor/selection; indices 16–255 come from
/// XtermSharp's standard 256-color table. Brushes are frozen and cached.
/// </summary>
public sealed class TerminalPalette
{
    private readonly Brush[] _ansi = new Brush[256];
    private readonly Dictionary<int, Brush> _dimCache = new();

    public Brush Foreground { get; }
    public Brush Background { get; }
    public Brush Cursor { get; }
    public Brush Selection { get; }

    public TerminalPalette(TerminalTheme theme)
    {
        Foreground = Freeze(Parse(theme.Foreground));
        Background = Freeze(Parse(theme.Background));
        Cursor = Freeze(Parse(theme.Cursor));
        Selection = Freeze(Parse(theme.SelectionBackground, alpha: 0xA0));

        string[] theme16 =
        {
            theme.Black, theme.Red, theme.Green, theme.Yellow,
            theme.Blue, theme.Magenta, theme.Cyan, theme.White,
            theme.BrightBlack, theme.BrightRed, theme.BrightGreen, theme.BrightYellow,
            theme.BrightBlue, theme.BrightMagenta, theme.BrightCyan, theme.BrightWhite,
        };
        for (var i = 0; i < 256; i++)
        {
            if (i < 16)
            {
                _ansi[i] = Freeze(Parse(theme16[i]));
            }
            else
            {
                var c = XtermSharp.Color.DefaultAnsiColors[i];
                _ansi[i] = Freeze(Color.FromRgb(c.Red, c.Green, c.Blue));
            }
        }
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

    /// <summary>
    /// Splits a packed attribute into render-ready brushes. A null background
    /// means "default": the row already paints the theme background, so
    /// nothing needs drawing. INVERSE is applied by swapping; INVISIBLE by
    /// dropping the foreground (the background fill remains).
    /// </summary>
    public void Resolve(int attribute, out Brush? foreground, out Brush? background, out FLAGS flags)
    {
        var fg = (attribute >> 9) & 0x1ff;
        var bg = attribute & 0x1ff;
        flags = (FLAGS)(attribute >> 18);

        if (flags.HasFlag(FLAGS.INVERSE))
        {
            (fg, bg) = (bg, fg);
            if (fg == XtermSharp.Renderer.DefaultColor)
                fg = XtermSharp.Renderer.InvertedDefaultColor;
            if (bg == XtermSharp.Renderer.DefaultColor)
                bg = XtermSharp.Renderer.InvertedDefaultColor;
        }

        background = bg switch
        {
            XtermSharp.Renderer.DefaultColor => null,
            XtermSharp.Renderer.InvertedDefaultColor => Foreground,
            _ => _ansi[bg],
        };

        if (flags.HasFlag(FLAGS.INVISIBLE))
        {
            // Same color as the background keeps selection+search readable-ish
            // while staying invisible on the plain surface.
            foreground = background ?? Background;
            return;
        }

        foreground = fg switch
        {
            XtermSharp.Renderer.DefaultColor => Foreground,
            XtermSharp.Renderer.InvertedDefaultColor => Background,
            _ => _ansi[fg],
        };

        if (flags.HasFlag(FLAGS.DIM) && foreground is SolidColorBrush solid)
        {
            if (!_dimCache.TryGetValue(fg, out var dim))
            {
                var c = solid.Color;
                dim = Freeze(Color.FromArgb((byte)(c.A * 0.6), c.R, c.G, c.B));
                _dimCache[fg] = dim;
            }
            foreground = dim;
        }
    }
}
