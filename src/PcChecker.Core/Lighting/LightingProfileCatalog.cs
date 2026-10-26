namespace PcChecker.Core.Lighting;

/// <summary>Built-in lighting profiles shared by the real and demo services.</summary>
public static class LightingProfileCatalog
{
    public static IReadOnlyList<LightingProfile> BuiltIn { get; } =
    [
        new() { Name = "Gaming (blue/cyan)", AllColor = new RgbColor(0x4C, 0xC2, 0xFF), IsBuiltIn = true },
        new() { Name = "Work (warm white, 40%)", AllColor = RgbColorMath.Scale(new RgbColor(0xFF, 0xE4, 0xC4), 40), IsBuiltIn = true },
        new() { Name = "Night (off)", AllColor = RgbColor.Black, IsBuiltIn = true },
        new() { Name = "Rainbow", AllColor = null, IsBuiltIn = true, Devices = new(StringComparer.OrdinalIgnoreCase) { ["*"] = new LightingAssignment { ModeName = "Rainbow" } } },
    ];
}
