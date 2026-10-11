using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Xunit;

namespace Vex.App.Tests;

public sealed class RoundedSurfaceTests
{
    [Theory]
    [InlineData("CommandPalette.xaml")]
    [InlineData("SettingsOverlay.xaml")]
    [InlineData("ThemeSwitcher.xaml")]
    [InlineData("TabPeek.xaml")]
    [InlineData("TabCloseOverlay.xaml")]
    [InlineData("SavedLayoutOverlay.xaml")]
    [InlineData("WslProjectOverlay.xaml")]
    public void OverlayPanels_UseTheSharedRoundedSurface(string filename)
    {
        var panel = ReadXaml(filename).Descendants().Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "Panel");
        Assert.Equal("RoundedSurface", panel.Name.LocalName);
        if (filename == "SettingsOverlay.xaml") Assert.Equal("16", (string?)panel.Attribute("CornerRadius"));
        else Assert.Null(panel.Attribute("CornerRadius"));
    }

    [Theory]
    [InlineData("MainWindow.xaml", "ProjectPickerTemplate")]
    [InlineData("MainWindow.xaml", "TabCloseTemplate")]
    [InlineData("MainWindow.xaml", "ShellChoiceContextMenu")]
    [InlineData("App.xaml", "DarkContextMenu")]
    [InlineData("App.xaml", "DarkMenuItem")]
    public void PopupTemplates_UseTheSharedRoundedSurface(string filename, string key)
    {
        var template = ReadXaml(filename).Descendants().Single(element =>
            (string?)element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == key);
        Assert.Contains(template.Descendants(), element => element.Name.LocalName == "RoundedSurface");
    }

    [Fact]
    public void SettingsDropdown_ClipsItsItemsInsideTheRoundedSurface()
    {
        var surface = ReadXaml("SettingsOverlay.xaml").Descendants().Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "DropDownBorder");
        Assert.Equal("RoundedSurface", surface.Name.LocalName);
        Assert.Equal("ScrollViewer", Assert.Single(surface.Elements()).Name.LocalName);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void MenuSurface_ClipsOpaqueChildrenWithAntialiasedCornersAtUiScales(double scale)
    {
        RunOnSta(() =>
        {
            var template = ReadXaml("App.xaml").Descendants().Single(element =>
                (string?)element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "DarkContextMenu");
            var surface = template.Descendants().Single(element => element.Name.LocalName == "ControlTemplate").Elements().Single();
            var tag = surface.Name.LocalName == "RoundedSurface" ? "local:RoundedSurface" : "Border";
            var radius = (string?)surface.Attribute("CornerRadius");
            var radiusAttribute = radius is null ? "" : $"CornerRadius=\"{radius}\"";
            var border = Assert.IsAssignableFrom<Border>(XamlReader.Parse($"""
                <{tag} xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                       xmlns:local="clr-namespace:Vex.App;assembly=Vex.App"
                       Background="White" {radiusAttribute}>
                    <Border Background="Black"/>
                </{tag}>
                """));
            Assert.Equal(new CornerRadius(12), border.CornerRadius);
            AssertCorners(border, 96, scale);
            border.CornerRadius = new CornerRadius(20);
            AssertCorners(border, 144, scale);
        });
    }

    [Fact]
    public void RoundedSurface_DoesNotDarkenItsAntialiasedOutlineTwice()
    {
        RunOnSta(() =>
        {
            var reference = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12) };
            var surface = new RoundedSurface { Background = Brushes.White };
            Assert.Equal(CaptureTopCorner(reference, 500, 400), CaptureTopCorner(surface, 500, 400));
        });
    }

    [Fact]
    public void RoundedSurface_FilterSizedHeightChangesKeepTheSameCornerShape()
    {
        RunOnSta(() =>
        {
            var surface = new RoundedSurface { Background = Brushes.White };
            var initial = CaptureTopCorner(surface, 500, 400);
            foreach (var height in new[] { 112, 56, 400, 180, 112, 400 })
                Assert.Equal(initial, CaptureTopCorner(surface, 500, height));
        });
    }

    private static byte[] CaptureTopCorner(Border surface, int width, int height)
    {
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var corner = new byte[24 * 24 * 4];
        bitmap.CopyPixels(new Int32Rect(0, 0, 24, 24), corner, 24 * 4, 0);
        return corner;
    }

    [Fact]
    public void RoundedSurface_PreservesTheOuterShadow()
    {
        RunOnSta(() =>
        {
            var surface = new RoundedSurface
            {
                Width = 64, Height = 64, Background = Brushes.White,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 12, ShadowDepth = 4, Opacity = 1, Color = Colors.Black,
                },
            };
            var host = new Grid { Width = 96, Height = 96 };
            host.Children.Add(surface);
            host.Measure(new Size(96, 96));
            host.Arrange(new Rect(0, 0, 96, 96));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var pixels = new byte[96 * 96 * 4];
            bitmap.CopyPixels(pixels, 96 * 4, 0);
            Assert.Contains(Enumerable.Range(0, 96), x => pixels[(82 * 96 + x) * 4 + 3] > 0);
        });
    }

    private static void AssertCorners(Border border, int logicalSize, double scale)
    {
        border.Measure(new Size(logicalSize, logicalSize));
        border.Arrange(new Rect(0, 0, logicalSize, logicalSize));
        border.UpdateLayout();
        var size = (int)(logicalSize * scale);
        var bitmap = new RenderTargetBitmap(size, size, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(border);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        byte Alpha(int x, int y) => pixels[(y * size + x) * 4 + 3];
        Assert.Equal(0, Alpha(0, 0));
        Assert.Equal(0, Alpha(size - 1, 0));
        Assert.Equal(0, Alpha(0, size - 1));
        Assert.Equal(0, Alpha(size - 1, size - 1));
        Assert.Equal(255, Alpha(size / 2, size / 2));
        var radius = (int)Math.Ceiling(border.CornerRadius.TopLeft * scale);
        Assert.Contains(Enumerable.Range(0, radius * radius), index => Alpha(index % radius, index / radius) is > 0 and < 255);
    }

    private static XDocument ReadXaml(string filename)
    {
        var assembly = typeof(RoundedSurfaceTests).Assembly;
        var name = assembly.GetManifestResourceNames().Single(name => name.EndsWith($".ThemeXaml.{filename}"));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing embedded XAML: " + filename);
        return XDocument.Load(stream);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Rounded surface rendering did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
