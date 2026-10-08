using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class TabIconStackTests
{
    [Fact]
    public void IconStack_BindingUpdatesLayoutAndRendersAcrossThemes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var tab = new WorkspaceTab("Terminal", @"C:\work");
                var first = Assert.IsType<TerminalPane>(tab.ActiveLeaf);
                var stack = new TabIconStack { DataContext = tab };
                stack.SetBinding(TabIconStack.IconsProperty, new Binding(nameof(WorkspaceTab.TabIcons)));
                stack.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "VexSurface");
                stack.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "VexBorder");

                foreach (var theme in new[] { BuiltInThemes.VexDark, BuiltInThemes.VexLight })
                {
                    ChromePalette.Apply(theme, stack.Resources);
                    first.AppIcon = AppIconCatalog.ResolveIcon("codex", theme.IsDark);
                    stack.Measure(new Size(200, 40));
                    var singleWidth = stack.DesiredSize.Width;
                    tab.Split(System.Windows.Controls.Orientation.Horizontal);
                    var second = Assert.IsType<TerminalPane>(tab.ActiveLeaf);
                    second.AppIcon = AppIconCatalog.ResolveIcon("claude", theme.IsDark);
                    stack.Measure(new Size(200, 40));
                    Assert.True(stack.DesiredSize.Width > singleWidth);
                    stack.Arrange(new Rect(stack.DesiredSize));

                    var bitmap = new RenderTargetBitmap(120, 88, 384, 384, PixelFormats.Pbgra32);
                    bitmap.Render(stack);
                    var pixels = new byte[120 * 88 * 4];
                    bitmap.CopyPixels(pixels, 120 * 4, 0);
                    Assert.Contains(pixels, value => value != 0);

                    second.Close();
                    stack.Measure(new Size(200, 40));
                    Assert.Equal(singleWidth, stack.DesiredSize.Width);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Tab icon stack rendering did not finish.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
