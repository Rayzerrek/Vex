using System.Windows.Media;
using Vex.App.Model;
using XtermSharp;
using Color = System.Windows.Media.Color;

namespace Vex.App.Terminal.Native;

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
    private readonly Brush[] _trueColorBrushes = new Brush[Renderer.TrueColorEnd - Renderer.TrueColorStart + 1];

    public Brush Foreground { get; }
    public Brush Background { get; }
    public Brush Cursor { get; }
    public Brush Selection { get; }

    public TerminalPalette(TerminalTheme theme)
    {
        Foreground = Freeze(Parse(theme.Foreground));
        // Opaque so the surface is one uniform color from the first row to the
        // bottom edge: a translucent fill let the darker window show through
        // the partial bottom row as a visible strip.
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

    private bool TryResolveTrueColor(int index, out Brush? brush)
    {
        var slot = index - Renderer.TrueColorStart;
        if ((uint)slot < (uint)_trueColorBrushes.Length)
        {
            brush = _trueColorBrushes[slot];
            if (brush is null && XtermSharp.Renderer.TryGetTrueColor(index, out var color))
            {
                brush = Freeze(Color.FromRgb(color.Red, color.Green, color.Blue));
                _trueColorBrushes[slot] = brush;
            }
            return brush is not null;
        }

        brush = null;
        return false;
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
        // Classic xterm convention: BOLD bumps the base 16 ANSI colors to
        // their bright variants (0-7 -> 8-15). Shells like nushell rely on
        // this for their default prompt/status colors, so without the shift
        // bold output renders in the dark variants and looks wrong.
        if (flags.HasFlag(FLAGS.BOLD))
        {
            if (fg < 8)
                fg += 8;
            if (bg < 8)
                bg += 8;
        }

        background = bg switch
        {
            XtermSharp.Renderer.DefaultColor => null,
            XtermSharp.Renderer.InvertedDefaultColor => Foreground,
            _ when bg < XtermSharp.Renderer.TrueColorStart => _ansi[bg],
            _ when TryResolveTrueColor(bg, out var brush) => brush,
            _ => null,
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
            _ when fg < XtermSharp.Renderer.TrueColorStart => _ansi[fg],
            _ when TryResolveTrueColor(fg, out var brush) => brush,
            _ => null,
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
