using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Sensors;

/// <summary>Pure math over sensor history and thresholds.</summary>
public static class SensorMath
{
    public sealed record Stats(double Min, double Max, double Average, double Last, int Count);

    /// <summary>Min/max/average over a series. Returns null for an empty series.</summary>
    public static Stats? Summarize(IEnumerable<double> values)
    {
        double min = double.MaxValue, max = double.MinValue, sum = 0, last = 0;
        int count = 0;
        foreach (var v in values)
        {
            if (double.IsNaN(v))
            {
                continue;
            }

            min = Math.Min(min, v);
            max = Math.Max(max, v);
            sum += v;
            last = v;
            count++;
        }

        return count == 0 ? null : new Stats(min, max, sum / count, last, count);
    }

    public static Stats? Summarize(IEnumerable<(DateTimeOffset Time, double Value)> history) => Summarize(history.Select(h => h.Value));

    /// <summary>Keeps the samples within <paramref name="span"/> of <paramref name="now"/>.</summary>
    public static IReadOnlyList<(DateTimeOffset Time, double Value)> Window(
        IEnumerable<(DateTimeOffset Time, double Value)> history, TimeSpan span, DateTimeOffset now)
    {
        var cutoff = now - span;
        return history.Where(h => h.Time >= cutoff).ToList();
    }

    /// <summary>Reduces a series to at most <paramref name="points"/> points by averaging buckets (for sparklines).</summary>
    public static IReadOnlyList<double> Downsample(IReadOnlyList<double> values, int points)
    {
        if (points <= 0 || values.Count <= points)
        {
            return values;
        }

        var result = new List<double>(points);
        double bucket = (double)values.Count / points;
        for (int i = 0; i < points; i++)
        {
            int start = (int)(i * bucket);
            int end = Math.Min(values.Count, (int)((i + 1) * bucket));
            if (end <= start)
            {
                end = start + 1;
            }

            double sum = 0;
            for (int j = start; j < end; j++)
            {
                sum += values[j];
            }

            result.Add(sum / (end - start));
        }

        return result;
    }

    /// <summary>Returns the warning and critical thresholds for a hardware kind from the user's settings.</summary>
    public static (double Warn, double Crit)? ThresholdsFor(HardwareKind kind, MonitoringSettings settings) => kind switch
    {
        HardwareKind.Cpu => (settings.CpuWarnC, settings.CpuCritC),
        HardwareKind.Gpu => (settings.GpuWarnC, settings.GpuCritC),
        HardwareKind.Storage => (settings.DiskWarnC, settings.DiskCritC),
        HardwareKind.Motherboard => (settings.CpuWarnC, settings.CpuCritC),
        _ => null,
    };

    /// <summary>
    /// The thresholds for one reading. Same as <see cref="ThresholdsFor(HardwareKind, MonitoringSettings)"/> except for
    /// extra drive sensors (an NVMe controller sensor, "Sensor 2"), which run hotter than the drive and get their own,
    /// higher limits (see <see cref="Storage.DriveTemperatureRules.Thresholds"/>).
    /// </summary>
    public static (double Warn, double Crit)? ThresholdsFor(SensorReading reading, MonitoringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return AdjustForSensor(reading, ThresholdsFor(reading.Hardware, settings), settings);
    }

    /// <summary>
    /// Applies the limits that depend on the sensor itself rather than its hardware to thresholds worked out
    /// elsewhere (per hardware, or per role): an extra drive sensor (NVMe controller, "Sensor 2") gets the drive limits
    /// plus <see cref="Storage.DriveTemperatureRules.ExtraSensorMarginC"/>. Anything else passes through unchanged, so
    /// this can run after any other threshold rule without undoing it.
    /// </summary>
    public static (double Warn, double Crit) AdjustForSensor(SensorReading reading, (double Warn, double Crit) thresholds, MonitoringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(settings);
        return Storage.DriveTemperatureRules.IsExtraDriveSensor(reading)
            ? Storage.DriveTemperatureRules.Thresholds(reading, settings)
            : thresholds;
    }

    /// <summary>Same as the non-nullable overload; a sensor with no limits (null) keeps none.</summary>
    public static (double Warn, double Crit)? AdjustForSensor(SensorReading reading, (double Warn, double Crit)? thresholds, MonitoringSettings settings) =>
        thresholds is { } t ? AdjustForSensor(reading, t, settings) : null;

    /// <summary>Evaluates a temperature against thresholds: null when fine, otherwise the severity crossed.</summary>
    public static Severity? Evaluate(double value, double warn, double crit)
    {
        if (double.IsNaN(value))
        {
            return null;
        }

        if (value >= crit)
        {
            return Severity.Critical;
        }

        return value >= warn ? Severity.High : null;
    }

    /// <summary>Creates an alert when the reading crosses a threshold, otherwise null.</summary>
    public static TemperatureAlert? EvaluateReading(SensorReading reading, MonitoringSettings settings, DateTimeOffset now)
    {
        if (reading.Kind != SensorKind.Temperature || reading.Value is not { } value)
        {
            return null;
        }

        if (ThresholdsFor(reading, settings) is not { } t)
        {
            return null;
        }

        var severity = Evaluate(value, t.Warn, t.Crit);
        if (severity is null)
        {
            return null;
        }

        return new TemperatureAlert(reading.Id, reading.HardwareName, value, severity == Severity.Critical ? t.Crit : t.Warn, severity.Value, now);
    }

    /// <summary>Maps a temperature to a 0..1 "heat" fraction between 30 °C and the critical threshold.</summary>
    public static double HeatFraction(double value, double crit, double floor = 30)
    {
        if (crit <= floor)
        {
            return value >= crit ? 1 : 0;
        }

        return Math.Clamp((value - floor) / (crit - floor), 0, 1);
    }

    /// <summary>Maps LibreHardwareMonitor style hardware type names to <see cref="HardwareKind"/>.</summary>
    public static HardwareKind KindFromTypeName(string? typeName) => typeName?.ToLowerInvariant() switch
    {
        "cpu" => HardwareKind.Cpu,
        "gpunvidia" or "gpuamd" or "gpuintel" or "gpu" => HardwareKind.Gpu,
        "memory" => HardwareKind.Memory,
        "motherboard" or "superio" => HardwareKind.Motherboard,
        "storage" => HardwareKind.Storage,
        "network" => HardwareKind.Network,
        "psu" => HardwareKind.Psu,
        "cooler" or "embeddedcontroller" => HardwareKind.Cooler,
        "battery" => HardwareKind.Battery,
        _ => HardwareKind.Other,
    };

    /// <summary>Unit text for a sensor kind.</summary>
    public static string UnitFor(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.Load => "%",
        SensorKind.Clock => "MHz",
        SensorKind.Power => "W",
        SensorKind.Voltage => "V",
        SensorKind.Fan => "RPM",
        SensorKind.Flow => "L/h",
        SensorKind.Control => "%",
        SensorKind.Data => "GB",
        SensorKind.Throughput => "B/s",
        SensorKind.Level => "%",
        SensorKind.Factor => "x",
        _ => string.Empty,
    };
}
