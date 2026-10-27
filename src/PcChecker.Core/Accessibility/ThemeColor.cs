using System.Globalization;

namespace PcChecker.Core.Accessibility;

/// <summary>
/// A colour as the interface uses it: alpha plus red, green and blue, written as #RRGGBB or #AARRGGBB like XAML.
/// Kept free of WPF so the palette and its contrast checks can be tested anywhere.
/// </summary>
public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    /// <summary>An opaque colour.</summary>
    public static ThemeColor Rgb(byte r, byte g, byte b) => new(255, r, g, b);

    public bool IsOpaque => A == 255;

    /// <summary>Parses #RGB, #RRGGBB or #AARRGGBB (the leading # is optional).</summary>
    public static ThemeColor Parse(string hex) =>
        TryParse(hex, out var color) ? color : throw new FormatException($"'{hex}' is not a colour in #RRGGBB or #AARRGGBB form.");

    public static bool TryParse(string? hex, out ThemeColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var digits = hex.Trim().TrimStart('#');
        if (digits.Length == 3)
        {
            digits = string.Concat(digits.Select(c => new string(c, 2)));
        }

        if (digits.Length == 6)
        {
            digits = "FF" + digits;
        }

        if (digits.Length != 8 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = new ThemeColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>The colour seen when this (possibly see-through) colour is drawn over an opaque <paramref name="background"/>.</summary>
    public ThemeColor Over(ThemeColor background)
    {
        if (IsOpaque)
        {
            return this;
        }

        double alpha = A / 255.0;
        byte Mix(byte top, byte bottom) => (byte)Math.Round(top * alpha + bottom * (1 - alpha), MidpointRounding.AwayFromZero);
        return Rgb(Mix(R, background.R), Mix(G, background.G), Mix(B, background.B));
    }

    /// <summary>#RRGGBB for opaque colours, #AARRGGBB otherwise.</summary>
    public string ToHex() => IsOpaque
        ? string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{A:X2}{R:X2}{G:X2}{B:X2}");

    public override string ToString() => ToHex();
}
