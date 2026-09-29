using System.Windows;
using System.Windows.Media;
using Vex.App;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class ChromePaletteTests
{
    [Fact]
    public void Apply_DoesNotThrow_WhenBrushesAreFrozen()
    {
        var res = new ResourceDictionary();

        // Populate res with frozen brushes simulating WPF style/template sealing
        var frozenBrush = new SolidColorBrush(Colors.Black);
        frozenBrush.Freeze();
        res["VexText"] = frozenBrush;
        res["VexTextColor"] = Colors.Black;

        var frozenBg = new LinearGradientBrush(Colors.Red, Colors.Blue, 90);
        frozenBg.Freeze();
        res["VexBackground"] = frozenBg;
        res["VexBackgroundTopColor"] = Colors.Red;
        res["VexBackgroundBottomColor"] = Colors.Blue;

        var frozenTrans = new SolidColorBrush(Colors.Green);
        frozenTrans.Freeze();
        res["VexAccentTranslucent"] = frozenTrans;
        res["VexAccentColor"] = Colors.Green;

        // Apply dark theme, then flip to light theme
        ChromePalette.Apply(BuiltInThemes.VexDark, res);
        ChromePalette.Apply(BuiltInThemes.OneLight, res);

        // Brushes in res should now reflect the OneLight theme colors without crashing
        var textBrush = Assert.IsType<SolidColorBrush>(res["VexText"]);
        Assert.NotEqual(Colors.Black, textBrush.Color);

        var bgBrush = Assert.IsType<LinearGradientBrush>(res["VexBackground"]);
        Assert.NotEqual(Colors.Red, bgBrush.GradientStops[0].Color);
    }

    [Fact]
    public void Apply_MutatesInPlace_WhenBrushesAreUnfrozen()
    {
        var res = new ResourceDictionary();

        var unfrozenBrush = new SolidColorBrush(Colors.Black);
        res["VexText"] = unfrozenBrush;
        res["VexTextColor"] = Colors.Black;

        ChromePalette.Apply(BuiltInThemes.VexDark, res);
        Assert.Same(unfrozenBrush, res["VexText"]);

        var darkColor = unfrozenBrush.Color;

        ChromePalette.Apply(BuiltInThemes.OneLight, res);
        Assert.Same(unfrozenBrush, res["VexText"]);
        Assert.NotEqual(darkColor, unfrozenBrush.Color);
    }

    [Fact]
    public void Apply_TogglesAcrossMultipleThemesWithoutCrash()
    {
        var res = new ResourceDictionary();

        string[] themes = ["Vex Dark", "One Dark", "One Light", "GitHub Dark", "GitHub Light", "Dracula"];
        foreach (var theme in themes)
        {
            ChromePalette.Apply(BuiltInThemes.Resolve(theme), res);
            Assert.NotNull(res["VexText"]);
            Assert.NotNull(res["VexBackground"]);
        }
    }
}
