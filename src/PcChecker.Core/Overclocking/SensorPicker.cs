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

        var r = snapshot.Readings;
        return new LiveReadout(
            AsInt(First(r, HardwareKind.Gpu, SensorKind.Clock, "core", "graphics")),
            AsInt(First(r, HardwareKind.Gpu, SensorKind.Clock, "memory", "mem")),
            First(r, HardwareKind.Gpu, SensorKind.Temperature, "core", "gpu"),
            First(r, HardwareKind.Gpu, SensorKind.Temperature, "hot spot", "hotspot", "junction"),
            First(r, HardwareKind.Gpu, SensorKind.Power, "package", "total", "board", "gpu"),
            First(r, HardwareKind.Gpu, SensorKind.Load, "core", "gpu", "3d"),
            First(r, HardwareKind.Gpu, SensorKind.Control, "fan"),
            First(r, HardwareKind.Gpu, SensorKind.Fan, "fan"),
            First(r, HardwareKind.Cpu, SensorKind.Temperature, "package", "tctl", "tdie", "core max", "cpu", "core"),
            AsInt(First(r, HardwareKind.Cpu, SensorKind.Clock, "core #1", "core", "cpu")),
            First(r, HardwareKind.Cpu, SensorKind.Load, "total", "cpu"),
            First(r, HardwareKind.Cpu, SensorKind.Power, "package", "cpu"));
    }

    /// <summary>The hottest CPU reading, for the watchdog.</summary>
    public static double? MaxCpuTemp(SensorSnapshot? snapshot) => Max(snapshot, HardwareKind.Cpu);

    /// <summary>The hottest GPU reading (core or hot spot), for the watchdog.</summary>
    public static double? MaxGpuTemp(SensorSnapshot? snapshot) => Max(snapshot, HardwareKind.Gpu);

    private static double? Max(SensorSnapshot? snapshot, HardwareKind hardware)
    {
        if (snapshot is null)
        {
            return null;
        }

        double? max = null;
        foreach (var reading in snapshot.Readings)
        {
            if (reading.Hardware == hardware && reading.Kind == SensorKind.Temperature && reading.Value is { } v && !double.IsNaN(v))
            {
                max = max is null ? v : Math.Max(max.Value, v);
            }
        }

        return max;
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
