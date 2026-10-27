namespace PcChecker.Core.History;

/// <summary>
/// The metric ids written to the history files, with their unit, roll-up rule and display order. Free space is one
/// metric per drive: <c>disk.free:C:</c>.
/// </summary>
public static class HistoryMetrics
{
    public const string CpuTemp = "cpu.temp";
    public const string GpuTemp = "gpu.temp";
    public const string CpuLoad = "cpu.load";
    public const string GpuLoad = "gpu.load";
    public const string Power = "power";
    public const string Ram = "ram";
    public const string Health = "health";
    public const string DiskFreePrefix = "disk.free:";

    private static readonly string[] Fixed = [CpuTemp, GpuTemp, CpuLoad, GpuLoad, Power, Ram, Health];

    /// <summary>The free-space metric for a drive: "c:\" → "disk.free:C:".</summary>
    public static string DiskFree(string drive) => DiskFreePrefix + NormalizeDrive(drive);

    public static bool IsDiskFree(string metric) => metric.StartsWith(DiskFreePrefix, StringComparison.Ordinal);

    public static bool IsTemperature(string metric) => metric is CpuTemp or GpuTemp;

    /// <summary>"disk.free:C:" → "C:", otherwise null.</summary>
    public static string? DriveOf(string metric) => IsDiskFree(metric) ? metric[DiskFreePrefix.Length..] : null;

    /// <summary>"c:\", "C:" or "c" → "C:". Other names (mount folders) are kept as they are, without a trailing slash.</summary>
    public static string NormalizeDrive(string drive)
    {
        var d = (drive ?? string.Empty).Trim().TrimEnd('\\', '/');
        if (d.Length == 1 && char.IsLetter(d[0]))
        {
            d += ":";
        }

        return d.Length == 2 && d[1] == ':' ? d.ToUpperInvariant() : d;
    }

    /// <summary>A metric id is stored as a JSON key: printable, short and without quotes or control characters.</summary>
    public static bool IsValidId(string? metric) =>
        !string.IsNullOrEmpty(metric) && metric.Length <= 64 && metric.All(c => c >= ' ' && c != '"' && c != '\\' && c < 0x7F);

    public static HistoryAggregation AggregationOf(string metric) => metric switch
    {
        CpuTemp or GpuTemp => HistoryAggregation.Max,
        Health => HistoryAggregation.Min,
        _ when IsDiskFree(metric) => HistoryAggregation.Last,
        _ => HistoryAggregation.Average,
    };

    public static string UnitOf(string metric) => metric switch
    {
        CpuTemp or GpuTemp => "°C",
        CpuLoad or GpuLoad or Ram => "%",
        Power => "W",
        Health => "/ 100",
        _ when IsDiskFree(metric) => "GB",
        _ => string.Empty,
    };

    /// <summary>Plain English name, used for CSV headers and as the translation key on the page.</summary>
    public static string LabelOf(string metric) => metric switch
    {
        CpuTemp => "CPU temperature",
        GpuTemp => "GPU temperature",
        CpuLoad => "CPU load",
        GpuLoad => "GPU load",
        Power => "Power draw",
        Ram => "Memory in use",
        Health => "Health score",
        _ when IsDiskFree(metric) => "Free space on " + DriveOf(metric),
        _ => metric,
    };

    /// <summary>Sorts metric ids for display: the fixed metrics first, then the drives alphabetically.</summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> metrics) =>
        metrics.Distinct(StringComparer.Ordinal)
            .OrderBy(m => Array.IndexOf(Fixed, m) is var i && i >= 0 ? i : Fixed.Length)
            .ThenBy(m => m, StringComparer.Ordinal)
            .ToList();
}
