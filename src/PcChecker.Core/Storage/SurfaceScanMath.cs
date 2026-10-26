using System.Globalization;

namespace PcChecker.Core.Storage;

/// <summary>Pure arithmetic for the surface scan: slow-block detection, running median, ETA and the result summary.</summary>
public static class SurfaceScanMath
{
    /// <summary>Blocks slower than this many milliseconds are always "slow", whatever the median says.</summary>
    public const double AbsoluteSlowMs = 500;

    /// <summary>Blocks slower than this multiple of the median block time count as "slow".</summary>
    public const double SlowMultiplier = 3;

    /// <summary>A block is slow when it took longer than max(3 × median, 500 ms); see <see cref="BenchmarkMath.IsSlowBlock"/>.</summary>
    public static bool IsSlow(double blockMs, double medianMs) => BenchmarkMath.IsSlowBlock(blockMs, medianMs);

    /// <summary>Median of a sample; zero for an empty sample.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values is null || values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Time left given progress so far; null until at least one block is done.</summary>
    public static TimeSpan? Eta(long blocksDone, long blocksTotal, TimeSpan elapsed)
    {
        if (blocksDone <= 0 || blocksTotal <= 0 || blocksDone >= blocksTotal)
        {
            return blocksDone >= blocksTotal && blocksTotal > 0 ? TimeSpan.Zero : null;
        }

        double perBlock = elapsed.TotalSeconds / blocksDone;
        return TimeSpan.FromSeconds(perBlock * (blocksTotal - blocksDone));
    }

    /// <summary>MB/s for one block.</summary>
    public static double BlockMBps(long blockBytes, double blockMs) =>
        blockMs <= 0 ? 0 : blockBytes / BenchmarkMath.BytesPerMegabyte / (blockMs / 1000.0);

    /// <summary>Formats a span as "2h 05m", "12m 30s" or "45 s".</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalHours >= 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}h {1:00}m", (int)span.TotalHours, span.Minutes);
        }

        if (span.TotalMinutes >= 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}m {1:00}s", span.Minutes, span.Seconds);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0} s", (int)Math.Round(span.TotalSeconds));
    }

    /// <summary>One-paragraph verdict for a finished (or cancelled) scan.</summary>
    public static string BuildSummary(string model, ScanMode mode, long badBlocks, long slowBlocks, long blocksScanned, bool completed)
    {
        string scope = mode == ScanMode.Quick ? "quick scan (sample of the drive)" : "full scan";
        string prefix = completed ? $"{scope} of {model} finished" : $"{scope} of {model} was stopped early";
        prefix = char.ToUpperInvariant(prefix[0]) + prefix[1..];

        if (badBlocks > 0)
        {
            return $"{prefix}: {badBlocks:N0} unreadable block{(badBlocks == 1 ? string.Empty : "s")} found. Back up this drive now and plan to replace it.";
        }

        if (slowBlocks > 0 && blocksScanned > 0 && slowBlocks * 100.0 / blocksScanned >= 5)
        {
            return $"{prefix}: no unreadable blocks, but {slowBlocks:N0} of {blocksScanned:N0} blocks were slow. The drive may be wearing out or was busy during the scan; re-run when idle.";
        }

        if (slowBlocks > 0)
        {
            return $"{prefix}: no unreadable blocks. {slowBlocks:N0} slow block{(slowBlocks == 1 ? string.Empty : "s")} (normal for background activity).";
        }

        return completed
            ? $"{prefix}: every block read back cleanly."
            : $"{prefix}: no problems found in the part that was read.";
    }

    /// <summary>Plain-English next steps for the result.</summary>
    public static IReadOnlyList<string> Guidance(long badBlocks, long slowBlocks, bool isSystemDisk)
    {
        if (badBlocks > 0)
        {
            var steps = new List<string>
            {
                "Back up anything important from this drive right now, starting with files you cannot replace.",
                "Do not run repair or optimize tools on a failing drive; every extra write can make recovery harder.",
                "Check the drive's SMART details on the Health page and run the vendor's own tool for a warranty report.",
                "Replace the drive. A drive that has started to lose sectors usually gets worse over time.",
            };
            if (isSystemDisk)
            {
                steps.Add("This is the Windows drive: plan a fresh install or a cloning tool from the vendor before it fails completely.");
            }

            return steps;
        }

        if (slowBlocks > 0)
        {
            return
            [
                "Slow blocks are not errors. They often come from other programs using the drive during the scan.",
                "Re-run the scan when the PC is idle. If the same areas are slow every time, the drive may be wearing out.",
                "Keep regular backups either way.",
            ];
        }

        return ["No action needed. Keep regular backups: a surface scan checks readability today, not tomorrow."];
    }
}

/// <summary>Running median over the last N block times, so the "slow" threshold adapts to the drive and the area being read.</summary>
public sealed class RunningMedian
{
    private readonly double[] _ring;
    private int _count;
    private int _next;
    private double _cached;
    private bool _dirty;

    public RunningMedian(int window = 256)
    {
        _ring = new double[Math.Max(3, window)];
    }

    public int Count => _count;

    public void Add(double value)
    {
        _ring[_next] = value;
        _next = (_next + 1) % _ring.Length;
        if (_count < _ring.Length)
        {
            _count++;
        }

        _dirty = true;
    }

    /// <summary>Current median; zero until anything was added.</summary>
    public double Value
    {
        get
        {
            if (!_dirty)
            {
                return _cached;
            }

            _cached = SurfaceScanMath.Median(_ring.Take(_count).ToArray());
            _dirty = false;
            return _cached;
        }
    }
}
