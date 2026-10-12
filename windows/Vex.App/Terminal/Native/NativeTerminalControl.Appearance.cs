using System.Windows;
using System.Windows.Media;
using Vex.App.Model;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Rebuild only what the changed setting actually affects. Settings
        // that no pane renders (shell profiles, window backdrop) used to trigger a full
        var settings = AppSettings.Instance;
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ThemeName):
            case nameof(AppSettings.AutomaticContrast):
                var theme = BuiltInThemes.Resolve(settings.ThemeName);
                _palette = PaletteFor(theme);
                ApplyTerminalColors();
                RebuildScrollbarBrushes();
                RebuildLinkPen();
                _renderVersion++;
                _needsFullRedraw = true;
                InvalidateVisual(); // background brush changed
                FlushRedraw();
                break;
            case nameof(AppSettings.FontFamily):
                ApplyTypefaces(settings.FontFamily);
                RebuildFontMetrics();
                _needsFullRedraw = true;
                FlushRedraw();
                break;
            case nameof(AppSettings.FontSize):
                _fontSize = settings.FontSize;
                RebuildFontMetrics();
                _needsFullRedraw = true;
                FlushRedraw();
                break;
            case nameof(AppSettings.CursorBlink):
                _cursorBlinkSetting = settings.CursorBlink;
                UpdateBlinkTimer();
                DrawCaret();
                break;
        }
    }

    /// <summary>A family's four typeface/glyph pairs, cached per process:
    /// TryGetGlyphTypeface parses font files, so rebuilding it on every
    /// settings touch is wasteful.</summary>
    private sealed record TypefaceSet(
        Typeface Normal, Typeface Bold, Typeface Italic, Typeface BoldItalic,
        GlyphTypeface? NormalGlyph, GlyphTypeface? BoldGlyph, GlyphTypeface? ItalicGlyph, GlyphTypeface? BoldItalicGlyph,
        ushort[]? NormalAscii, ushort[]? BoldAscii, ushort[]? ItalicAscii, ushort[]? BoldItalicAscii);

    private static readonly Dictionary<(string ThemeName, bool AutomaticContrast), TerminalPalette> PaletteCache = new();
    private static readonly Dictionary<string, TypefaceSet> TypefaceCache = new();

    /// <summary>
    /// Prewarms the theme palette (frozen brushes) and OpenType typeface/glyph caches
    /// on a background task during startup so the first terminal pane realizes instantly
    /// on the UI thread with zero font-parsing latency.
    /// </summary>
    public static void Prewarm(string? themeName, string? fontFamily)
    {
        try
        {
            var theme = BuiltInThemes.Resolve(themeName);
            PaletteFor(theme);
            TypefacesFor(fontFamily ?? "Cascadia Mono");
            // Touch the native VT library off the UI thread so the first
            // GhosttyTerminal does not pay LoadLibrary inside the first frame.
            System.Runtime.InteropServices.NativeLibrary.TryLoad("ghostty-vt", out _);
        }
        catch
        {
            // Best-effort prewarm
        }
    }

    private static TerminalPalette PaletteFor(TerminalTheme theme)
    {
        // All panes share one palette per theme. A TerminalPalette builds 256
        // frozen brushes, so constructing one per pane per settings change
        // multiplied every slider tick by the pane count.
        lock (PaletteCache)
        {
            var key = (theme.Name, AppSettings.Instance.AutomaticContrast);
            if (!PaletteCache.TryGetValue(key, out var palette))
                PaletteCache[key] = palette = new TerminalPalette(theme, key.AutomaticContrast);
            return palette;
        }
    }

    /// <summary>Evicts a theme's cached palette so the next render rebuilds it
    /// from the current theme values. Used by the theme editor after mutating
    /// <see cref="BuiltInThemes.Custom"/>.</summary>
    public static void InvalidatePaletteCache(string themeName)
    {
        lock (PaletteCache)
        {
            PaletteCache.Remove((themeName, true));
            PaletteCache.Remove((themeName, false));
        }
    }

    private static TypefaceSet TypefacesFor(string familySource)
    {
        lock (TypefaceCache)
        {
            if (TypefaceCache.TryGetValue(familySource, out var set))
                return set;
            var family = new FontFamily(familySource);
            var normal = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var italic = new Typeface(family, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
            var boldItalic = new Typeface(family, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
            normal.TryGetGlyphTypeface(out var normalGlyph);
            bold.TryGetGlyphTypeface(out var boldGlyph);
            italic.TryGetGlyphTypeface(out var italicGlyph);
            boldItalic.TryGetGlyphTypeface(out var boldItalicGlyph);
            set = new TypefaceSet(
                normal, bold, italic, boldItalic,
                normalGlyph, boldGlyph, italicGlyph, boldItalicGlyph,
                BuildAsciiGlyphTable(normalGlyph),
                BuildAsciiGlyphTable(boldGlyph),
                BuildAsciiGlyphTable(italicGlyph),
                BuildAsciiGlyphTable(boldItalicGlyph));
            TypefaceCache[familySource] = set;
            return set;
        }
    }

    private static ushort[]? BuildAsciiGlyphTable(GlyphTypeface? glyph)
    {
        if (glyph is null)
            return null;
        var table = new ushort[128];
        var map = glyph.CharacterToGlyphMap;
        for (var c = 0; c < 128; c++)
        {
            if (map.TryGetValue(c, out var idx))
                table[c] = idx;
        }
        return table;
    }

    private void ApplyTypefaces(string familySource)
    {
        _fontFamily = new FontFamily(familySource);
        var set = TypefacesFor(familySource);
        _normalTypeface = set.Normal;
        _boldTypeface = set.Bold;
        _italicTypeface = set.Italic;
        _boldItalicTypeface = set.BoldItalic;
        _normalGlyph = set.NormalGlyph;
        _boldGlyph = set.BoldGlyph;
        _italicGlyph = set.ItalicGlyph;
        _boldItalicGlyph = set.BoldItalicGlyph;
        _normalAscii = set.NormalAscii;
        _boldAscii = set.BoldAscii;
        _italicAscii = set.ItalicAscii;
        _boldItalicAscii = set.BoldItalicAscii;
    }
    private void ApplySettings()
    {
        var settings = AppSettings.Instance;
        var theme = BuiltInThemes.Resolve(settings.ThemeName);
        _palette = PaletteFor(theme);
        ApplyTerminalColors();
        RebuildScrollbarBrushes();
        RebuildLinkPen();
        ApplyTypefaces(settings.FontFamily);
        _fontSize = settings.FontSize;
        _cursorBlinkSetting = settings.CursorBlink;
        UpdateBlinkTimer();
    }

    /// <summary>Pushes the theme's default colors into the emulator so ANSI
    /// and OSC color queries resolve against them.</summary>
    private void ApplyTerminalColors()
    {
        _fallbackCellDrawings.Clear();
        var fg = ((SolidColorBrush)_palette.Foreground).Color;
        var bg = ((SolidColorBrush)_palette.Background).Color;
        var cursor = ((SolidColorBrush)_palette.Cursor).Color;
        _terminal.SetDefaultColors(
            new GhosttyColorRgb(fg.R, fg.G, fg.B),
            new GhosttyColorRgb(bg.R, bg.G, bg.B),
            new GhosttyColorRgb(cursor.R, cursor.G, cursor.B),
            _palette.PaletteRgb());
    }
}
