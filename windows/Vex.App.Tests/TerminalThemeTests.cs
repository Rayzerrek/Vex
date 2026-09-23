using Vex.App;
using Vex.App.Model;
using Vex.App.Terminal.Native;
using Xunit;

namespace Vex.App.Tests;

/// <summary>
/// Theme resolution decides what the terminal and chrome look like, and a
/// stale name persisted from the other appearance must never re-tint the app.
/// </summary>
public sealed class TerminalThemeTests
{
    [Fact]
    public void AllThemes_AreUniqueAndFullyPopulated()
    {
        var names = BuiltInThemes.All.Select(t => t.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());

        foreach (var theme in BuiltInThemes.All)
            AssertThemeIsComplete(theme);
    }

    [Fact]
    public void ForAppearance_SplitsThemesCleanly()
    {
        var dark = BuiltInThemes.ForAppearance(dark: true);
        var light = BuiltInThemes.ForAppearance(dark: false);

        Assert.NotEmpty(dark);
        Assert.NotEmpty(light);
        Assert.All(dark, t => Assert.True(t.IsDark, $"{t.Name} should be dark"));
        Assert.All(light, t => Assert.False(t.IsDark, $"{t.Name} should be light"));
        Assert.Equal(BuiltInThemes.All.Length, dark.Length + light.Length);
    }

    [Fact]
    public void Resolve_ReturnsTheNamedTheme()
    {
        Assert.Equal("One Dark", BuiltInThemes.Resolve("One Dark").Name);
    }

    [Fact]
    public void Resolve_FallsBackToVexDarkForUnknownNames()
    {
        Assert.Same(BuiltInThemes.VexDark, BuiltInThemes.Resolve("No Such Theme"));
        Assert.Same(BuiltInThemes.VexDark, BuiltInThemes.Resolve(null));
    }

    [Fact]
    public void Resolve_CustomNameReturnsTheMutableCustomTheme()
    {
        Assert.Same(BuiltInThemes.Custom, BuiltInThemes.Resolve("Custom"));
    }

    [Fact]
    public void ResolveInAppearance_RejectsThemesFromTheOtherAppearance()
    {
        // A light theme requested while the app is dark must fall back to a
        // dark theme, not tint the app light.
        var resolved = BuiltInThemes.ResolveInAppearance("Vex Light", dark: true);
        Assert.True(resolved.IsDark);
    }

    [Fact]
    public void ResolveInAppearance_KeepsMatchingThemes()
    {
        Assert.Equal("Vex Light", BuiltInThemes.ResolveInAppearance("Vex Light", dark: false).Name);
        Assert.Equal("One Dark", BuiltInThemes.ResolveInAppearance("One Dark", dark: true).Name);
    }

    [Fact]
    public void ResolveInAppearance_UnknownNameFallsBackWithinAppearance()
    {
        var light = BuiltInThemes.ResolveInAppearance("Nope", dark: false);
        var dark = BuiltInThemes.ResolveInAppearance("Nope", dark: true);

        Assert.False(light.IsDark);
        Assert.True(dark.IsDark);
    }

    [Fact]
    public void Clone_CopiesEveryColor()
    {
        var clone = BuiltInThemes.Clone(BuiltInThemes.OneDark);
        AssertThemeIsComplete(clone);
        Assert.Equal(BuiltInThemes.OneDark.Background, clone.Background);
        Assert.Equal(BuiltInThemes.OneDark.BrightCyan, clone.BrightCyan);
    }

    [Fact]
    public void CustomTheme_IsIndependentOfItsSource()
    {
        // Custom is seeded from Vex Dark; editing it must not mutate the
        // built-in card, which is what makes the theme editor safe.
        var original = BuiltInThemes.VexDark.Background;
        try
        {
            BuiltInThemes.Custom.Background = "#123456";
            Assert.Equal(original, BuiltInThemes.VexDark.Background);
        }
        finally
        {
            BuiltInThemes.Custom.Background = original;
        }
    }

    [Fact]
    public void IsDark_UsesPerceivedLuminance()
    {
        Assert.True(new TerminalTheme { Background = "#000000" }.IsDark);
        Assert.True(new TerminalTheme { Background = "#1E1E2E" }.IsDark);
        Assert.False(new TerminalTheme { Background = "#FFFFFF" }.IsDark);
        Assert.False(new TerminalTheme { Background = "#F7F8FA" }.IsDark);
    }

    [Fact]
    public void TerminalPalette_BackgroundIsOpaqueForDwmComposition()
    {
        var palette = new TerminalPalette(BuiltInThemes.VexDark);
        var background = Assert.IsType<System.Windows.Media.SolidColorBrush>(palette.Background);

        Assert.Equal(byte.MaxValue, background.Color.A);
    }

    [Fact]
    public void FastColor_ParsesTheFormatsThemesUse()
    {
        Assert.Equal("#282C34", ToHex(FastColor.ParseHex("#282C34")));
        Assert.Equal("#282C34", ToHex(FastColor.ParseHex("282C34")));
        // Short form expands each nibble, matching CSS.
        Assert.Equal("#FFFFFF", ToHex(FastColor.ParseHex("#fff")));
        // The 8-digit form is #AARRGGBB, the XAML convention.
        Assert.Equal("#282C34", ToHex(FastColor.ParseHex("#FF282C34")));
    }

    private static void AssertThemeIsComplete(TerminalTheme theme)
    {
        Assert.False(string.IsNullOrWhiteSpace(theme.Name));
        foreach (var color in new[]
        {
            theme.Background, theme.Foreground, theme.Cursor, theme.SelectionBackground,
            theme.Black, theme.Red, theme.Green, theme.Yellow,
            theme.Blue, theme.Magenta, theme.Cyan, theme.White,
            theme.BrightBlack, theme.BrightRed, theme.BrightGreen, theme.BrightYellow,
            theme.BrightBlue, theme.BrightMagenta, theme.BrightCyan, theme.BrightWhite,
        })
        {
            Assert.StartsWith("#", color);
            Assert.Equal(7, color.Length);
        }
    }

    private static string ToHex(System.Windows.Media.Color color)
        => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
