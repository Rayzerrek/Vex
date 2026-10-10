using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class ChromeThemeResourceTests
{
    [Fact]
    public void ProjectChrome_DarkLightDark_UpdatesAfterBrushesAreFrozen()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = ReadThemeXaml("MainWindow.xaml");
                var projectName = window.Descendants().Single(e =>
                    e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{Binding SelectedProject.Name}");
                var projectPopup = window.Descendants().Single(e =>
                    (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ProjectPickerPopup");
                var projectBorder = projectPopup.Elements().Single();
                var resources = new ResourceDictionary();
                ChromePalette.Apply(BuiltInThemes.VexDark, resources);

                // Use the production resource expressions, but no workspace or terminal processes.
                var grid = (Grid)XamlReader.Parse($"""
                    <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          Background="{window.Root!.Attribute("Background")!.Value}">
                        <Grid.Resources>{XamlWriter.Save(resources)}</Grid.Resources>
                        <Border Background="{projectBorder.Attribute("Background")!.Value}">
                            <TextBlock Foreground="{projectName.Attribute("Foreground")!.Value}"/>
                        </Border>
                    </Grid>
                    """);
                var border = (Border)grid.Children[0];
                var text = (TextBlock)border.Child;

                foreach (var theme in new[] { BuiltInThemes.VexLight, BuiltInThemes.VexDark, BuiltInThemes.OneLight, BuiltInThemes.OneDark })
                {
                    ChromePalette.Apply(theme, grid.Resources);
                    Assert.Equal(grid.Resources["VexTextColor"], ((SolidColorBrush)text.Foreground).Color);
                    Assert.Equal(grid.Resources["VexSurfaceColor"], ((SolidColorBrush)border.Background).Color);
                    var background = Assert.IsType<SolidColorBrush>(grid.Background);
                    Assert.Equal(grid.Resources["VexBackgroundColor"], background.Color);
                    var selectedTab = Assert.IsType<LinearGradientBrush>(grid.Resources["VexTabSelected"]);
                    Assert.Equal(grid.Resources["VexTabSelectedStartColor"], selectedTab.GradientStops[0].Color);
                    Assert.Equal(grid.Resources["VexTabSelectedEndColor"], selectedTab.GradientStops[^1].Color);

                    // Style/template sealing can freeze shared brushes between appearance changes.
                    foreach (var brush in grid.Resources.Values.OfType<Brush>())
                        brush.Freeze();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Chrome theme resource test did not finish.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void ChromeGradients_AreLimitedToTheSelectedTab()
    {
        var app = ReadThemeXaml("App.xaml");
        var gradient = Assert.Single(app.Descendants(), e => e.Name.LocalName == "LinearGradientBrush");
        Assert.Equal("VexTabSelected", (string?)gradient.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")));
        Assert.DoesNotContain(ReadThemeXaml("SettingsOverlay.xaml").Descendants(), e => e.Name.LocalName == "LinearGradientBrush");
    }

    [Fact]
    public void ThemeBrushConsumers_UseDynamicResources()
    {
        var app = ReadThemeXaml("App.xaml");
        var brushKeys = app.Descendants()
            .Where(e => e.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush")
            .Select(e => (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")))
            .OfType<string>()
            .ToHashSet();
        var assembly = typeof(ChromeThemeResourceTests).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".xaml")))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var document = XDocument.Load(stream);
            foreach (var attribute in document.Descendants().Attributes())
            {
                var value = attribute.Value;
                if (!value.StartsWith("{StaticResource ") || !value.EndsWith('}'))
                    continue;
                Assert.False(brushKeys.Contains(value[16..^1]),
                    $"{name}: {attribute.Parent!.Name.LocalName}.{attribute.Name} captures a replaceable theme brush: {value}");
            }
        }
    }

    private static XDocument ReadThemeXaml(string filename)
    {
        var assembly = typeof(ChromeThemeResourceTests).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith($".ThemeXaml.{filename}"));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return XDocument.Load(stream);
    }
}
