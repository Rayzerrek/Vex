using System.Windows.Media;
using Vex.App.Model;
using Vex.App.Terminal.Native;
using Vex.Libghostty;
using Xunit;

namespace Vex.App.Tests;

public sealed class TerminalPaletteTests
{
    [Theory]
    [InlineData(0xffffaa, 0xffffff)]
    [InlineData(0x222222, 0x181818)]
    [InlineData(0x88bbff, 0xffffff)]
    [InlineData(0x888888, 0x888888)]
    public void AutomaticContrast_RepairsTruecolorAgainstTheActualBackground(int fg, int bg)
    {
        var palette = new TerminalPalette(BuiltInThemes.VexLight);
        palette.Resolve(ColorTag.Rgb, fg, ColorTag.Rgb, bg, CellFlags.None, out var foreground, out var background);
        var text = Assert.IsType<SolidColorBrush>(foreground);
        var surface = Assert.IsType<SolidColorBrush>(background);
        Assert.True(TerminalContrastCorrection.ContrastRatio(text.Color, surface.Color) >= 4.5);
        Assert.True(text.IsFrozen);
        palette.Resolve(ColorTag.Rgb, fg, ColorTag.Rgb, bg, CellFlags.None, out var cached, out _);
        Assert.Same(text, cached);
    }

    [Fact]
    public void AutomaticContrast_PreservesReadableColorsAndColorIdentity()
    {
        var readable = Color.FromRgb(160, 0, 0);
        Assert.Equal(readable, TerminalContrastCorrection.CorrectForeground(readable, Colors.White, 4.5));
        var yellow = TerminalContrastCorrection.CorrectForeground(Color.FromRgb(255, 255, 160), Colors.White, 4.5);
        Assert.True(yellow.R > yellow.B && yellow.G > yellow.B, "Yellow must not collapse to the default gray foreground.");
        var blue = TerminalContrastCorrection.CorrectForeground(Color.FromRgb(160, 190, 255), Colors.White, 4.5);
        Assert.True(blue.B > blue.R && blue.B > blue.G);
    }

    [Fact]
    public void AutomaticContrast_HandlesInverseFaintAndInvisibleWithoutChangingPaletteQueries()
    {
        var palette = new TerminalPalette(BuiltInThemes.VexLight);
        var originalPalette = palette.PaletteRgb().ToArray();
        palette.Resolve(ColorTag.Rgb, 0xffffff, ColorTag.Rgb, 0xffffee, CellFlags.Inverse, out var inverse, out var background);
        Assert.True(TerminalContrastCorrection.ContrastRatio(Assert.IsType<SolidColorBrush>(inverse).Color,
            Assert.IsType<SolidColorBrush>(background).Color) >= 4.5);
        palette.Resolve(ColorTag.Rgb, 0xffffaa, ColorTag.Rgb, 0xffffff, CellFlags.Faint, out var faint, out _);
        Assert.True(TerminalContrastCorrection.ContrastRatio(Assert.IsType<SolidColorBrush>(faint).Color, Colors.White) >= 3);
        palette.Resolve(ColorTag.Rgb, 0, ColorTag.Rgb, 0xffffff, CellFlags.Invisible, out var invisible, out var surface);
        Assert.Same(surface, invisible);
        Assert.Equal(originalPalette, palette.PaletteRgb());
    }

    [Theory]
    [InlineData(CellFlags.None)]
    [InlineData(CellFlags.Bold)]
    [InlineData(CellFlags.Faint)]
    [InlineData(CellFlags.Inverse)]
    [InlineData(CellFlags.Invisible)]
    public void AutomaticContrast_GraphicsOptOutPreservesColorsAndSgr(CellFlags flags)
    {
        var palette = new TerminalPalette(BuiltInThemes.VexDark);
        var uncorrected = new TerminalPalette(BuiltInThemes.VexDark, automaticContrast: false);
        palette.Resolve(ColorTag.Rgb, 0x505a64, ColorTag.Rgb, 0x5a646e, flags,
            out var foreground, out var background, correctContrast: false);
        uncorrected.Resolve(ColorTag.Rgb, 0x505a64, ColorTag.Rgb, 0x5a646e, flags,
            out var originalForeground, out var originalBackground);
        Assert.Equal(Assert.IsType<SolidColorBrush>(originalForeground).Color, Assert.IsType<SolidColorBrush>(foreground).Color);
        Assert.Equal(Assert.IsType<SolidColorBrush>(originalBackground).Color, Assert.IsType<SolidColorBrush>(background).Color);
    }

