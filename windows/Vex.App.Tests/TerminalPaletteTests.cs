using System.Windows.Media;
using Vex.App.Model;
using Vex.App.Terminal.Native;
using Vex.Libghostty;
using Xunit;

namespace Vex.App.Tests;

public sealed class TerminalPaletteTests
{
    [Fact]
    public void FaintForeground_DoesNotReuseDefaultColorForRgbBlack()
    {
        var palette = new TerminalPalette(BuiltInThemes.VexDark);
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
