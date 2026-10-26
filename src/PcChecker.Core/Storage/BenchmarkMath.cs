namespace PcChecker.Core.Storage;

/// <summary>Throughput and IOPS arithmetic for the disk benchmark.</summary>
public static class BenchmarkMath
{
    public const double BytesPerMegabyte = 1024 * 1024;

    public static double MegabytesPerSecond(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 || bytes <= 0 ? 0 : bytes / BytesPerMegabyte / elapsed.TotalSeconds;

    public static double Iops(long operations, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 || operations <= 0 ? 0 : operations / elapsed.TotalSeconds;

    /// <summary>Throughput of random 4 K operations expressed in MB/s.</summary>
    public static double Random4kMegabytesPerSecond(long operations, TimeSpan elapsed) =>
        MegabytesPerSecond(operations * 4096L, elapsed);

    /// <summary>Blocks slower than max(3 × median, 500 ms) count as slow.</summary>
    public static bool IsSlowBlock(double blockMs, double medianMs) => blockMs > Math.Max(medianMs * 3, 500);

    /// <summary>Rough class label from sequential read speed, for the "compare to typical" cards.</summary>
    public static string ClassifySequentialRead(double seqReadMBps) => seqReadMBps switch
    {
        >= 5000 => "NVMe (PCIe 4.0 class)",
        >= 2000 => "NVMe (PCIe 3.0 class)",
        >= 400 => "SATA SSD class",
        >= 80 => "Hard disk class",
        > 0 => "Slow (USB stick or faulty drive?)",
        _ => "Unknown",
    };
}