    [Fact]
    public void AutomaticContrast_CanBeDisabledAndPersistsInSettings()
    {
        var palette = new TerminalPalette(BuiltInThemes.VexLight, automaticContrast: false);
        palette.Resolve(ColorTag.Rgb, 0xffffaa, ColorTag.Rgb, 0xffffff, CellFlags.None, out var foreground, out _);
        Assert.Equal(Color.FromRgb(255, 255, 170), Assert.IsType<SolidColorBrush>(foreground).Color);
        var settings = new AppSettings { AutomaticContrast = false };
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(settings, VexJsonContext.Default.AppSettings);
        Assert.False(AppSettings.DeserializeSettings(json).AutomaticContrast);
        Assert.True(AppSettings.DeserializeSettings("{}"u8.ToArray()).AutomaticContrast);
    }

    [Fact]
    public void DecorationPens_EvictOldColorsButKeepRetainedDrawingsImmutable()
    {
        var palette = new TerminalPalette(BuiltInThemes.VexDark);
        var original = palette.GetPen(Brushes.Red);
        for (var index = 0; index < 8192; index++)
            palette.GetPen(new SolidColorBrush(Color.FromRgb((byte)(index >> 8), (byte)index, 128)));
        var replacement = palette.GetPen(Brushes.Red);
        Assert.NotSame(original, replacement);
        Assert.Same(replacement, palette.GetPen(Brushes.Red));
        Assert.True(original.IsFrozen && replacement.IsFrozen);
        Assert.Same(Brushes.Red, original.Brush);
    }

    [Fact]
    public void FaintForeground_DoesNotReuseDefaultColorForRgbBlack()
    {
        var palette = new TerminalPalette(BuiltInThemes.VexDark, automaticContrast: false);
        palette.Resolve(ColorTag.None, 0, ColorTag.None, 0, CellFlags.Faint, out _, out _);
        palette.Resolve(ColorTag.Rgb, 0, ColorTag.None, 0, CellFlags.Faint, out var foreground, out _);
        var color = Assert.IsType<SolidColorBrush>(foreground).Color;
        Assert.Equal(Color.FromArgb(153, 0, 0, 0), color);
    }

    [Fact]
    public void FaintForeground_KeepsBoldAndRegularAnsiColorsDistinct()
    {
        var palette = new TerminalPalette(BuiltInThemes.VexDark);
        palette.Resolve(ColorTag.Palette, 1, ColorTag.None, 0, CellFlags.Faint, out var regular, out _);
        palette.Resolve(ColorTag.Palette, 1, ColorTag.None, 0, CellFlags.Faint | CellFlags.Bold, out var bold, out _);
        palette.Resolve(ColorTag.Palette, 9, ColorTag.None, 0, CellFlags.Faint, out var bright, out _);
        Assert.Equal(Assert.IsType<SolidColorBrush>(bright).Color, Assert.IsType<SolidColorBrush>(bold).Color);
        Assert.NotEqual(Assert.IsType<SolidColorBrush>(regular).Color, Assert.IsType<SolidColorBrush>(bold).Color);
    }

    [Fact]
    public void FaintForeground_ReevaluatesContrastWhenBackgroundChanges()
    {
        var palette = new TerminalPalette(BuiltInThemes.VexDark);
        palette.Resolve(ColorTag.Palette, 0, ColorTag.None, 0, CellFlags.Faint, out var repaired, out _);
        palette.Resolve(ColorTag.Palette, 0, ColorTag.Rgb, 0xffffff, CellFlags.Faint, out var readable, out _);
        Assert.NotEqual(Assert.IsType<SolidColorBrush>(repaired).Color, Assert.IsType<SolidColorBrush>(readable).Color);
    }
}
