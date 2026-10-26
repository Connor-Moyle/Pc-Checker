namespace PcChecker.Core.Storage;

/// <summary>Typical figures for one class of drive (round numbers from vendor specs and reviews).</summary>
public sealed record DriveClassReference(string Name, double SeqReadMBps, double SeqWriteMBps, double Rand4kReadMBps, double Rand4kWriteMBps);

/// <summary>One row of the "measured vs typical" comparison.</summary>
public sealed record BenchmarkComparison(string Metric, double Measured, string Unit, string Verdict, double TypicalHdd, double TypicalSataSsd, double TypicalNvmeGen3, double TypicalNvmeGen4);

/// <summary>Reference numbers and comparison helpers for the disk benchmark cards.</summary>
public static class BenchmarkReference
{
    public static readonly DriveClassReference Hdd = new("Hard disk (7200 rpm)", 180, 170, 0.8, 1.5);
    public static readonly DriveClassReference SataSsd = new("SATA SSD", 540, 500, 35, 90);
    public static readonly DriveClassReference NvmeGen3 = new("NVMe PCIe 3.0", 3400, 2800, 55, 180);
    public static readonly DriveClassReference NvmeGen4 = new("NVMe PCIe 4.0", 7000, 6000, 80, 250);

    public static IReadOnlyList<DriveClassReference> All { get; } = [Hdd, SataSsd, NvmeGen3, NvmeGen4];

    /// <summary>Picks the reference class to compare against from the device's media and bus type.</summary>
    public static DriveClassReference Expected(string? mediaType, string? busType)
    {
        if (string.Equals(mediaType, "HDD", StringComparison.OrdinalIgnoreCase))
        {
            return Hdd;
        }

        if (string.Equals(busType, "NVMe", StringComparison.OrdinalIgnoreCase))
        {
            return NvmeGen3;
        }

        return SataSsd;
    }

    /// <summary>Verdict text comparing a measurement with what the drive class should reach.</summary>
    public static string Verdict(double measured, double expected)
    {
        if (measured <= 0 || expected <= 0)
        {
            return "No data";
        }

        double ratio = measured / expected;
        return ratio switch
        {
            >= 0.85 => "As expected",
            >= 0.5 => "Below typical",
            _ => "Much slower than typical",
        };
    }

    /// <summary>Builds the comparison rows for a benchmark result against the expected drive class.</summary>
    public static IReadOnlyList<BenchmarkComparison> Compare(DiskBenchmarkResult result, DriveClassReference expected)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(expected);
        return
        [
            new("Sequential read", result.SeqReadMBps, "MB/s", Verdict(result.SeqReadMBps, expected.SeqReadMBps), Hdd.SeqReadMBps, SataSsd.SeqReadMBps, NvmeGen3.SeqReadMBps, NvmeGen4.SeqReadMBps),
            new("Sequential write", result.SeqWriteMBps, "MB/s", Verdict(result.SeqWriteMBps, expected.SeqWriteMBps), Hdd.SeqWriteMBps, SataSsd.SeqWriteMBps, NvmeGen3.SeqWriteMBps, NvmeGen4.SeqWriteMBps),
            new("Random 4K read", result.Rand4kReadMBps, "MB/s", Verdict(result.Rand4kReadMBps, expected.Rand4kReadMBps), Hdd.Rand4kReadMBps, SataSsd.Rand4kReadMBps, NvmeGen3.Rand4kReadMBps, NvmeGen4.Rand4kReadMBps),
            new("Random 4K write", result.Rand4kWriteMBps, "MB/s", Verdict(result.Rand4kWriteMBps, expected.Rand4kWriteMBps), Hdd.Rand4kWriteMBps, SataSsd.Rand4kWriteMBps, NvmeGen3.Rand4kWriteMBps, NvmeGen4.Rand4kWriteMBps),
        ];
    }

    /// <summary>Summary sentence for a result, e.g. "NVMe (PCIe 4.0 class): excellent."</summary>
    public static string Summarize(DiskBenchmarkResult result, DriveClassReference expected)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(expected);
        string cls = BenchmarkMath.ClassifySequentialRead(result.SeqReadMBps);
        string seq = Verdict(result.SeqReadMBps, expected.SeqReadMBps);
        string rnd = Verdict(result.Rand4kReadMBps, expected.Rand4kReadMBps);
        if (seq == "As expected" && rnd == "As expected")
        {
            return $"{cls}: performing as a {expected.Name.ToLowerInvariant()} should. No action needed.";
        }

        if (seq.StartsWith("Much", StringComparison.Ordinal) || rnd.StartsWith("Much", StringComparison.Ordinal))
        {
            return $"{cls}: much slower than a typical {expected.Name.ToLowerInvariant()}. Check the cable/port, free up space (SSDs slow down when nearly full), look for background activity, and check drive health.";
        }

        return $"{cls}: a little below a typical {expected.Name.ToLowerInvariant()}. Usually caused by other programs using the drive, a nearly full SSD or a slower port.";
    }
}
