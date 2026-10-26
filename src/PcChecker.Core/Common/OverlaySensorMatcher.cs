using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Common;

/// <summary>A resolved overlay value plus the unit it is expressed in (null unit = the metric's default unit).</summary>
public readonly record struct OverlayMetricValue(double? Value, string? Unit = null)
{
    public static OverlayMetricValue None => new(null, null);

    public bool HasValue => Value is not null && !double.IsNaN(Value.Value);
}

/// <summary>
/// Picks the sensor reading that best represents each overlay metric using name heuristics that match
/// LibreHardwareMonitor's naming ("CPU Package", "GPU Core", "GPU Hot Spot", "GPU Memory Used", ...).
/// Pure: works on a <see cref="SensorSnapshot"/> only, so it is unit-testable.
/// </summary>
public static class OverlaySensorMatcher
{
    /// <summary>Resolves a metric from the snapshot. Unknown metrics and metrics with no matching sensor return no value.</summary>
    public static OverlayMetricValue Resolve(SensorSnapshot? snapshot, string metricId)
    {
        if (snapshot is null || snapshot.Readings.Count == 0)
        {
            return OverlayMetricValue.None;
        }

        var r = snapshot.Readings;
        return metricId switch
        {
            OverlayMetricIds.CpuTemp => Value(First(r, HardwareKind.Cpu, SensorKind.Temperature, "CPU Package", "Package", "Tctl", "Tdie", "Core Average", "Core Max")
                                              ?? First(r, HardwareKind.Cpu, SensorKind.Temperature)),
            OverlayMetricIds.CpuLoad => Value(First(r, HardwareKind.Cpu, SensorKind.Load, "CPU Total", "Total") ?? First(r, HardwareKind.Cpu, SensorKind.Load)),
            OverlayMetricIds.CpuClock => Max(r, HardwareKind.Cpu, SensorKind.Clock, exclude: "Bus"),
            OverlayMetricIds.CpuPower => Value(First(r, HardwareKind.Cpu, SensorKind.Power, "CPU Package", "Package") ?? First(r, HardwareKind.Cpu, SensorKind.Power)),
            OverlayMetricIds.GpuTemp => Value(First(r, HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", "GPU Temperature")
                                              ?? FirstWhere(r, HardwareKind.Gpu, SensorKind.Temperature, x => !Contains(x.Name, "Hot Spot") && !Contains(x.Name, "Memory"))),
            OverlayMetricIds.GpuHotspot => Value(First(r, HardwareKind.Gpu, SensorKind.Temperature, "Hot Spot", "Hotspot", "Junction")),
            OverlayMetricIds.GpuLoad => Value(First(r, HardwareKind.Gpu, SensorKind.Load, "GPU Core", "D3D 3D") ?? First(r, HardwareKind.Gpu, SensorKind.Load)),
            OverlayMetricIds.GpuClock => Value(First(r, HardwareKind.Gpu, SensorKind.Clock, "GPU Core", "Graphics") ?? First(r, HardwareKind.Gpu, SensorKind.Clock)),
            OverlayMetricIds.GpuMemClock => Value(First(r, HardwareKind.Gpu, SensorKind.Clock, "GPU Memory", "Memory")),
            OverlayMetricIds.GpuVram => VramMegabytes(r),
            OverlayMetricIds.GpuPower => Value(First(r, HardwareKind.Gpu, SensorKind.Power, "GPU Package", "GPU Power", "Package", "Board") ?? First(r, HardwareKind.Gpu, SensorKind.Power)),
            OverlayMetricIds.GpuFan => GpuFan(r),
            OverlayMetricIds.RamUsed => Gigabytes(First(r, HardwareKind.Memory, SensorKind.Data, "Memory Used", "Used") ?? First(r, HardwareKind.Memory, SensorKind.Data)),
            OverlayMetricIds.RamPercent => Value(First(r, HardwareKind.Memory, SensorKind.Load, "Memory") ?? First(r, HardwareKind.Memory, SensorKind.Load)),
            OverlayMetricIds.DiskTemp => Max(r, HardwareKind.Storage, SensorKind.Temperature),
            OverlayMetricIds.DiskActivity => Max(r, HardwareKind.Storage, SensorKind.Load, include: "Activity"),
            OverlayMetricIds.NetDown => Throughput(r, "Download"),
            OverlayMetricIds.NetUp => Throughput(r, "Upload"),
            OverlayMetricIds.BatteryPercent => Value(First(r, HardwareKind.Battery, SensorKind.Level, "Charge") ?? First(r, HardwareKind.Battery, SensorKind.Level)),
            OverlayMetricIds.BatteryWatts => Value(First(r, HardwareKind.Battery, SensorKind.Power, "Discharge", "Charge") ?? First(r, HardwareKind.Battery, SensorKind.Power)),
            OverlayMetricIds.SystemPower => SystemPower(r),
            _ => OverlayMetricValue.None,
        };
    }

    /// <summary>Converts a data/throughput value to megabytes (or MB/s) based on the sensor's unit text.</summary>
    public static double ToMegabytes(double value, string? unit)
    {
        var u = (unit ?? string.Empty).Trim();
        if (u.StartsWith("GB", StringComparison.OrdinalIgnoreCase))
        {
            return value * 1024;
        }

        if (u.StartsWith("MB", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (u.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
        {
            return value / 1024;
        }

        if (u.StartsWith("B", StringComparison.OrdinalIgnoreCase))
        {
            return value / (1024.0 * 1024.0);
        }

        return value;
    }

    private static OverlayMetricValue Value(SensorReading? reading) => reading?.Value is { } v ? new(v) : OverlayMetricValue.None;

    private static SensorReading? First(IReadOnlyList<SensorReading> readings, HardwareKind hardware, SensorKind kind, params string[] preferredNames)
    {
        foreach (var name in preferredNames)
        {
            var match = FirstWhere(readings, hardware, kind, x => Contains(x.Name, name));
            if (match is not null)
            {
                return match;
            }
        }

        return preferredNames.Length == 0 ? FirstWhere(readings, hardware, kind, _ => true) : null;
    }

    private static SensorReading? FirstWhere(IReadOnlyList<SensorReading> readings, HardwareKind hardware, SensorKind kind, Func<SensorReading, bool> predicate)
    {
        foreach (var x in readings)
        {
            if (x.Hardware == hardware && x.Kind == kind && x.Value is not null && predicate(x))
            {
                return x;
            }
        }

        return null;
    }

    private static OverlayMetricValue Max(IReadOnlyList<SensorReading> readings, HardwareKind hardware, SensorKind kind, string? include = null, string? exclude = null)
    {
        double? best = null;
        foreach (var x in readings)
        {
            if (x.Hardware != hardware || x.Kind != kind || x.Value is null)
            {
                continue;
            }

            if (include is not null && !Contains(x.Name, include))
            {
                continue;
            }

            if (exclude is not null && Contains(x.Name, exclude))
            {
                continue;
            }

            best = best is null ? x.Value : Math.Max(best.Value, x.Value.Value);
        }

        return best is null ? OverlayMetricValue.None : new(best);
    }

    private static OverlayMetricValue VramMegabytes(IReadOnlyList<SensorReading> readings)
    {
        var used = First(readings, HardwareKind.Gpu, SensorKind.Data, "GPU Memory Used", "Memory Used", "D3D Dedicated Memory Used");
        return used?.Value is { } v ? new(ToMegabytes(v, used.Unit), "MB") : OverlayMetricValue.None;
    }

    private static OverlayMetricValue GpuFan(IReadOnlyList<SensorReading> readings)
    {
        var control = First(readings, HardwareKind.Gpu, SensorKind.Control, "Fan") ?? First(readings, HardwareKind.Gpu, SensorKind.Control);
        if (control?.Value is { } percent)
        {
            return new(percent, "%");
        }

        var rpm = First(readings, HardwareKind.Gpu, SensorKind.Fan, "Fan") ?? First(readings, HardwareKind.Gpu, SensorKind.Fan);
        return rpm?.Value is { } r ? new(r, "RPM") : OverlayMetricValue.None;
    }

    private static OverlayMetricValue Gigabytes(SensorReading? reading) =>
        reading?.Value is { } v ? new(ToMegabytes(v, reading.Unit) / 1024.0, "GB") : OverlayMetricValue.None;

    private static OverlayMetricValue Throughput(IReadOnlyList<SensorReading> readings, string direction)
    {
        double total = 0;
        bool any = false;
        foreach (var x in readings)
        {
            if (x.Hardware != HardwareKind.Network || x.Kind != SensorKind.Throughput || x.Value is null || !Contains(x.Name, direction))
            {
                continue;
            }

            any = true;
            total += ToMegabytes(x.Value.Value, x.Unit);
        }

        return any ? new(total, "MB/s") : OverlayMetricValue.None;
    }

    private static OverlayMetricValue SystemPower(IReadOnlyList<SensorReading> readings)
    {
        var psu = First(readings, HardwareKind.Psu, SensorKind.Power, "Total", "Output", "Power") ?? First(readings, HardwareKind.Psu, SensorKind.Power);
        if (psu?.Value is { } p)
        {
            return new(p, "W");
        }

        var snapshot = new SensorSnapshot(DateTimeOffset.MinValue, readings);
        var cpu = Resolve(snapshot, OverlayMetricIds.CpuPower);
        var gpu = Resolve(snapshot, OverlayMetricIds.GpuPower);
        if (!cpu.HasValue && !gpu.HasValue)
        {
            return OverlayMetricValue.None;
        }

        // Same estimate the Power module uses when no PSU sensor exists: known parts × 1.15 + 25 W baseline.
        double known = (cpu.Value ?? 0) + (gpu.Value ?? 0);
        return new(Math.Round(known * 1.15 + 25, 0), "W");
    }

    private static bool Contains(string? text, string part) => text is not null && text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
