using PcChecker.Core.Hardware;

namespace PcChecker.Core.Sensors;

/// <summary>
/// Pure choice of "the GPU" in a sensor snapshot. LibreHardwareMonitor lists AMD graphics before NVIDIA and does not
/// leave out integrated graphics, so on every Ryzen 7000/9000 desktop the 2-CU "AMD Radeon(TM) Graphics" comes first.
/// Every picker (Dashboard, overlay, Overclock page, fan profiles, power total, watchdog) filters to the hardware this
/// helper ranks first: a discrete card by name, then one with more than about 2 GB of dedicated memory. The Sensors
/// page still shows the integrated GPU's readings.
/// </summary>
public static class GpuSelection
{
    /// <summary>Dedicated memory above this marks a graphics card (an APU's UMA frame buffer is usually smaller).</summary>
    public const double DedicatedMemoryThresholdMb = 2048;

    /// <summary>The key that groups a reading by its hardware: the LibreHardwareMonitor hardware id, else the name.</summary>
    public static string HardwareKey(SensorReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var id = reading.Id ?? string.Empty;
        if (id.StartsWith('/'))
        {
            int second = id.IndexOf('/', 1);
            int third = second < 0 ? -1 : id.IndexOf('/', second + 1);
            if (third > 0)
            {
                return id[..third];
            }
        }

        return reading.HardwareName ?? string.Empty;
    }

    /// <summary>True when the GPU hardware the readings belong to is integrated graphics.</summary>
    public static bool IsIntegrated(string? hardwareName, IEnumerable<SensorReading>? readingsOfThatHardware = null, IReadOnlyCollection<string>? integratedNames = null)
    {
        if (string.IsNullOrWhiteSpace(hardwareName))
        {
            return false;
        }

        if (integratedNames is not null && integratedNames.Contains(hardwareName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return HardwareMappings.IsIntegratedGpu(hardwareName, VendorOf(hardwareName));
    }

    /// <summary>The key (see <see cref="HardwareKey"/>) of the GPU to treat as "the GPU", or null without GPU readings.</summary>
    public static string? PrimaryGpuKey(IReadOnlyList<SensorReading> readings, IReadOnlyCollection<string>? integratedNames = null)
    {
        ArgumentNullException.ThrowIfNull(readings);
        string? bestKey = null;
        int bestScore = int.MinValue;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reading in readings)
        {
            if (reading.Hardware != HardwareKind.Gpu)
            {
                continue;
            }

            var key = HardwareKey(reading);
            if (!seen.Add(key))
            {
                continue;
            }

            int score = Score(readings, key, reading.HardwareName, integratedNames);
            if (score > bestScore)
            {
                bestScore = score;
                bestKey = key;
            }
        }

        return bestKey;
    }

    /// <summary>
    /// The readings with every GPU reading that does not belong to the primary GPU removed. Returns the input list
    /// unchanged when there is at most one GPU.
    /// </summary>
    public static IReadOnlyList<SensorReading> PrimaryGpuOnly(IReadOnlyList<SensorReading> readings, IReadOnlyCollection<string>? integratedNames = null)
    {
        ArgumentNullException.ThrowIfNull(readings);
        string? first = null;
        bool several = false;
        foreach (var r in readings)
        {
            if (r.Hardware != HardwareKind.Gpu)
            {
                continue;
            }

            var key = HardwareKey(r);
            if (first is null)
            {
                first = key;
            }
            else if (!string.Equals(first, key, StringComparison.Ordinal))
            {
                several = true;
                break;
            }
        }

        if (!several)
        {
            return readings;
        }

        var primary = PrimaryGpuKey(readings, integratedNames);
        return readings.Where(r => r.Hardware != HardwareKind.Gpu || string.Equals(HardwareKey(r), primary, StringComparison.Ordinal)).ToList();
    }

    /// <summary>Same as <see cref="PrimaryGpuOnly(IReadOnlyList{SensorReading}, IReadOnlyCollection{string}?)"/> for a snapshot.</summary>
    public static SensorSnapshot? PrimaryGpuOnly(SensorSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Readings.Count == 0)
        {
            return snapshot;
        }

        var filtered = PrimaryGpuOnly(snapshot.Readings);
        return ReferenceEquals(filtered, snapshot.Readings) ? snapshot : snapshot with { Readings = filtered };
    }

    /// <summary>The dedicated video memory a GPU reports through "GPU Memory Total" or "D3D Dedicated Memory Total", in MB.</summary>
    public static double? DedicatedMemoryMb(IEnumerable<SensorReading> readingsOfThatHardware)
    {
        ArgumentNullException.ThrowIfNull(readingsOfThatHardware);
        double? best = null;
        foreach (var r in readingsOfThatHardware)
        {
            if (r.Kind != SensorKind.Data || r.Value is not { } v || double.IsNaN(v))
            {
                continue;
            }

            bool total = r.Name.Contains("GPU Memory Total", StringComparison.OrdinalIgnoreCase)
                || r.Name.Contains("Dedicated Memory Total", StringComparison.OrdinalIgnoreCase);
            if (!total)
            {
                continue;
            }

            double mb = Common.OverlaySensorMatcher.ToMegabytes(v, r.Unit);
            best = best is null ? mb : Math.Max(best.Value, mb);
        }

        return best;
    }

    private static int Score(IReadOnlyList<SensorReading> readings, string key, string hardwareName, IReadOnlyCollection<string>? integratedNames)
    {
        int score = 0;
        bool integrated = IsIntegrated(hardwareName, null, integratedNames);
        score += integrated ? -2 : 2;
        var memory = DedicatedMemoryMb(readings.Where(r => r.Hardware == HardwareKind.Gpu && string.Equals(HardwareKey(r), key, StringComparison.Ordinal)));
        if (memory is > DedicatedMemoryThresholdMb)
        {
            score += 1;
        }

        return score;
    }

    private static string VendorOf(string name)
    {
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        return name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" : string.Empty;
    }
}
