using System.Windows.Input;
using Vex.App.Terminal.Native;
using Vex.Libghostty;
using Xunit;

namespace Vex.App.Tests;

public sealed class TerminalKeyMapTests
{
    [Theory]
    [InlineData(Key.A, TerminalKey.A)]
    [InlineData(Key.Z, TerminalKey.Z)]
    [InlineData(Key.D0, TerminalKey.Digit0)]
    [InlineData(Key.D9, TerminalKey.Digit9)]
    [InlineData(Key.NumPad4, TerminalKey.Numpad4)]
    [InlineData(Key.F1, TerminalKey.F1)]
    [InlineData(Key.F24, TerminalKey.F24)]
    [InlineData(Key.Up, TerminalKey.ArrowUp)]
    [InlineData(Key.Oem4, TerminalKey.BracketLeft)]
    public void PhysicalKeys_MapToGhosttyIdentity(Key key, TerminalKey expected)
    {
        Assert.True(TerminalKeyMap.TryMap(key, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Modifiers_MapWithoutChangingKittyBitPositions()
    {
        var actual = TerminalKeyMap.MapModifiers(
            ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows);

        Assert.Equal(
            TerminalKeyModifiers.Shift | TerminalKeyModifiers.Control |
            TerminalKeyModifiers.Alt | TerminalKeyModifiers.Super,
            actual);
    }

    [Fact]
    public void TextCandidates_AreSeparatedFromFunctionalKeys()
    {
        Assert.True(TerminalKeyMap.IsTextKey(Key.A));
        Assert.True(TerminalKeyMap.IsTextKey(Key.OemQuotes));
        Assert.True(TerminalKeyMap.IsTextKey(Key.Space));
        Assert.False(TerminalKeyMap.IsTextKey(Key.Enter));
        Assert.False(TerminalKeyMap.IsTextKey(Key.Up));
    }

    [Fact]
    public void UnshiftedCodepoint_IsOnlyInferredWhenLayoutIndependent()
    {
        Assert.Equal((uint)'a', TerminalKeyMap.UnshiftedCodepoint(Key.A));
        Assert.Equal((uint)'7', TerminalKeyMap.UnshiftedCodepoint(Key.D7));
        Assert.Equal((uint)' ', TerminalKeyMap.UnshiftedCodepoint(Key.Space));
        Assert.Equal(0u, TerminalKeyMap.UnshiftedCodepoint(Key.Oem1));
    }

    [Fact]
    public void RowHash_DistinguishesWideTailFromBlankCell()
    {
        var blank = new FrameRow { Cells = new[] { new CellInfo { Text = "" } } };
        var tail = new FrameRow { Cells = new[] { new CellInfo { Text = "", Tail = true } } };

        Assert.NotEqual(
            NativeTerminalControl.RowHash(blank, 1),
            NativeTerminalControl.RowHash(tail, 1));
    }

    [Fact]
    public void RowHash_DistinguishesDefaultFromAnsiPaletteColor()
    {
        var defaultColor = new FrameRow { Cells = new[] { new CellInfo { Text = "x" } } };
        var ansiRed = new FrameRow
        {
            Cells = new[] { new CellInfo { Text = "x", FgTag = ColorTag.Palette, FgValue = 1 } },
        };

        Assert.NotEqual(
            NativeTerminalControl.RowHash(defaultColor, 1),
            NativeTerminalControl.RowHash(ansiRed, 1));
    }
}
