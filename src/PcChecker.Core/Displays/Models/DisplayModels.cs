namespace PcChecker.Core.Displays;

public sealed record DisplayMode(int Width, int Height, int RefreshHz, int BitsPerPixel)
{
    public override string ToString() => $"{Width} x {Height} @ {RefreshHz} Hz";
}

public sealed record DisplayInfo(
    string DeviceName,
    string FriendlyName,
    string? Manufacturer,
    string? Serial,
    bool IsPrimary,
    DisplayMode Current,
    double ScalePercent,
    int PositionX,
    int PositionY,
    int OrientationDegrees,
    bool HdrSupported,
    bool HdrEnabled,
    string? ConnectionType,
    double? DiagonalInches,
    string? AdapterName,
    IReadOnlyList<DisplayMode> Modes,
    bool SupportsDdc,
    int? Brightness,
    int? Contrast,
    bool IsInternal);

/// <summary>A saved arrangement: which mode each display uses and which one is primary.</summary>
public sealed class DisplayProfile
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, DisplayMode> Modes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? PrimaryDevice { get; set; }
}

/// <summary>Display settings stored in <c>AppSettings.Displays</c>.</summary>
public sealed class DisplaySettings
{
    public List<DisplayProfile> Profiles { get; set; } = [];
}
