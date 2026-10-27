using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>What a vendor tuner found: the control method it can offer and the hardware's ranges.</summary>
public sealed record GpuTunerDetection(GpuControlMethod Method, string? GpuName, GpuLimits? Limits, IReadOnlyList<string> Notes)
{
    public static GpuTunerDetection Unavailable(string note) => new(GpuControlMethod.None, null, null, [note]);
}

/// <summary>Current tuner-side state (what the driver reports as applied).</summary>
public sealed record GpuTunerState(int CoreOffsetMhz, int MemoryOffsetMhz, int PowerLimitPercent, int? TempLimitC, int? FanPercent, int? CoreClockMhz, int? MemoryClockMhz, double? TempC, double? PowerW)
{
    /// <summary>The voltage offset the driver reports in mV (0 = stock), or null when the tuner cannot read one.</summary>
    public int? VoltageOffsetMv { get; init; }

    /// <summary>
    /// True when the driver reports every value PC Checker manages at stock. A voltage offset that could not be read
    /// counts as stock only when <paramref name="expectVoltageOffset"/> is false (none was applied).
    /// </summary>
    public bool IsStock(bool expectVoltageOffset) =>
        CoreOffsetMhz == 0 && MemoryOffsetMhz == 0 && PowerLimitPercent == 100
        && (VoltageOffsetMv is { } v ? v == 0 : !expectVoltageOffset);
}

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

    /// <summary>Returns the temperature limit to the driver default (a no-op success when the card has none).</summary>
    OperationResult ResetTempLimit();

    /// <summary>Fixed fan speed, or null to return the fan to automatic control.</summary>
    OperationResult SetFan(int? percent);

    /// <summary>
    /// Returns clocks, power and temperature limits to stock. Fans are only returned to automatic control when
    /// <paramref name="resetFan"/> is true, i.e. when the overclock profile itself fixed them; otherwise they are left
    /// to whoever owns them (the Fans page).
    /// </summary>
    OperationResult Reset(bool resetFan);
}

/// <summary>Optional: a tuner that can offset the GPU voltage (AMD Overdrive8 OD_VOLTAGE on RX 7000/9000).</summary>
public interface IGpuVoltageTuner
{
    /// <summary>Sets the voltage offset in mV (0 = stock); validated against the driver's range.</summary>
    OperationResult SetVoltageOffset(int millivolts);
}

/// <summary>Optional: a tuner that knows whether the vendor's own tuning switch is on (AMD Software manual tuning).</summary>
public interface IGpuTuningSwitch
{
    /// <summary>False when the driver reports tuning as disabled, so values sent now would be ignored.</summary>
    bool TuningEnabled { get; }
}
