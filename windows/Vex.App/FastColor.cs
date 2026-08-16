using System.Windows.Media;

namespace Vex.App;

/// <summary>
/// High-performance zero-allocation hex color parser to replace slow reflection-based
/// ColorConverter.ConvertFromString across theme and palette loading.
/// </summary>
internal static class FastColor
{
    public static Color ParseHex(ReadOnlySpan<char> hex, byte alpha = 0xFF)
    {
        if (hex.StartsWith("#"))
            hex = hex[1..];

        if (hex.Length == 6)
        {
            var r = ParseByte(hex[..2]);
            var g = ParseByte(hex.Slice(2, 2));
            var b = ParseByte(hex.Slice(4, 2));
            return Color.FromArgb(alpha, r, g, b);
        }
        if (hex.Length == 8)
        {
            var a = ParseByte(hex[..2]);
            var r = ParseByte(hex.Slice(2, 2));
            var g = ParseByte(hex.Slice(4, 2));
            var b = ParseByte(hex.Slice(6, 2));
            return Color.FromArgb(alpha == 0xFF ? a : alpha, r, g, b);
        }
        if (hex.Length == 3)
        {
            var r = (byte)(HexVal(hex[0]) * 17);
            var g = (byte)(HexVal(hex[1]) * 17);
            var b = (byte)(HexVal(hex[2]) * 17);
            return Color.FromArgb(alpha, r, g, b);
        }
        return Colors.Transparent;
    }

    private static byte ParseByte(ReadOnlySpan<char> span) =>
        (byte)((HexVal(span[0]) << 4) | HexVal(span[1]));

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0
    };
}
