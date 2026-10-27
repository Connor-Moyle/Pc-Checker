namespace PcChecker.Core.Overclocking;

public enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
    Unknown,
}

public enum GpuControlMethod
{
    None,
    NvApi,
    AmdAdl,
    /// <summary>Intel Arc via the Intel Graphics Control Library (experimental).</summary>
    IntelIgcl,
}

public enum CpuControlMethod
{
    None,
    IntelXtuCli,
    /// <summary>Kept for settings compatibility; new code uses <see cref="RyzenMaster"/>.</summary>
    RyzenMasterCli,
    /// <summary>AMD Ryzen Master is installed and can be launched for tuning.</summary>
    RyzenMaster,
    /// <summary>ryzenadj.exe is available for power limits (mobile/APU focus).</summary>
    RyzenAdj,
}

/// <summary>Support levels used in <see cref="OverclockCapabilities.SupportMatrix"/> and on the Overclock page.</summary>
public static class SupportLevel
{
    public const string Full = "Full";
    public const string Partial = "Partial";
    public const string Experimental = "Experimental";
    public const string MonitorOnly = "Monitor only";
    public const string Unsupported = "Unsupported";
}

public enum StressTarget
{
    Cpu,
    Gpu,
    Both,
}

public sealed record GpuLimits(
    int CoreOffsetMinMhz,
    int CoreOffsetMaxMhz,
    int MemoryOffsetMinMhz,
    int MemoryOffsetMaxMhz,
    int PowerLimitMinPercent,
    int PowerLimitMaxPercent,
    int TempLimitMinC,
    int TempLimitMaxC,
    bool FanControl)
{
    /// <summary>GPU voltage offset range in mV (AMD Overdrive8 on RDNA3/4, typically -200..0); 0..0 when not offered.</summary>
    public int VoltageOffsetMinMv { get; init; }

    public int VoltageOffsetMaxMv { get; init; }

    public bool VoltageOffsetSupported => VoltageOffsetMinMv < VoltageOffsetMaxMv;

    /// <summary>True when the fan is driven through a vendor fan curve (AMD Overdrive8) rather than a fixed duty.</summary>
    public bool FanCurve { get; init; }
}

public sealed record OverclockCapabilities(
    GpuVendor GpuVendor,
    string? GpuName,
    GpuControlMethod GpuMethod,
    GpuLimits? GpuLimits,
    string? CpuName,
    CpuControlMethod CpuMethod,
    string? CpuToolPath,
    string XmpState,
    int? MemoryConfiguredMhz,
    int? MemoryMaxMhz,
    IReadOnlyList<string> Notes)
{
    public static OverclockCapabilities None(string? note = null) =>
        new(GpuVendor.Unknown, null, GpuControlMethod.None, null, null, CpuControlMethod.None, null, "Unknown", null, null, note is null ? [] : [note]);

    public bool CanTuneGpu => GpuMethod != GpuControlMethod.None;
    public bool CanTuneCpu => CpuMethod != CpuControlMethod.None;

    /// <summary>What this app can do for each feature on this machine (Full, Partial, Experimental, Monitor only, Unsupported).</summary>
    public IReadOnlyList<(string Feature, string Vendor, string Level, string Note)> SupportMatrix { get; init; } = [];
}

public sealed record OverclockState(
    OverclockProfile Current,
    int? GpuCoreClockMhz,
    int? GpuMemoryClockMhz,
    double? GpuTempC,
    double? GpuPowerW,
    double? CpuTempC,
    int? CpuClockMhz);

public sealed record OverclockStep(int Order, string Title, string Rationale, OverclockProfile Target, int StressSeconds, string AbortIf, string ExpectedGain);

public sealed record OverclockPlan(
    string Summary,
    IReadOnlyList<OverclockStep> Steps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Assumptions,
    string Disclaimer);

public sealed record StressTestResult(
    StressTarget Target,
    int DurationSeconds,
    double? MaxCpuTempC,
    double? MaxGpuTempC,
    bool ThermalThrottleSuspected,
    int Errors,
    bool Passed,
    string Summary,
    bool AbortedByWatchdog);
