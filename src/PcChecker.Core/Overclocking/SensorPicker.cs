using PcChecker.Core.Sensors;

namespace PcChecker.Core.Overclocking;

/// <summary>Live values the Overclock page shows, picked out of a sensor snapshot.</summary>
public sealed record LiveReadout(
    int? GpuCoreClockMhz,
    int? GpuMemoryClockMhz,
    double? GpuTempC,
    double? GpuHotSpotC,
    double? GpuPowerW,
    double? GpuLoadPercent,
    double? GpuFanPercent,
    double? GpuFanRpm,
    double? CpuTempC,
    int? CpuClockMhz,
    double? CpuLoadPercent,
    double? CpuPowerW)
{
    public static LiveReadout Empty { get; } = new(null, null, null, null, null, null, null, null, null, null, null, null);
}

/// <summary>Pure selection of the readings that matter for tuning from any <see cref="SensorSnapshot"/>.</summary>
public static class SensorPicker
{
    public static LiveReadout Pick(SensorSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Readings.Count == 0)
        {
            return LiveReadout.Empty;
        }

        // Only the primary (discrete) GPU: an integrated Radeon on Ryzen 7000/9000 is listed first otherwise.
        var r = GpuSelection.PrimaryGpuOnly(snapshot.Readings);
        return new LiveReadout(
            AsInt(First(r, HardwareKind.Gpu, SensorKind.Clock, "core", "graphics")),
            AsInt(First(r, HardwareKind.Gpu, SensorKind.Clock, "memory", "mem")),
            FirstWhere(r, HardwareKind.Gpu, SensorKind.Temperature, x => TemperatureRoles.Classify(x) == TemperatureRole.GpuCore, "core", "gpu"),
            First(r, HardwareKind.Gpu, SensorKind.Temperature, "hot spot", "hotspot", "junction"),
            First(r, HardwareKind.Gpu, SensorKind.Power, "package", "total", "board", "gpu"),
            First(r, HardwareKind.Gpu, SensorKind.Load, "core", "gpu", "3d"),
            First(r, HardwareKind.Gpu, SensorKind.Control, "fan"),
            First(r, HardwareKind.Gpu, SensorKind.Fan, "fan"),
            TemperatureRoles.PickCpuTemperature(r)?.Value,
            AsInt(First(r, HardwareKind.Cpu, SensorKind.Clock, "core #1", "core", "cpu")),
            First(r, HardwareKind.Cpu, SensorKind.Load, "total", "cpu"),
            First(r, HardwareKind.Cpu, SensorKind.Power, "package", "cpu"));
    }

    /// <summary>
    /// The hottest CPU control temperature (Tdie, Tctl/Tdie, package), for the watchdog and the pre-check. Raw
    /// "Core (Tctl)" is left out when a Tdie reading exists (it carries a 10-27 °C offset on old Ryzen parts).
    /// </summary>
    public static double? MaxCpuTemp(SensorSnapshot? snapshot) =>
        snapshot is null ? null : MaxOf(TemperatureRoles.CpuLimitReadings(snapshot.Readings));

    /// <summary>
    /// The hottest core/edge temperature of the primary GPU, for the watchdog and the pre-check. Hot spot and memory
    /// sensors have their own, higher limits (see <see cref="GpuLimitBreach"/>); integrated graphics are left out.
    /// </summary>
    public static double? MaxGpuTemp(SensorSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var gpu = GpuSelection.PrimaryGpuOnly(snapshot.Readings).Where(r => r.Hardware == HardwareKind.Gpu && r.Kind == SensorKind.Temperature).ToList();
        var core = gpu.Where(r => TemperatureRoles.Classify(r) is TemperatureRole.GpuCore or TemperatureRole.GpuLiquid).ToList();
        return MaxOf(core.Count > 0 ? core : gpu.Where(r => TemperatureRoles.Classify(r) != TemperatureRole.IntegratedGpu));
    }

    /// <summary>The hottest hot spot (junction) reading of the primary GPU.</summary>
    public static double? MaxGpuHotSpot(SensorSnapshot? snapshot) => MaxGpuRole(snapshot, TemperatureRole.GpuHotSpot);

    /// <summary>The hottest memory reading of the primary GPU.</summary>
    public static double? MaxGpuMemoryTemp(SensorSnapshot? snapshot) => MaxGpuRole(snapshot, TemperatureRole.GpuMemory);

    /// <summary>
    /// Null while every primary-GPU temperature is under its own limit (core, hot spot, memory); otherwise a short
    /// reason. The hot spot and memory limits default to AMD's 110 °C and 105 °C.
    /// </summary>
    public static string? GpuLimitBreach(
        SensorSnapshot? snapshot,
        double coreLimitC,
        double hotSpotLimitC = TemperatureRoles.HotSpotCritC,
        double memoryLimitC = TemperatureRoles.MemoryCritC)
    {
        if (MaxGpuTemp(snapshot) is { } core && core >= coreLimitC)
        {
            return $"GPU {core:0} °C (limit {coreLimitC:0} °C)";
        }

        if (MaxGpuHotSpot(snapshot) is { } hot && hot >= hotSpotLimitC)
        {
            return $"GPU hot spot {hot:0} °C (limit {hotSpotLimitC:0} °C)";
        }

        return MaxGpuMemoryTemp(snapshot) is { } mem && mem >= memoryLimitC ? $"GPU memory {mem:0} °C (limit {memoryLimitC:0} °C)" : null;
    }

    /// <summary>The CPU name as the sensors report it, used for platform-aware limits.</summary>
    public static string? CpuName(SensorSnapshot? snapshot) =>
        snapshot?.Readings.FirstOrDefault(r => r.Hardware == HardwareKind.Cpu)?.HardwareName;

    private static double? MaxGpuRole(SensorSnapshot? snapshot, TemperatureRole role) =>
        snapshot is null
            ? null
            : MaxOf(GpuSelection.PrimaryGpuOnly(snapshot.Readings)
                .Where(r => r.Hardware == HardwareKind.Gpu && r.Kind == SensorKind.Temperature && TemperatureRoles.Classify(r) == role));

    private static double? MaxOf(IEnumerable<SensorReading> readings)
    {
        double? max = null;
        foreach (var reading in readings)
        {
            if (reading.Kind == SensorKind.Temperature && reading.Value is { } v && !double.IsNaN(v))
            {
                max = max is null ? v : Math.Max(max.Value, v);
            }
        }

        return max;
    }

    private static double? FirstWhere(IReadOnlyList<SensorReading> readings, HardwareKind hardware, SensorKind kind, Func<SensorReading, bool> filter, params string[] nameHints)
    {
        var subset = readings.Where(x => x.Hardware != hardware || x.Kind != kind || filter(x)).ToList();
        return First(subset, hardware, kind, nameHints) ?? First(readings, hardware, kind, nameHints);
    }

    private static double? First(IReadOnlyList<SensorReading> readings, HardwareKind hardware, SensorKind kind, params string[] nameHints)
    {
        foreach (var hint in nameHints)
        {
            foreach (var reading in readings)
            {
                if (reading.Hardware == hardware && reading.Kind == kind && reading.Value is { } v && !double.IsNaN(v)
                    && reading.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    return v;
                }
            }
        }

        foreach (var reading in readings)
        {
            if (reading.Hardware == hardware && reading.Kind == kind && reading.Value is { } v && !double.IsNaN(v))
            {
                return v;
            }
        }

        return null;
    }

    private static int? AsInt(double? value) => value is { } v ? (int)Math.Round(v) : null;
}
