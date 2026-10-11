using System.Windows.Media;

namespace Vex.App.Terminal.Native;

/// <summary>Repairs terminal text contrast by moving perceptual lightness, preserving hue and readable colors.</summary>
internal static class TerminalContrastCorrection
{
    private readonly record struct Oklab(double Lightness, double A, double B);

    internal static double ContrastRatio(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    internal static Color CorrectForeground(Color foreground, Color background, double minimumContrast)
    {
        if (ContrastRatio(foreground, background) >= minimumContrast)
            return foreground;

        var lab = ToOklab(foreground);
        var darker = FindReadableColor(lab, background, minimumContrast, 0);
        var lighter = FindReadableColor(lab, background, minimumContrast, 1);
        if (darker is null) return lighter ?? foreground;
        if (lighter is null) return darker.Value;
        return Math.Abs(ToOklab(darker.Value).Lightness - lab.Lightness)
            <= Math.Abs(ToOklab(lighter.Value).Lightness - lab.Lightness) ? darker.Value : lighter.Value;
    }

    private static Color? FindReadableColor(Oklab source, Color background, double contrast, double endpoint)
    {
        var readable = FromOklab(new Oklab(endpoint, 0, 0));
        if (ContrastRatio(readable, background) < contrast)
            return null;
        var failingLightness = source.Lightness;
        var passingLightness = endpoint;
        for (var step = 0; step < 14; step++)
        {
            var lightness = (failingLightness + passingLightness) / 2;
            var candidate = MapToGamut(source with { Lightness = lightness });
            // Include a small margin for conversion to 8-bit terminal colors.
            if (ContrastRatio(candidate, background) >= contrast + 0.02)
            {
                readable = candidate;
                passingLightness = lightness;
            }
            else
                failingLightness = lightness;
        }
        return readable;
    }

    private static Color MapToGamut(Oklab source)
    {
        if (InGamut(source)) return FromOklab(source);
        var low = 0.0;
        var high = 1.0;
        for (var step = 0; step < 10; step++)
        {
            var scale = (low + high) / 2;
            if (InGamut(source with { A = source.A * scale, B = source.B * scale })) low = scale;
            else high = scale;
        }
        return FromOklab(source with { A = source.A * low, B = source.B * low });
    }

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static Oklab ToOklab(Color color)
    {
        var r = Linear(color.R);
        var g = Linear(color.G);
        var b = Linear(color.B);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return new Oklab(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static (double R, double G, double B) ToLinearRgb(Oklab color)
    {
        var l = color.Lightness + 0.3963377774 * color.A + 0.2158037573 * color.B;
        var m = color.Lightness - 0.1055613458 * color.A - 0.0638541728 * color.B;
        var s = color.Lightness - 0.0894841775 * color.A - 1.2914855480 * color.B;
        l *= l * l;
        m *= m * m;
        s *= s * s;
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    private static bool InGamut(Oklab color)
    {
        var (r, g, b) = ToLinearRgb(color);
        return r is >= 0 and <= 1 && g is >= 0 and <= 1 && b is >= 0 and <= 1;
    }

    private static Color FromOklab(Oklab color)
    {
        static byte Channel(double value)
        {
            value = Math.Clamp(value, 0, 1);
            var srgb = value <= 0.0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
            return (byte)Math.Clamp(Math.Round(srgb * 255), 0, 255);
        }
        var (r, g, b) = ToLinearRgb(color);
        return Color.FromRgb(Channel(r), Channel(g), Channel(b));
    }
}
