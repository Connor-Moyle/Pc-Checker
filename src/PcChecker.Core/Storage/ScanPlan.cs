using System.Collections;

namespace PcChecker.Core.Storage;

/// <summary>How much of the drive a surface scan reads.</summary>
public enum ScanMode
{
    /// <summary>First and last few percent plus a few hundred random samples: minutes instead of hours.</summary>
    Quick,

    /// <summary>Every block from start to end. A 4 TB hard disk takes several hours.</summary>
    Full,
}

/// <summary>Options for a surface scan. Block size is in megabytes (1–64).</summary>
public sealed record SurfaceScanOptions(ScanMode Mode, int BlockSizeMb = SurfaceScanOptions.DefaultBlockSizeMb, int RandomSamples = 200, double EdgePercent = 2.0)
{
    public const int DefaultBlockSizeMb = 8;
    public const int MinBlockSizeMb = 1;
    public const int MaxBlockSizeMb = 64;

    public static SurfaceScanOptions Quick(int blockSizeMb = DefaultBlockSizeMb) => new(ScanMode.Quick, blockSizeMb);

    public static SurfaceScanOptions Full(int blockSizeMb = DefaultBlockSizeMb) => new(ScanMode.Full, blockSizeMb);

    /// <summary>Block size clamped to the supported range.</summary>
    public int EffectiveBlockSizeMb => Math.Clamp(BlockSizeMb, MinBlockSizeMb, MaxBlockSizeMb);

    public long BlockSizeBytes => EffectiveBlockSizeMb * 1024L * 1024L;
}

/// <summary>
/// Decides which blocks a surface scan reads. Full = every block; Quick = the first and last <c>EdgePercent</c> of the
/// drive (where the partition table, file system metadata and most-used data live) plus <c>RandomSamples</c> blocks
/// spread through the middle. Deterministic for a given seed so a repeated quick scan touches the same blocks.
/// </summary>
public static class ScanPlan
{
    /// <summary>Number of blocks needed to cover <paramref name="sizeBytes"/> at the given block size.</summary>
    public static long BlockCount(long sizeBytes, long blockSizeBytes)
    {
        if (sizeBytes <= 0 || blockSizeBytes <= 0)
        {
            return 0;
        }

        return (sizeBytes + blockSizeBytes - 1) / blockSizeBytes;
    }

    /// <summary>
    /// Returns the ascending, distinct block indices the scan will read. A full plan is a lazy range (no array of
    /// millions of indices for a large drive at a small block size); a quick plan is a small materialised list.
    /// </summary>
    public static IReadOnlyList<long> Build(long totalBlocks, SurfaceScanOptions options, int seed = 20240)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (totalBlocks <= 0)
        {
            return [];
        }

        if (options.Mode == ScanMode.Full)
        {
            return new BlockRange(totalBlocks);
        }

        double edgeFraction = Math.Clamp(options.EdgePercent, 0, 50) / 100.0;
        long edge = Math.Max(1, (long)Math.Ceiling(totalBlocks * edgeFraction));
        var set = new SortedSet<long>();
        for (long i = 0; i < Math.Min(edge, totalBlocks); i++)
        {
            set.Add(i);
        }

        for (long i = Math.Max(0, totalBlocks - edge); i < totalBlocks; i++)
        {
            set.Add(i);
        }

        long middleStart = edge;
        long middleEnd = totalBlocks - edge; // exclusive
        long middleCount = middleEnd - middleStart;
        int samples = Math.Max(0, options.RandomSamples);
        if (middleCount > 0 && samples > 0)
        {
            if (samples >= middleCount)
            {
                for (long i = middleStart; i < middleEnd; i++)
                {
                    set.Add(i);
                }
            }
            else
            {
                var random = new Random(seed);
                int guard = 0;
                int added = 0;
                while (added < samples && guard < samples * 20)
                {
                    guard++;
                    long candidate = middleStart + random.NextInt64(middleCount);
                    if (set.Add(candidate))
                    {
                        added++;
                    }
                }
            }
        }

        return set.ToList();
    }

    /// <summary>
    /// How many blocks <see cref="Build"/> will return, computed arithmetically so an estimate never has to materialise
    /// the plan. Equals <c>Build(...).Count</c> (the random sampling in a quick plan adds exactly the requested number of
    /// distinct middle blocks, or the whole middle when it is smaller).
    /// </summary>
    public static long PlannedCount(long totalBlocks, SurfaceScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (totalBlocks <= 0)
        {
            return 0;
        }

        if (options.Mode == ScanMode.Full)
        {
            return totalBlocks;
        }

        double edgeFraction = Math.Clamp(options.EdgePercent, 0, 50) / 100.0;
        long edge = Math.Max(1, (long)Math.Ceiling(totalBlocks * edgeFraction));
        long edges = Math.Min(totalBlocks, 2 * edge);
        long middleCount = totalBlocks - 2 * edge;
        long samples = Math.Max(0, options.RandomSamples);
        return edges + (middleCount > 0 ? Math.Min(samples, middleCount) : 0);
    }

    /// <summary>Rough wall-clock estimate for reading <paramref name="blocks"/> blocks at <paramref name="expectedMBps"/>.</summary>
    public static TimeSpan Estimate(long blocks, long blockSizeBytes, double expectedMBps)
    {
        if (blocks <= 0 || blockSizeBytes <= 0 || expectedMBps <= 0)
        {
            return TimeSpan.Zero;
        }

        double megabytes = blocks * (blockSizeBytes / BenchmarkMath.BytesPerMegabyte);
        return TimeSpan.FromSeconds(megabytes / expectedMBps);
    }

    /// <summary>Typical sequential read speed for an estimate before the scan starts.</summary>
    public static double TypicalMBps(string? mediaType, string? busType)
    {
        bool hdd = string.Equals(mediaType, "HDD", StringComparison.OrdinalIgnoreCase);
        bool usb = string.Equals(busType, "USB", StringComparison.OrdinalIgnoreCase);
        bool nvme = string.Equals(busType, "NVMe", StringComparison.OrdinalIgnoreCase);
        if (hdd)
        {
            return usb ? 110 : 160;
        }

        if (nvme)
        {
            return 2500;
        }

        return usb ? 300 : 480;
    }
}

/// <summary>The indices 0 … Count-1 without storing them. Count is clamped to int.MaxValue for the list contract.</summary>
public sealed class BlockRange : IReadOnlyList<long>
{
    private readonly long _count;

    public BlockRange(long count)
    {
        _count = Math.Max(0, count);
    }

    public long LongCount => _count;

    public int Count => (int)Math.Min(_count, int.MaxValue);

    public long this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return index;
        }
    }

    public IEnumerator<long> GetEnumerator()
    {
        for (long i = 0; i < _count; i++)
        {
            yield return i;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
