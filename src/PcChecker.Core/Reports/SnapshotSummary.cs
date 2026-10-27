using PcChecker.Core.Sensors;

namespace PcChecker.Core.Reports;

/// <summary>The handful of live numbers the Dashboard and the report headline show.</summary>
public sealed record HeadlineReadings(
    double? CpuTempC,
    double? GpuTempC,
    double? CpuLoadPercent,
    double? GpuLoadPercent,
    double? MemoryUsedPercent,
    double? MemoryUsedGb,
    double? HottestDriveC,
    double? TotalPowerW,
    string? CpuName,
    string? GpuName)
{
    public static HeadlineReadings Empty { get; } = new(null, null, null, null, null, null, null, null, null, null);
}

/// <summary>Picks headline readings out of a <see cref="SensorSnapshot"/> using the same rules everywhere. Pure.</summary>
public static class SnapshotSummary
{
    private static readonly string[] GpuPreferred = ["gpu core", "core", "gpu"];

    public static HeadlineReadings Summarize(SensorSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Readings.Count == 0)
        {
            return HeadlineReadings.Empty;
        }

        // The primary (discrete) GPU only: an integrated Radeon is listed first on Ryzen 7000/9000 otherwise.
        var readings = GpuSelection.PrimaryGpuOnly(snapshot.Readings);
        var cpuTemp = TemperatureRoles.PickCpuTemperature(readings);
        var gpuTemp = PickTemperature(readings.Where(r => TemperatureRoles.Classify(r) is not (TemperatureRole.GpuMemory or TemperatureRole.GpuVr)).ToList(), HardwareKind.Gpu, GpuPreferred);
        var cpuLoad = PickLoad(readings, HardwareKind.Cpu, "total");
        var gpuLoad = PickLoad(readings, HardwareKind.Gpu, "core");
        var memoryLoad = readings.FirstOrDefault(r => r.Hardware == HardwareKind.Memory && r.Kind == SensorKind.Load && !IsVirtual(r.Name))?.Value;
        var memoryUsed = readings.FirstOrDefault(r => r.Hardware == HardwareKind.Memory && r.Kind == SensorKind.Data && r.Name.Contains("used", StringComparison.OrdinalIgnoreCase) && !IsVirtual(r.Name))?.Value;
        var driveTemps = readings.Where(r => r.Hardware == HardwareKind.Storage && r.Kind == SensorKind.Temperature && r.Value.HasValue).Select(r => r.Value!.Value).ToList();
        var power = SumPower(readings);

        return new HeadlineReadings(
            cpuTemp?.Value,
            gpuTemp?.Value,
            cpuLoad,
            gpuLoad,
            memoryLoad,
            memoryUsed,
            driveTemps.Count > 0 ? driveTemps.Max() : null,
            power,
            cpuTemp?.HardwareName ?? readings.FirstOrDefault(r => r.Hardware == HardwareKind.Cpu)?.HardwareName,
            gpuTemp?.HardwareName ?? readings.FirstOrDefault(r => r.Hardware == HardwareKind.Gpu)?.HardwareName);
    }

    /// <summary>Temperature readings grouped for the report table, hottest first within each part.</summary>
    public static IReadOnlyList<SensorReading> Temperatures(SensorSnapshot? snapshot) =>
        snapshot is null
            ? []
            : snapshot.Readings.Where(r => r.Kind == SensorKind.Temperature && r.Value.HasValue)
                .OrderBy(r => r.Hardware).ThenByDescending(r => r.Value).ToList();

    /// <summary>The best temperature sensor for a part: a preferred name when present, else the hottest reading.</summary>
    public static SensorReading? PickTemperature(IReadOnlyList<SensorReading> readings, HardwareKind hardware, IReadOnlyList<string> preferredNames)
    {
        var candidates = readings.Where(r => r.Hardware == hardware && r.Kind == SensorKind.Temperature && r.Value.HasValue).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var name in preferredNames)
        {
            var hit = candidates.FirstOrDefault(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && !r.Name.Contains("hot spot", StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit;
            }
        }

        return candidates.MaxBy(r => r.Value);
    }

    private static double? PickLoad(IReadOnlyList<SensorReading> readings, HardwareKind hardware, string preferred)
    {
        var loads = readings.Where(r => r.Hardware == hardware && r.Kind == SensorKind.Load && r.Value.HasValue).ToList();
        if (loads.Count == 0)
        {
            return null;
        }

        return (loads.FirstOrDefault(r => r.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase)) ?? loads[0]).Value;
    }

    private static double? SumPower(IReadOnlyList<SensorReading> readings)
    {
        // Integrated graphics power is already inside the CPU package on an APU: do not count it twice.
        readings = Power.PowerMath.WithoutIntegratedGpuPower(readings).ToList();
        double sum = 0;
        bool any = false;
        foreach (var kind in new[] { HardwareKind.Cpu, HardwareKind.Gpu })
        {
            var reading = readings.Where(r => r.Hardware == kind && r.Kind == SensorKind.Power && r.Value.HasValue)
                .OrderByDescending(r => r.Name.Contains("package", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (reading?.Value is { } w)
            {
                sum += w;
                any = true;
            }
        }

        var psu = readings.FirstOrDefault(r => r.Hardware == HardwareKind.Psu && r.Kind == SensorKind.Power && r.Value.HasValue);
        if (psu?.Value is { } total)
        {
            return total;
        }

        return any ? sum : null;
    }

    private static bool IsVirtual(string name) => name.Contains("virtual", StringComparison.OrdinalIgnoreCase);
}
