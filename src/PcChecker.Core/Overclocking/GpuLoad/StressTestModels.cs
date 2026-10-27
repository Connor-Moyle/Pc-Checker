using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>Where the GPU load comes from in a GPU or combined stress test.</summary>
public enum GpuStressMode
{
    /// <summary>PC Checker loads the card itself with <see cref="IGpuLoadGenerator"/>.</summary>
    BuiltIn,

    /// <summary>The user runs their own game or benchmark; PC Checker only watches.</summary>
    Guided,
}

/// <summary>Full stress-test request, including the GPU choices the original <see cref="IOverclockService"/> call cannot carry.</summary>
public sealed record StressTestRequest(StressTarget Target, int Seconds, double AbortCpuTempC, double AbortGpuTempC)
{
    public GpuStressMode GpuMode { get; init; } = GpuStressMode.BuiltIn;

    /// <summary>DXGI adapter index; negative picks the recommended (most dedicated memory) adapter.</summary>
    public int GpuAdapterIndex { get; init; } = -1;

    public GpuLoadIntensity Intensity { get; init; } = GpuLoadIntensity.Medium;

    public bool VerifyGpuResults { get; init; } = true;

    public bool LoadsCpu => Target is StressTarget.Cpu or StressTarget.Both;

    public bool LoadsGpu => (Target is StressTarget.Gpu or StressTarget.Both) && GpuMode == GpuStressMode.BuiltIn;

    public bool WatchesGpu => Target is StressTarget.Gpu or StressTarget.Both;
}

/// <summary>Live readouts once a second while a stress test runs.</summary>
public sealed record StressLiveStatus(
    TimeSpan Elapsed,
    double Percent,
    double? CpuTempC,
    double? GpuTempC,
    int? CpuClockMhz,
    int? GpuClockMhz,
    double? GpuLoadPercent,
    double DispatchesPerSecond,
    long GpuErrors,
    long CpuChecks,
    int CpuErrors,
    string? GpuAdapterName);

/// <summary>Whether a stress test may start right now (battery check) and what to tell the user.</summary>
public sealed record StressPreflight(bool CanRun, bool OnBattery, int? BatteryPercent, string? Message)
{
    public static StressPreflight Ok { get; } = new(true, false, null, null);

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
}

/// <summary>Everything a finished stress test produced: the contract result plus the GPU details and the verdict text.</summary>
public sealed record StressTestOutcome(
    StressTestResult Result,
    StressVerdict Verdict,
    GpuLoadResult? Gpu,
    double? AverageCpuClockMhz,
    double? AverageGpuClockMhz,
    long CpuChecks,
    bool OverclockReverted);

/// <summary>
/// Extended stress test implemented by both overclock services: adapter choice, intensity, the guided option, live
/// readouts and a richer verdict. The plain <see cref="IOverclockService.RunStressTestAsync"/> uses the defaults.
/// </summary>
public interface IStressTestRunner
{
    /// <summary>Graphics adapters for the GPU load, best first. Empty when DirectX 11 is not available.</summary>
    Task<IReadOnlyList<GpuAdapterChoice>> ListGpuAdaptersAsync(CancellationToken cancellationToken);

    /// <summary>Checks the power source: a laptop on battery below 20 % must not start a stress test.</summary>
    StressPreflight CheckPreflight();

    Task<StressTestOutcome> RunStressTestAsync(StressTestRequest request, IProgress<StressLiveStatus>? live, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
