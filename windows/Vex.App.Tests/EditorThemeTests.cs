using System.Windows.Media;
using Vex.App;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

/// <summary>
/// The file editor paints its own surface, gutter and syntax palette on top of
/// the chrome. All three must follow the active theme — the same rule
/// ChromePalette uses for its chrome variant — never the appearance setting on
/// its own: a light surface carrying One Dark rules renders washed-out "ghost"
/// code, and a translucent surface lets DWM keep stale glyph tiles, the
/// artifacts that used to clear only when a selection repainted the line.
/// </summary>
[Collection("CustomTheme")]
public sealed class EditorThemeTests
{
    [Fact]
    public void Editors_UseTheSyntaxPaletteOfTheSurfaceTheyPaintOn()
    {
        foreach (var theme in BuiltInThemes.All)
        {
            var dark = EditorPane.IsDarkFor(theme.Name);
            Assert.Equal(theme.IsDark, dark);

            var definition = EditorHighlighting.ForExtension(".cs", dark);
            Assert.NotNull(definition);
            Assert.StartsWith(dark ? "OneDark" : "OneLight", definition!.Name);
        }
    }

    [Fact]
    public void Editors_ResolveBothVariantsForEveryMappedExtension()
    {
        string[] extensions =
        [
            ".js", ".ts", ".tsx", ".json", ".cs", ".c", ".cpp", ".go", ".rs",
            ".swift", ".kt", ".py", ".sh", ".xml", ".xaml", ".yml", ".md",
        ];

        foreach (var extension in extensions)
        {
            var dark = EditorHighlighting.ForExtension(extension, dark: true);
            var light = EditorHighlighting.ForExtension(extension, dark: false);

            Assert.NotNull(dark);
            Assert.NotNull(light);
            Assert.StartsWith("OneDark", dark!.Name);
            Assert.StartsWith("OneLight", light!.Name);
            Assert.NotSame(dark, light);
        }

        // Unmapped extensions still render as plain text.
        Assert.Null(EditorHighlighting.ForExtension(".txt", dark: true));
        Assert.Null(EditorHighlighting.ForExtension(".ps1", dark: false));
    }

    [Fact]
    public void EditorSurface_IsOpaqueForDwmComposition()
    {
        foreach (var theme in BuiltInThemes.All)
            Assert.Equal(byte.MaxValue, EditorPane.SurfaceColorFor(theme.Name).A);

        // A custom theme may be handed a translucent background; the editor
        // surface has to stay opaque anyway (see TerminalPalette).
        var original = BuiltInThemes.Custom.Background;
        try
        {
            BuiltInThemes.Custom.Background = "#80FFFFFF";
            Assert.Equal(Colors.White, EditorPane.SurfaceColorFor("Custom"));
        }
        finally
        {
            BuiltInThemes.Custom.Background = original;
        }
    }

    [Fact]
    public void GutterNumbers_StayLegibleOnTheEditorSurface()
    {
        foreach (var theme in BuiltInThemes.All)
        {
            var surface = EditorPane.SurfaceColorFor(theme.Name);
            var gutter = EditorPane.GutterColorFor(theme.Name);
            var ratio = ContrastRatio(gutter, surface);

            Assert.NotEqual(surface, gutter);
            Assert.True(ratio >= 3.0, $"{theme.Name}: gutter contrast is only {ratio:F2}:1");
        }
    }

    [Fact]
    public void SyntaxPalette_FollowsACustomThemeEditedToTheOtherVariant()
    {
        // Editing "Custom" to a light background re-tints the chrome light
        // while the app's Appearance setting still says dark; the editor has to
        // follow the theme, or the code turns into pale ghosts on the new
        // surface.
        var original = BuiltInThemes.Custom.Background;
        try
        {
            BuiltInThemes.Custom.Background = "#FFFFFF";
            Assert.False(EditorPane.IsDarkFor("Custom"));
            Assert.StartsWith("OneLight",
                EditorHighlighting.ForExtension(".cs", EditorPane.IsDarkFor("Custom"))!.Name);

            BuiltInThemes.Custom.Background = "#101014";
            Assert.True(EditorPane.IsDarkFor("Custom"));
            Assert.StartsWith("OneDark",
                EditorHighlighting.ForExtension(".cs", EditorPane.IsDarkFor("Custom"))!.Name);
        }
        finally
        {
            BuiltInThemes.Custom.Background = original;
        }
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var first = RelativeLuminance(a);
        var second = RelativeLuminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);

        static double RelativeLuminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
    }
}
