using PcChecker.Core.Sensors;

namespace PcChecker.Core.Power;

/// <summary>Where a power reading counts in the total.</summary>
public enum PowerSource
{
    CpuPackage,
    Gpu,
    Psu,
    Battery,
    Other,
    Ignore,
}

/// <summary>One named contributor to the power total, for the per-component table.</summary>
public sealed record PowerComponent(string Name, string HardwareName, PowerSource Source, double Watts);

/// <summary>Sum of the power sensors in one snapshot.</summary>
public sealed record PowerBreakdown(
    double? CpuPackageW,
    double? GpuW,
    double? OtherW,
    double? PsuW,
    double? BatteryW,
    IReadOnlyList<PowerComponent> Components)
{
    public static PowerBreakdown Empty { get; } = new(null, null, null, null, null, []);

    /// <summary>True when at least one component reported a wattage.</summary>
    public bool HasAnyReading => CpuPackageW is not null || GpuW is not null || OtherW is not null || PsuW is not null;
}

/// <summary>Pure power math: classifying sensor readings, estimating the total, energy integration and cost.</summary>
public static class PowerMath
{
    /// <summary>Multiplier applied to the sum of known component wattages to cover losses and unmeasured parts.</summary>
    public const double EstimateFactor = 1.15;

    /// <summary>Baseline watts added for fans, drives, RAM and the motherboard when no PSU sensor exists.</summary>
    public const double EstimateBaselineW = 25;

    /// <summary>Longest gap between two samples that is still integrated (sleep/hibernate gaps are skipped).</summary>
    public static readonly TimeSpan MaxIntegrationGap = TimeSpan.FromSeconds(60);

    /// <summary>Decides how one reading counts. Only <see cref="SensorKind.Power"/> readings are considered.</summary>
    public static PowerSource Classify(SensorReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.Kind != SensorKind.Power)
        {
            return PowerSource.Ignore;
        }

