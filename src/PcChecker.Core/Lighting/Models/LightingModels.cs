namespace PcChecker.Core.Lighting;

/// <summary>An RGB colour. Serializes as three bytes; use <see cref="ToHex"/> for display.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black => new(0, 0, 0);
    public static RgbColor White => new(255, 255, 255);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Parses "#RRGGBB", "RRGGBB" or "#RGB". Throws on invalid input; see <see cref="RgbColorMath.TryParseHex"/>.</summary>
    public static RgbColor FromHex(string hex) =>
        RgbColorMath.TryParseHex(hex, out var c) ? c : throw new FormatException($"\"{hex}\" is not a colour in #RRGGBB form.");

    public override string ToString() => ToHex();
}

public sealed record RgbZone(int Index, string Name, int LedCount);

public sealed record RgbMode(int Index, string Name, bool HasColor, bool HasSpeed, bool HasBrightness, int? MinSpeed, int? MaxSpeed);

public sealed record RgbDevice(
    int Index,
    string Name,
    string Vendor,
    string Type,
    string? Serial,
    int LedCount,
    IReadOnlyList<RgbZone> Zones,
    IReadOnlyList<RgbMode> Modes,
    int ActiveModeIndex,
    RgbColor? Color);

/// <summary>What a profile sets on one device.</summary>
public sealed class LightingAssignment
{
    public string? ModeName { get; set; }
    public RgbColor? Color { get; set; }
    public int? Brightness { get; set; }
    public int? Speed { get; set; }
}

/// <summary>Named lighting setup keyed by device name.</summary>
public sealed class LightingProfile
{
    public string Name { get; set; } = string.Empty;
    public RgbColor? AllColor { get; set; }
    public Dictionary<string, LightingAssignment> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsBuiltIn { get; set; }
}

/// <summary>Lighting settings stored in <c>AppSettings.Lighting</c>.</summary>
public sealed class LightingSettings
{
    public string OpenRgbHost { get; set; } = "127.0.0.1";
    public int OpenRgbPort { get; set; } = 6742;
    public bool AutoConnect { get; set; } = true;
    public List<LightingProfile> Profiles { get; set; } = [];
    public string? ActiveProfileName { get; set; }
}
