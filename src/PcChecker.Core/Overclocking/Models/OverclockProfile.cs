namespace PcChecker.Core.Overclocking;

/// <summary>A set of overclock targets. Mutable so it can be edited in the UI and serialized to settings.</summary>
public sealed class OverclockProfile
{
    public string Name { get; set; } = "Default";
    public int GpuCoreOffsetMhz { get; set; }
    public int GpuMemoryOffsetMhz { get; set; }
    public int GpuPowerLimitPercent { get; set; } = 100;
    public int? GpuTempLimitC { get; set; }
    public int? GpuFanPercent { get; set; }
    public int? CpuAllCoreRatio { get; set; }
    public int? CpuVoltageOffsetMv { get; set; }
    public int? CpuPowerLimitW { get; set; }
    public string? Notes { get; set; }

    /// <summary>A profile that represents stock settings.</summary>
    public static OverclockProfile Stock() => new() { Name = "Stock" };

    public OverclockProfile Clone() => (OverclockProfile)MemberwiseClone();

    /// <summary>True when every value is at stock.</summary>
    public bool IsStock =>
        GpuCoreOffsetMhz == 0 && GpuMemoryOffsetMhz == 0 && GpuPowerLimitPercent == 100 && GpuTempLimitC is null
        && GpuFanPercent is null && CpuAllCoreRatio is null && CpuVoltageOffsetMv is null && CpuPowerLimitW is null;
}
