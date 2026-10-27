namespace PcChecker.Core.Overclocking;

/// <summary>Built-in starting points. Users save their own profiles next to these in settings.</summary>
public static class OverclockPresets
{
    public const string DefaultName = "Default (0 / 0)";
    public const string MildName = "Mild (+50 / +200)";
    public const string ModerateName = "Moderate (+100 / +500)";
    public const string QuietName = "Quiet and cool (power 85 %)";

    public const string AmdUndervoltName = "Undervolt (-50 mV)";
    public const string AmdUndervoltPowerName = "Undervolt (-80 mV, power -10 %)";
    public const string AmdQuietName = "Quiet (power -10 %)";

    public static IReadOnlyList<OverclockProfile> All { get; } =
    [
        new() { Name = DefaultName, Notes = "Stock clocks. Use this to undo everything." },
        new() { Name = MildName, GpuCoreOffsetMhz = 50, GpuMemoryOffsetMhz = 200, Notes = "A small bump almost every card handles." },
        new() { Name = ModerateName, GpuCoreOffsetMhz = 100, GpuMemoryOffsetMhz = 500, GpuPowerLimitPercent = 105, Notes = "Needs a short stress test; back off if you see artifacts." },
        new() { Name = QuietName, GpuPowerLimitPercent = 85, GpuTempLimitC = 75, Notes = "Lower power and temperature targets for a quieter PC." },
    ];

    /// <summary>
    /// Radeon starting points. On RX 7000/9000 the voltage offset is the main and safest control (less power and heat
    /// at the same clocks); NVIDIA-style memory offsets are clamped to +100..+250 MHz on most Radeons and Overdrive8
    /// temperature limits rarely go below about 90 °C, so the generic presets do not fit.
    /// </summary>
    public static IReadOnlyList<OverclockProfile> Amd { get; } =
    [
        new() { Name = DefaultName, Notes = "Stock settings. Use this to undo everything." },
        new() { Name = AmdUndervoltName, GpuVoltageOffsetMv = -50, Notes = "Same performance with less power and heat on most RX 7000/9000 cards." },
        new() { Name = AmdUndervoltPowerName, GpuVoltageOffsetMv = -80, GpuPowerLimitPercent = 90, Notes = "Cooler and quieter; run the stress test, and go back to -50 mV if a game crashes." },
        new() { Name = AmdQuietName, GpuPowerLimitPercent = 90, Notes = "Lower power for a quieter card with a small performance cost." },
    ];

    /// <summary>The presets for a GPU vendor: the Radeon set for AMD, the offset set otherwise.</summary>
    public static IReadOnlyList<OverclockProfile> ForVendor(GpuVendor vendor) => vendor == GpuVendor.Amd ? Amd : All;

    public static bool IsPreset(string? name) => name is not null && All.Concat(Amd).Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a copy of the preset (presets are never edited in place).</summary>
    public static OverclockProfile? Get(string? name) => All.Concat(Amd).FirstOrDefault(p => p.Name.Equals(name ?? string.Empty, StringComparison.OrdinalIgnoreCase))?.Clone();

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

        if (profile.GpuVoltageOffsetMv != 0)
        {
            lines.Add($"GPU voltage offset: {profile.GpuVoltageOffsetMv:+#;-#;0} mV");
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
