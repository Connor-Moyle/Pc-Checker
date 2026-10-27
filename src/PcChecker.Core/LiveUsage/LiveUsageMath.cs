using PcChecker.Core.Sensors;

namespace PcChecker.Core.LiveUsage;

/// <summary>The four parts of the memory composition bar as shares of usable memory (they add up to 1).</summary>
public readonly record struct MemoryComposition(double InUse, double Modified, double Standby, double Free);

/// <summary>The pure arithmetic behind the Live usage page.</summary>
public static class LiveUsageMath
{
    /// <summary>The engines graphed for a GPU, in Task Manager's default order.</summary>
    public static IReadOnlyList<string> PreferredEngines { get; } = ["3D", "Copy", "VideoEncode", "VideoDecode"];

    /// <summary>A percentage clamped to 0–100 (NaN becomes 0).</summary>
    public static double ClampPercent(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    /// <summary>
    /// The current processor speed: "% Processor Performance" (100 = base speed, more with turbo) times the base speed.
    /// Null when either part is missing.
    /// </summary>
    public static double? CurrentSpeedMhz(double? performancePercent, double baseSpeedMhz)
    {
        if (performancePercent is not { } perf || !double.IsFinite(perf) || perf <= 0 || !double.IsFinite(baseSpeedMhz) || baseSpeedMhz <= 0)
        {
            return null;
        }

        // A counter glitch can report absurd values; no desktop processor runs at more than four times its base speed.
        return baseSpeedMhz * Math.Min(perf, 400) / 100.0;
    }

    /// <summary>Disk active time from the "% Idle Time" counter: 100 − idle, clamped to 0–100 (idle can read above 100).</summary>
    public static double DiskActivePercent(double idlePercent) =>
        double.IsFinite(idlePercent) ? Math.Clamp(100 - idlePercent, 0, 100) : 0;

    /// <summary>Seconds per transfer as milliseconds, never negative.</summary>
    public static double ResponseMs(double secondsPerTransfer) =>
        double.IsFinite(secondsPerTransfer) && secondsPerTransfer > 0 ? secondsPerTransfer * 1000 : 0;

    /// <summary>
    /// Bytes per second between two readings of a byte counter. A counter that went backwards (adapter reset) or a
    /// zero interval gives 0 instead of a huge spike.
    /// </summary>
    public static double RatePerSecond(long previous, long current, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || current < previous)
        {
            return 0;
        }

        return (current - previous) / elapsed.TotalSeconds;
    }

    /// <summary>
    /// Memory in use the way Task Manager counts it: everything that is not modified, standby (cached) or free.
    /// </summary>
    public static long InUseBytes(long total, long modified, long standby, long free) =>
        Math.Max(0, total - Math.Max(0, modified) - Math.Max(0, standby) - Math.Max(0, free));

    /// <summary>
    /// The shares of the memory composition bar. When the parts add up to more than <paramref name="total"/> (the
    /// counters are read at slightly different moments) they are scaled down so the bar never overflows.
    /// </summary>
    public static MemoryComposition Compose(long total, long inUse, long modified, long standby, long free)
    {
        double a = Math.Max(0, inUse), m = Math.Max(0, modified), s = Math.Max(0, standby), f = Math.Max(0, free);
        double sum = a + m + s + f;
        double denominator = Math.Max(sum, total);
        if (denominator <= 0)
        {
            return new MemoryComposition(0, 0, 0, 1);
        }

        double inUseShare = a / denominator, modifiedShare = m / denominator, standbyShare = s / denominator;
        double freeShare = Math.Max(0, 1 - inUseShare - modifiedShare - standbyShare);
        return new MemoryComposition(inUseShare, modifiedShare, standbyShare, freeShare);
    }

    /// <summary>
    /// Turns "GPU Engine" values into per-adapter, per-engine-type use. Processes using the same engine are added up;
    /// when an adapter has several engines of one type (two copy engines), the busiest one is shown, as in Task Manager.
    /// Result: LUID → engine type → percent (0–100).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> AggregateGpuEngines(IEnumerable<KeyValuePair<string, double>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var perEngine = new Dictionary<(string Luid, int Phys, int Eng, string Type), double>();
        foreach (var (instance, value) in values)
        {
            if (!CounterInstanceParser.TryParseGpuEngine(instance, out var parsed) || !double.IsFinite(value) || value < 0)
            {
                continue;
            }

            var key = (parsed.Luid, parsed.Physical, parsed.Engine, parsed.EngineType);
            perEngine[key] = perEngine.GetValueOrDefault(key) + value;
        }

