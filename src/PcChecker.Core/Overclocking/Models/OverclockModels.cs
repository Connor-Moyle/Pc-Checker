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
}

public enum CpuControlMethod
{
    None,
    IntelXtuCli,
    RyzenMasterCli,
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
    bool FanControl);

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
