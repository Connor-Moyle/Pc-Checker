namespace PcChecker.Core.Overclocking;

/// <summary>Built-in starting points. Users save their own profiles next to these in settings.</summary>
public static class OverclockPresets
{
    public const string DefaultName = "Default (0 / 0)";
    public const string MildName = "Mild (+50 / +200)";
    public const string ModerateName = "Moderate (+100 / +500)";
    public const string QuietName = "Quiet and cool (power 85 %)";

    public static IReadOnlyList<OverclockProfile> All { get; } =
    [
        new() { Name = DefaultName, Notes = "Stock clocks. Use this to undo everything." },
        new() { Name = MildName, GpuCoreOffsetMhz = 50, GpuMemoryOffsetMhz = 200, Notes = "A small bump almost every card handles." },
        new() { Name = ModerateName, GpuCoreOffsetMhz = 100, GpuMemoryOffsetMhz = 500, GpuPowerLimitPercent = 105, Notes = "Needs a short stress test; back off if you see artifacts." },
        new() { Name = QuietName, GpuPowerLimitPercent = 85, GpuTempLimitC = 75, Notes = "Lower power and temperature targets for a quieter PC." },
    ];

    public static bool IsPreset(string? name) => name is not null && All.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a copy of the preset (presets are never edited in place).</summary>
    public static OverclockProfile? Get(string? name) => All.FirstOrDefault(p => p.Name.Equals(name ?? string.Empty, StringComparison.OrdinalIgnoreCase))?.Clone();

    /// <summary>Lists what a profile changes, in plain English, for confirmation dialogs.</summary>
    public static IReadOnlyList<string> Describe(OverclockProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var lines = new List<string>();
        if (profile.GpuCoreOffsetMhz != 0)
        {
            lines.Add($"GPU core clock offset: {profile.GpuCoreOffsetMhz:+#;-#;0} MHz");
        }

        if (profile.GpuMemoryOffsetMhz != 0)
        {
            lines.Add($"GPU memory clock offset: {profile.GpuMemoryOffsetMhz:+#;-#;0} MHz");
        }

        if (profile.GpuPowerLimitPercent != 100)
        {
            lines.Add($"GPU power limit: {profile.GpuPowerLimitPercent} %");
        }

        if (profile.GpuTempLimitC is { } t)
        {
            lines.Add($"GPU temperature limit: {t} °C");
        }

        if (profile.GpuFanPercent is { } f)
        {
            lines.Add($"GPU fan: fixed {f} %");
        }

        if (profile.CpuAllCoreRatio is { } r)
        {
            lines.Add($"CPU all-core ratio: {r}x");
        }

        if (profile.CpuVoltageOffsetMv is { } mv)
        {
            lines.Add($"CPU voltage offset: {mv:+#;-#;0} mV");
        }

        if (profile.CpuPowerLimitW is { } w)
        {
            lines.Add($"CPU power limit: {w} W");
        }

        if (lines.Count == 0)
        {
            lines.Add("Everything at stock (no change).");
        }

        return lines;
    }
}
