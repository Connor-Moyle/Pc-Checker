using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Fans;

/// <summary>Pure safety logic: when software-controlled fans must be forced to full speed and when they may relax again.</summary>
public static class FanSafetyRules
{
    /// <summary>The critical threshold that applies to a temperature sensor, from the monitoring settings.</summary>
    public static double CriticalThreshold(HardwareKind hardware, MonitoringSettings monitoring)
    {
        ArgumentNullException.ThrowIfNull(monitoring);
        return hardware switch
        {
            HardwareKind.Cpu => monitoring.CpuCritC,
            HardwareKind.Gpu => monitoring.GpuCritC,
            HardwareKind.Storage => monitoring.DiskCritC,
            _ => Math.Max(monitoring.CpuCritC, monitoring.GpuCritC),
        };
    }

    /// <summary>The warning threshold that applies to a temperature sensor, from the monitoring settings.</summary>
    public static double WarningThreshold(HardwareKind hardware, MonitoringSettings monitoring)
    {
        ArgumentNullException.ThrowIfNull(monitoring);
        return hardware switch
        {
            HardwareKind.Cpu => monitoring.CpuWarnC,
            HardwareKind.Gpu => monitoring.GpuWarnC,
            HardwareKind.Storage => monitoring.DiskWarnC,
            _ => Math.Max(monitoring.CpuWarnC, monitoring.GpuWarnC),
        };
    }

    /// <summary>
    /// Returns the hottest CPU/GPU/motherboard reading that is at or above its critical threshold, or null when
    /// everything is below. Storage temperatures are ignored: case fans cannot fix a hot drive quickly enough to matter.
    /// </summary>
    public static SensorReading? FindCriticalReading(IEnumerable<SensorReading> temperatures, MonitoringSettings monitoring)
    {
        ArgumentNullException.ThrowIfNull(temperatures);
        var list = temperatures as IReadOnlyCollection<SensorReading> ?? temperatures.ToList();
        string? cpuName = TemperatureRoles.CpuNameOf(list);
        SensorReading? worst = null;
        double worstExcess = double.NegativeInfinity;
        foreach (var reading in list)
        {
            if (reading.Kind != SensorKind.Temperature || reading.Value is not { } value || double.IsNaN(value))
            {
                continue;
            }

            if (reading.Hardware is not (HardwareKind.Cpu or HardwareKind.Gpu or HardwareKind.Motherboard))
            {
                continue;
            }

            if (LimitsFor(reading, monitoring, cpuName) is not { } limits)
            {
                continue;
            }

            double excess = value - limits.Crit;
            if (excess >= 0 && excess > worstExcess)
            {
                worstExcess = excess;
                worst = reading;
            }
        }

        return worst;
    }

    /// <summary>
    /// Once fans were forced to 100 %, they stay there until every CPU/GPU/motherboard temperature is back under
    /// its warning threshold, so the fans do not flap around the critical line.
    /// </summary>
    public static bool CanRelease(IEnumerable<SensorReading> temperatures, MonitoringSettings monitoring)
    {
        ArgumentNullException.ThrowIfNull(temperatures);
        var list = temperatures as IReadOnlyCollection<SensorReading> ?? temperatures.ToList();
        string? cpuName = TemperatureRoles.CpuNameOf(list);
        foreach (var reading in list)
        {
            if (reading.Kind != SensorKind.Temperature || reading.Value is not { } value)
            {
                continue;
            }

            if (reading.Hardware is not (HardwareKind.Cpu or HardwareKind.Gpu or HardwareKind.Motherboard))
            {
                continue;
            }

            if (LimitsFor(reading, monitoring, cpuName) is { } limits && value >= limits.Warn)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The limits a reading is held to, by role: hot spot and memory sensors have their own (higher) limits, the
    /// Ryzen 7000/9000 CPU limits follow their 95 °C design point, and raw "Core (Tctl)" is ignored (it carries an
    /// offset and always comes with a real Tdie reading). Motherboard sensors use the CPU/GPU maximum. Integrated GPU
    /// sensors use the limits of <paramref name="cpuName"/>, the CPU in the same snapshot.
    /// </summary>
    public static (double Warn, double Crit)? LimitsFor(SensorReading reading, MonitoringSettings monitoring, string? cpuName = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(monitoring);
        return reading.Hardware is HardwareKind.Cpu or HardwareKind.Gpu
            ? TemperatureRoles.ThresholdsFor(reading, monitoring, cpuName)
            : (WarningThreshold(reading.Hardware, monitoring), CriticalThreshold(reading.Hardware, monitoring));
    }

    /// <summary>Clamps a requested manual percentage to the allowed range: the configured minimum (unless explicitly allowed) up to 100.</summary>
    public static double ClampManual(double percent, double minimumPercent, bool allowBelowMinimum, double hardwareMin = 0, double hardwareMax = 100)
    {
        double floor = allowBelowMinimum ? hardwareMin : Math.Max(hardwareMin, minimumPercent);
        double ceiling = hardwareMax <= floor ? 100 : hardwareMax;
        if (double.IsNaN(percent))
        {
            return floor;
        }

        return Math.Clamp(Math.Round(percent), floor, ceiling);
    }
}