        var result = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in perEngine.GroupBy(p => p.Key.Luid, StringComparer.OrdinalIgnoreCase))
        {
            var types = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in adapter)
            {
                types[key.Type] = Math.Max(types.GetValueOrDefault(key.Type), Math.Min(100, value));
            }

            result[adapter.Key] = types;
        }

        return result;
    }

    /// <summary>The overall GPU use Task Manager shows: the busiest engine.</summary>
    public static double GpuUtilization(IReadOnlyDictionary<string, double>? engines) =>
        engines is null || engines.Count == 0 ? 0 : ClampPercent(engines.Values.Max());

    /// <summary>
    /// The engines to graph: the preferred four when the card has them, then the busiest of the others, up to
    /// <paramref name="count"/>.
    /// </summary>
    public static IReadOnlyList<string> ChooseEngines(IEnumerable<string> available, IReadOnlyDictionary<string, double>? usage = null, int count = 4)
    {
        ArgumentNullException.ThrowIfNull(available);
        var all = available.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var chosen = PreferredEngines.Where(p => all.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        var rest = all.Where(a => !chosen.Contains(a, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(a => usage?.GetValueOrDefault(a) ?? 0)
            .ThenBy(a => a, StringComparer.OrdinalIgnoreCase);
        chosen.AddRange(rest);
        return chosen.Take(Math.Max(0, count)).ToList();
    }

    /// <summary>
    /// The top of a throughput graph: the next "nice" step (1, 2 or 5 times a power of ten) above the highest value,
    /// never below <paramref name="floor"/>, so the scale moves in readable jumps as Task Manager's does.
    /// </summary>
    public static double NiceScale(double maxValue, double floor)
    {
        if (!double.IsFinite(maxValue) || maxValue <= floor)
        {
            return floor;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(maxValue)));
        foreach (double step in new[] { 1d, 2d, 5d, 10d })
        {
            if (step * magnitude >= maxValue)
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }

    /// <summary>
    /// The disk type from MSFT_PhysicalDisk's MediaType (3 HDD, 4 SSD) and BusType (17 NVMe, 7 USB, 12 SD, 13 MMC), with
    /// the model name as a last hint.
    /// </summary>
    public static DiskMediaKind ClassifyDisk(int? mediaType, int? busType, string? model)
    {
        if (busType == 17)
        {
            return DiskMediaKind.Nvme;
        }

        if (mediaType == 4)
        {
            return DiskMediaKind.Ssd;
        }

        if (mediaType == 3)
        {
            return DiskMediaKind.Hdd;
        }

        if (busType is 7 or 12 or 13)
        {
            return DiskMediaKind.Removable;
        }

        if (model is not null)
        {
            if (model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            {
                return DiskMediaKind.Nvme;
            }

            if (model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("Solid State", StringComparison.OrdinalIgnoreCase))
            {
                return DiskMediaKind.Ssd;
            }
        }

        return DiskMediaKind.Unknown;
    }

    /// <summary>
    /// The GPU core temperature for <paramref name="gpuName"/> from the sensor readings: a GPU temperature sensor whose
    /// hardware name matches the card ("GPU Core" preferred over hot spot or memory). Null when none matches.
    /// </summary>
    public static double? PickGpuTemperature(string gpuName, IEnumerable<SensorReading>? readings, bool onlyGpu = false)
    {
        if (readings is null || string.IsNullOrWhiteSpace(gpuName))
        {
            return null;
        }

        var temps = readings.Where(r => r.Hardware == HardwareKind.Gpu && r.Kind == SensorKind.Temperature && r.Value is { } v && double.IsFinite(v) && v > 0).ToList();
        var matching = temps.Where(r => NamesMatch(gpuName, r.HardwareName)).ToList();
        if (matching.Count == 0)
        {
            // With a single graphics card the sensor name can differ from the Windows name ("GeForce RTX 4070" vs "NVIDIA GeForce RTX 4070").
            if (!onlyGpu || temps.Select(t => t.HardwareName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            {
                return null;
            }

            matching = temps;
        }

        var best = matching.FirstOrDefault(r => r.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
            ?? matching.FirstOrDefault(r => !r.Name.Contains("Hot", StringComparison.OrdinalIgnoreCase) && !r.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            ?? matching[0];
        return best.Value;
    }

    private static bool NamesMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        string x = Simplify(a), y = Simplify(b);
        return x.Length > 0 && y.Length > 0 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal));
    }

    /// <summary>Lower-case letters and digits only, without trademark marks ("Intel(R) UHD" and "Intel UHD" match).</summary>
    private static string Simplify(string name) =>
        new(name.Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
}
