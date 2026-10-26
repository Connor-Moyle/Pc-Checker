using System.Globalization;

namespace PcChecker.Core.Lighting;

/// <summary>Pure colour helpers: hex parsing, HSV conversion, brightness scaling and rainbow palettes.</summary>
public static class RgbColorMath
{
    /// <summary>Parses "#RRGGBB", "RRGGBB", "#RGB" or "0xRRGGBB".</summary>
    public static bool TryParseHex(string? text, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        if (s.Length == 3)
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }

        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
        {
            return false;
        }

        color = new RgbColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>Hue 0–360, saturation 0–1, value 0–1 → RGB.</summary>
    public static RgbColor FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        double c = value * saturation;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = value - c;
        (double r, double g, double b) = (hue / 60) switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return new RgbColor(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    /// <summary>RGB → (hue 0–360, saturation 0–1, value 0–1).</summary>
    public static (double Hue, double Saturation, double Value) ToHsv(RgbColor color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                hue = 60 * ((b - r) / delta + 2);
            }
            else
            {
                hue = 60 * ((r - g) / delta + 4);
            }
        }

        if (hue < 0)
        {
            hue += 360;
        }

        double saturation = max == 0 ? 0 : delta / max;
        return (hue, saturation, max);
    }

    /// <summary>Scales a colour to <paramref name="brightnessPercent"/> (0–100) of its current intensity.</summary>
    public static RgbColor Scale(RgbColor color, int brightnessPercent)
    {
        double f = Math.Clamp(brightnessPercent, 0, 100) / 100.0;
        return new RgbColor(ToByte(color.R / 255.0 * f), ToByte(color.G / 255.0 * f), ToByte(color.B / 255.0 * f));
    }

    /// <summary>Evenly spaced hues around the colour wheel, starting at red.</summary>
    public static IReadOnlyList<RgbColor> Rainbow(int count, double saturation = 1, double value = 1)
    {
        if (count <= 0)
        {
            return [];
        }

        var list = new List<RgbColor>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(FromHsv(360.0 * i / count, saturation, value));
        }

        return list;
    }

    private static byte ToByte(double unit) => (byte)Math.Clamp((int)Math.Round(unit * 255), 0, 255);
}