        string name = reading.Name ?? string.Empty;
        switch (reading.Hardware)
        {
            case HardwareKind.Cpu:
                // "CPU Package" (Intel) / "Package" (AMD) already include cores, graphics and memory controller.
                return Contains(name, "package") ? PowerSource.CpuPackage : PowerSource.Ignore;

            case HardwareKind.Gpu:
                // NVIDIA: "GPU Power"; AMD: "GPU Package" (+ "GPU Core", "GPU SoC" which are parts of it); Intel: "GPU Power".
                if (Contains(name, "package") || Contains(name, "board") || Contains(name, "total"))
                {
                    return PowerSource.Gpu;
                }

                return Contains(name, "core") || Contains(name, "soc") || Contains(name, "memory") ? PowerSource.Ignore : PowerSource.Gpu;

            case HardwareKind.Psu:
                return Contains(name, "in") && !Contains(name, "out") ? PowerSource.Ignore : PowerSource.Psu;

            case HardwareKind.Battery:
                return PowerSource.Battery;

            default:
                return PowerSource.Other;
        }
    }

    /// <summary>Aggregates every power reading of a snapshot into a breakdown.</summary>
    public static PowerBreakdown Aggregate(IEnumerable<SensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var components = new List<PowerComponent>();
        var cpuByHardware = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var cpuFallback = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var gpuByHardware = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var psuByHardware = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double? other = null, battery = null;

        foreach (var reading in readings)
        {
            if (reading.Kind != SensorKind.Power || reading.Value is not { } watts || double.IsNaN(watts) || watts < 0)
            {
                continue;
            }

            var source = Classify(reading);
            if (reading.Hardware == HardwareKind.Cpu && source == PowerSource.Ignore)
            {
                // Remember the largest CPU reading in case no package sensor exists at all.
                cpuFallback[reading.HardwareName] = Math.Max(cpuFallback.GetValueOrDefault(reading.HardwareName), watts);
            }

            if (source == PowerSource.Ignore)
            {
                continue;
            }

            components.Add(new PowerComponent(reading.Name, reading.HardwareName, source, watts));
            switch (source)
            {
                case PowerSource.CpuPackage:
                    cpuByHardware[reading.HardwareName] = Math.Max(cpuByHardware.GetValueOrDefault(reading.HardwareName), watts);
                    break;
                case PowerSource.Gpu:
                    gpuByHardware[reading.HardwareName] = Math.Max(gpuByHardware.GetValueOrDefault(reading.HardwareName), watts);
                    break;
                case PowerSource.Psu:
                    psuByHardware[reading.HardwareName] = Math.Max(psuByHardware.GetValueOrDefault(reading.HardwareName), watts);
                    break;
                case PowerSource.Battery:
                    battery = (battery ?? 0) + watts;
                    break;
                default:
                    other = (other ?? 0) + watts;
                    break;
            }
        }

        foreach (var (hardware, watts) in cpuFallback)
        {
            if (!cpuByHardware.ContainsKey(hardware))
            {
                cpuByHardware[hardware] = watts;
                components.Add(new PowerComponent("CPU (largest reading)", hardware, PowerSource.CpuPackage, watts));
            }
        }

        double? cpu = cpuByHardware.Count == 0 ? null : cpuByHardware.Values.Sum();
        double? gpu = gpuByHardware.Count == 0 ? null : gpuByHardware.Values.Sum();
        double? psu = psuByHardware.Count == 0 ? null : psuByHardware.Values.Sum();
        return new PowerBreakdown(cpu, gpu, other, psu, battery, components);
    }

    /// <summary>
    /// Total system draw. A PSU sensor is authoritative; otherwise the known parts are scaled by
    /// <see cref="EstimateFactor"/> plus <see cref="EstimateBaselineW"/> and the result is flagged as an estimate.
    /// </summary>
    public static (double? TotalW, bool IsEstimate) EstimateTotal(double? cpuW, double? gpuW, double? otherW, double? psuW)
    {
        if (psuW is { } psu && psu > 0)
        {
            return (Math.Round(psu, 1), false);
        }

        if (cpuW is null && gpuW is null && otherW is null)
        {
            return (null, true);
        }

        double known = (cpuW ?? 0) + (gpuW ?? 0) + (otherW ?? 0);
        return (Math.Round(known * EstimateFactor + EstimateBaselineW, 1), true);
    }

    /// <summary>Energy (Wh) used over <paramref name="elapsed"/> at a steady <paramref name="watts"/>; gaps beyond the limit count as zero.</summary>
    public static double IntegrateEnergyWh(double? watts, TimeSpan elapsed)
    {
        if (watts is not { } w || double.IsNaN(w) || w <= 0 || elapsed <= TimeSpan.Zero || elapsed > MaxIntegrationGap)
        {
            return 0;
        }

        return w * elapsed.TotalHours;
    }

    /// <summary>Cost per hour at <paramref name="pricePerKwh"/> for a steady draw.</summary>
    public static double CostPerHour(double? watts, double pricePerKwh)
    {
        if (watts is not { } w || double.IsNaN(w) || w <= 0 || pricePerKwh <= 0)
        {
            return 0;
        }

        return w / 1000.0 * pricePerKwh;
    }

    /// <summary>Cost per day if the current draw continued for 24 hours.</summary>
    public static double CostPerDay(double? watts, double pricePerKwh) => CostPerHour(watts, pricePerKwh) * 24;

    /// <summary>Cost of a session's energy so far.</summary>
    public static double SessionCost(double energyWh, double pricePerKwh) =>
        energyWh <= 0 || pricePerKwh <= 0 ? 0 : energyWh / 1000.0 * pricePerKwh;

    /// <summary>How long a battery lasts at the current discharge rate; null when not discharging.</summary>
    public static TimeSpan? BatteryTimeRemaining(double? remainingMwh, double? dischargeMw)
    {
        if (remainingMwh is not { } remaining || dischargeMw is not { } rate || remaining <= 0 || rate <= 0)
        {
            return null;
        }

        double hours = remaining / rate;
        return hours > 72 ? null : TimeSpan.FromHours(hours);
    }

    /// <summary>Share of each component in the total (0..1), for the bar in the component table.</summary>
    public static double Share(double watts, double? totalW) =>
        totalW is { } t && t > 0 && watts > 0 ? Math.Clamp(watts / t, 0, 1) : 0;

    private static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
