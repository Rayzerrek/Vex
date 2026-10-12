using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class AppIconRenderingTests
{
    [Fact]
    public void ShellIcons_ShareTerminalArtworkAndKeepApplicationIconsDistinct()
    {
        foreach (var dark in new[] { true, false })
        {
            var terminal = AppIcon.TerminalShellIcon(dark);
            foreach (var name in new[] { "cmd", "wsl", "nu", "nushell", "pwsh", "powershell", "bash", "sh", "fish", "zsh", "dash", "ksh", "ash", "gitbash", "system", "PWSH" })
                Assert.Same(terminal, AppIconCatalog.ResolveIcon(name, dark));
            Assert.True(terminal.IsShellIcon);
            Assert.True(terminal.Image.IsFrozen);
            Assert.Equal(16, terminal.Image.Width);
            Assert.Equal(16, terminal.Image.Height);
            Assert.NotSame(terminal, AppIconCatalog.ResolveIcon("nvim", dark));
        }
        foreach (var shell in new[] { "cmd", "nu", "wsl", "wsl:/run/current-system/sw/bin/nu", "custom-shell" })
        {
            using var pane = new TerminalPane(@"C:\work", shellId: shell);
            Assert.Same(AppIcon.TerminalShellIcon(), pane.AppIcon);
        }
    }

    [Fact]
    public void CatalogIcons_PreserveArtboardAndRenderVisiblePixelsAtUiScales()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var names = AppIconCatalog.GlyphSlugs.Concat(new[] { "wsl", "cmd", "lazygit", "btop", "ssh", "aider", "nano", "micro", "emacs" });
                foreach (var name in names)
                foreach (var isDark in new[] { true, false })
                {
                    var contextLabel = $"Icon {name} in {(isDark ? "dark" : "light")} appearance";
                    var icon = Assert.IsType<AppIcon>(AppIconCatalog.ResolveIcon(name, isDark));
                    if (AppIconCatalog.GlyphSlugs.Contains(name))
                    {
                        Assert.True(AppIconCatalog.GeometryFor(name, isDark) is not null, $"{contextLabel} has no geometry.");
                        Assert.True(Math.Abs(icon.Image.Width - 16) < 0.000001 && Math.Abs(icon.Image.Height - 16) < 0.000001,
                            $"{contextLabel} must preserve a 16×16 artboard, got {icon.Image.Width}×{icon.Image.Height}.");
                    }
                    Assert.True(icon.Image.IsFrozen, $"{contextLabel} is not frozen.");
                    var visual = new DrawingVisual();
                    using (var context = visual.RenderOpen())
                        context.DrawImage(icon.Image, new Rect(0, 0, 16, 16));
                    foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                    {
                        var size = (int)(16 * scale);
                        var bitmap = new RenderTargetBitmap(size, size, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(visual);
                        var pixels = new byte[size * size * 4];
                        bitmap.CopyPixels(pixels, size * 4, 0);
                        Assert.True(Enumerable.Range(0, size * size).Any(pixel => pixels[pixel * 4 + 3] > 0),
                            $"{contextLabel} is empty at {scale * 100}%.");
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Catalog icon rendering did not finish.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TerminalShellIcon_SingleAndStackedTabs_RenderPromptGlyph(int count)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var stack = new TabIconStack
                {
                    Icons = Enumerable.Repeat(AppIcon.TerminalShellIcon(isDark: true), count).ToArray(),
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
                Assert.InRange(paintedRows[^1] - paintedRows[0] + 1, 8, 12);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Terminal shell icon rendering did not finish.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
