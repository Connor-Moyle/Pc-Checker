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
        SensorReading? worst = null;
        double worstExcess = double.NegativeInfinity;
        foreach (var reading in temperatures)
        {
            if (reading.Kind != SensorKind.Temperature || reading.Value is not { } value || double.IsNaN(value))
            {
                continue;
            }

            if (reading.Hardware is not (HardwareKind.Cpu or HardwareKind.Gpu or HardwareKind.Motherboard))
            {
                continue;
            }

            double excess = value - CriticalThreshold(reading.Hardware, monitoring);
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
        foreach (var reading in temperatures)
        {
            if (reading.Kind != SensorKind.Temperature || reading.Value is not { } value)
            {
                continue;
            }

            if (reading.Hardware is not (HardwareKind.Cpu or HardwareKind.Gpu or HardwareKind.Motherboard))
            {
                continue;
            }

            if (value >= WarningThreshold(reading.Hardware, monitoring))
            {
                return false;
            }
        }

        return true;
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
