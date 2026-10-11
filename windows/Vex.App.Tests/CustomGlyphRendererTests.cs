using System.Windows.Media;
using Vex.App.Terminal.Native;
using Xunit;

namespace Vex.App.Tests;

public sealed class CustomGlyphRendererTests
{
    [Fact]
    public void CanDraw_RecognizesSupportedGlyphsAndRejectsFontFallbacks()
    {
        int[] supported =
        [
            0x2588, 0x2580, 0x2584, 0x258C, 0x2590, 0x2591, 0x2592, 0x2593, 0x2596,
            0xE0B0, 0xE0B2, 0xE0B4, 0xE0B6,
            0x2500, 0x2502, 0x250C, 0x2518, 0x250F, 0x2550, 0x2551, 0x2573,
            0x2554, 0x2557, 0x255A, 0x255D, 0x256D, 0x256E, 0x256F, 0x2570,
        ];
        int[] fontFallbacks = [0x2504, 0x2552, 0xE0B1, 0xE0B3, 0xE0B5, 0xE0B7, 'A', ' ', 'z', 0x1F600];

        Assert.All(supported, cp => Assert.True(CustomGlyphRenderer.CanDraw(cp), $"Expected custom rendering for U+{cp:X4}."));
        Assert.All(fontFallbacks, cp => Assert.False(CustomGlyphRenderer.CanDraw(cp), $"Expected font fallback for U+{cp:X4}."));
    }

    [Fact]
    public void CanDraw_True_GuaranteesDrawSucceeds()
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        var brush = Brushes.Cyan;

        for (var cp = 0; cp <= 0xFFFF; cp++)
        {
            if (CustomGlyphRenderer.CanDraw(cp))
            {
                var drawn = CustomGlyphRenderer.Draw(dc, cp, 10, 20, 9, 21, brush, 1.0);
                Assert.True(drawn, $"CustomGlyphRenderer claimed CanDraw(0x{cp:X4}) but Draw returned false.");
                Assert.True(CustomGlyphRenderer.Draw(dc, cp, 0, 0, 10, 20, brush, 1.0),
                    $"CustomGlyphRenderer failed to draw 0x{cp:X4} at the cell origin.");
            }
        }
    }
}
