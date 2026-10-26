using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>What a vendor tuner found: the control method it can offer and the hardware's ranges.</summary>
public sealed record GpuTunerDetection(GpuControlMethod Method, string? GpuName, GpuLimits? Limits, IReadOnlyList<string> Notes)
{
    public static GpuTunerDetection Unavailable(string note) => new(GpuControlMethod.None, null, null, [note]);
}

/// <summary>Current tuner-side state (what the driver reports as applied).</summary>
public sealed record GpuTunerState(int CoreOffsetMhz, int MemoryOffsetMhz, int PowerLimitPercent, int? TempLimitC, int? FanPercent, int? CoreClockMhz, int? MemoryClockMhz, double? TempC, double? PowerW);

/// <summary>
/// A vendor-specific GPU controller. One implementation per vendor API (NVAPI, ADL/Overdrive8, IGCL). Every call is
/// guarded: failures come back as <see cref="OperationResult"/>, never exceptions, so the service can degrade.
/// </summary>
public interface IGpuTuner : IDisposable
{
    GpuVendor Vendor { get; }

    GpuControlMethod Method { get; }

    /// <summary>Loads the vendor library and reads the limits; returns <see cref="GpuControlMethod.None"/> on failure.</summary>
    GpuTunerDetection Detect();

    GpuLimits? Limits { get; }

    OperationResult<GpuTunerState> GetState();

    OperationResult ApplyOffsets(int coreOffsetMhz, int memoryOffsetMhz);

    OperationResult SetPowerLimit(int percent);

    OperationResult SetTempLimit(int tempC);

    /// <summary>Fixed fan speed, or null to return the fan to automatic control.</summary>
    OperationResult SetFan(int? percent);

    OperationResult Reset();
}
