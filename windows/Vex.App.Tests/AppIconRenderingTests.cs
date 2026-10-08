using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class AppIconRenderingTests
{
    [Theory]
    [InlineData("nushell")]
    [InlineData("anthropic")]
    [InlineData("powershell")]
    [InlineData("linux")]
    [InlineData("codex")]
    [InlineData("antigravity")]
    public void GlyphImage_PreservesSquareArtboard(string slug)
    {
        foreach (var isDark in new[] { true, false })
        {
            var image = AppIcon.Glyph(slug, isDark).Image;
            Assert.Equal(16, image.Width, 6);
            Assert.Equal(16, image.Height, 6);
            Assert.True(image.IsFrozen);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void NushellIcon_SingleAndStackedTabs_PreserveWideLogo(int count)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var stack = new TabIconStack
                {
                    Icons = Enumerable.Repeat(AppIcon.Glyph("nushell", isDark: true), count).ToArray(),
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                };
                stack.Measure(new Size(100, 40));
                stack.Arrange(new Rect(stack.DesiredSize));
                var width = (int)stack.DesiredSize.Width;
                var height = (int)stack.DesiredSize.Height;
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(stack);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                var paintedRows = Enumerable.Range(0, height)
                    .Where(y => Enumerable.Range(0, width).Any(x => pixels[(y * width + x) * 4 + 3] != 0))
                    .ToArray();
                Assert.NotEmpty(paintedRows);
                Assert.InRange(paintedRows[^1] - paintedRows[0] + 1, 5, 8);
                Assert.InRange(paintedRows[0], count == 1 ? 4 : 7, count == 1 ? 6 : 9);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Nushell icon rendering did not finish.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
