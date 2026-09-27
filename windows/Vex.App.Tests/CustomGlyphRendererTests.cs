using System.Windows.Media;
using Vex.App.Terminal.Native;
using Xunit;

namespace Vex.App.Tests;

public sealed class CustomGlyphRendererTests
{
    [Theory]
    [InlineData(0x2588, true)] // Full block
    [InlineData(0x2580, true)] // Upper half block
    [InlineData(0x2584, true)] // Lower half block
    [InlineData(0x258C, true)] // Left half block
    [InlineData(0x2590, true)] // Right half block
    [InlineData(0x2591, true)] // Light shade
    [InlineData(0x2592, true)] // Medium shade
    [InlineData(0x2593, true)] // Dark shade
    [InlineData(0x2596, true)] // Quadrant lower left
    [InlineData(0xE0B0, true)] // Powerline right arrow
    [InlineData(0xE0B2, true)] // Powerline left arrow
    [InlineData(0xE0B4, true)] // Powerline right rounded
    [InlineData(0xE0B6, true)] // Powerline left rounded
    [InlineData(0x2500, true)] // Box drawing light horizontal
    [InlineData(0x2502, true)] // Box drawing light vertical
    [InlineData(0x250C, true)] // Box drawing light down and right
    [InlineData(0x2518, true)] // Box drawing light up and left
    [InlineData(0x250F, true)] // Heavy corner down and right
    [InlineData(0x2554, true)] // Double corner down and right
    [InlineData(0x2550, true)] // Double horizontal
    [InlineData(0x2551, true)] // Double vertical
    [InlineData(0x2573, true)] // Diagonal cross
    [InlineData(0x2504, false)] // Box drawing light triple dash horizontal
    [InlineData(0x2552, false)] // Box drawing mixed single/double
    [InlineData(0xE0B1, false)] // Powerline thin right arrow
    [InlineData(0xE0B3, false)] // Powerline thin left arrow
    [InlineData(0xE0B5, false)] // Powerline thin right rounded
    [InlineData(0xE0B7, false)] // Powerline thin left rounded
    [InlineData('A', false)]
    [InlineData(' ', false)]
    [InlineData('z', false)]
    [InlineData(0x1F600, false)] // Grinning face emoji
    public void CanDraw_MatchesExpectedCodepoints(int codepoint, bool expected)
    {
        Assert.Equal(expected, CustomGlyphRenderer.CanDraw(codepoint));
    }

    [Fact]
    public void Draw_BlockElement_ExecutesSuccessfully()
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        var brush = Brushes.Cyan;

        // Verify Draw returns true and produces visual drawing without throwing
        var drawn = CustomGlyphRenderer.Draw(dc, 0x2588, 10, 20, 9, 21, brush, 1.0);
        Assert.True(drawn);
    }

    [Fact]
    public void Draw_Powerline_ExecutesSuccessfully()
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        var brush = Brushes.Cyan;

        var drawn = CustomGlyphRenderer.Draw(dc, 0xE0B0, 10, 20, 9, 21, brush, 1.0);
        Assert.True(drawn);
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
            }
        }
    }

    [Theory]
    [InlineData(0x256D)] // ╭
    [InlineData(0x256E)] // ╮
    [InlineData(0x256F)] // ╯
    [InlineData(0x2570)] // ╰
    public void Draw_RoundedCorners_ExecuteSuccessfully(int codepoint)
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        var brush = Brushes.Cyan;

        Assert.True(CustomGlyphRenderer.CanDraw(codepoint));
        Assert.True(CustomGlyphRenderer.Draw(dc, codepoint, 0, 0, 10, 20, brush, 1.0));
    }

    [Theory]
    [InlineData(0x2554)] // ╔
    [InlineData(0x2557)] // ╗
    [InlineData(0x255A)] // ╚
    [InlineData(0x255D)] // ╝
    public void Draw_DoubleCorners_ExecuteSuccessfully(int codepoint)
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        var brush = Brushes.Cyan;

        Assert.True(CustomGlyphRenderer.CanDraw(codepoint));
        Assert.True(CustomGlyphRenderer.Draw(dc, codepoint, 0, 0, 10, 20, brush, 1.0));
    }
}
