namespace PcChecker.Core.Processes;

/// <summary>
/// Task Manager style "Power usage": a rough energy score from what a program keeps busy right now (processor, graphics
/// card and disk), turned into Very low … Very high. Windows does not report energy per program, so this is an
/// estimate, the same kind Task Manager makes.
/// </summary>
public static class PowerUsageRules
{
    private const double GpuWeight = 0.6;
    private const double DiskWeightPerMegabyte = 0.3;
    private const double DiskCapMegabytesPerSecond = 50;

    /// <summary>Upper bounds of Very low, Low, Moderate and High; anything above is Very high.</summary>
    private static readonly double[] Bounds = [0.5, 3, 10, 25];

    /// <summary>Score: CPU % + 0.6 × GPU % + 0.3 per MB/s of disk traffic (capped at 50 MB/s).</summary>
    public static double Score(double cpuPercent, double? gpuPercent, double diskBytesPerSecond)
    {
        double cpu = double.IsFinite(cpuPercent) ? Math.Max(0, cpuPercent) : 0;
        double gpu = gpuPercent is { } g && double.IsFinite(g) ? Math.Max(0, g) : 0;
        double diskMb = double.IsFinite(diskBytesPerSecond) ? Math.Clamp(diskBytesPerSecond / (1024.0 * 1024.0), 0, DiskCapMegabytesPerSecond) : 0;
        return cpu + (GpuWeight * gpu) + (DiskWeightPerMegabyte * diskMb);
    }

    public static double Score(ProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return Score(process.CpuPercent, process.GpuPercent, process.DiskBytesPerSecond);
    }

    public static double Score(ProcessGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Score(group.CpuPercent, group.GpuPercent, group.DiskBytesPerSecond);
    }

    /// <summary>The level of a score.</summary>
    public static PowerUsage Level(double score)
    {
        if (!double.IsFinite(score) || score < Bounds[0])
        {
            return PowerUsage.VeryLow;
        }

        for (int i = 1; i < Bounds.Length; i++)
        {
            if (score < Bounds[i])
            {
                return (PowerUsage)i;
            }
        }

        return PowerUsage.VeryHigh;
    }

    /// <summary>"Very low", "Low", "Moderate", "High", "Very high".</summary>
    public static string Label(PowerUsage usage) => usage switch
    {
        PowerUsage.VeryLow => "Very low",
        PowerUsage.Low => "Low",
        PowerUsage.Moderate => "Moderate",
        PowerUsage.High => "High",
        _ => "Very high",
    };
}

/// <summary>
/// "Power usage trend": the power score averaged over about two minutes, so a program that is busy now and then shows
/// a steady value. Pure: the caller passes the readings and the time between them.
/// </summary>
public sealed class PowerTrendTracker
{
    /// <summary>The time window the average covers.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private Dictionary<string, double> _averages = new(StringComparer.Ordinal);

    public int Count => _averages.Count;

    /// <summary>
    /// Adds one reading per process (key → current score) taken <paramref name="elapsed"/> after the previous one and
    /// forgets processes that are gone. A new process starts at its current score.
    /// </summary>
    public void Update(IReadOnlyDictionary<string, double> scores, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(scores);
        double alpha = Math.Clamp(elapsed.TotalSeconds / Window.TotalSeconds, 0.01, 1);
        var next = new Dictionary<string, double>(scores.Count, StringComparer.Ordinal);
        foreach (var (key, score) in scores)
        {
            next[key] = _averages.TryGetValue(key, out double previous) ? previous + (alpha * (score - previous)) : score;
        }

        _averages = next;
    }

    /// <summary>The averaged score, or null for a process not seen yet.</summary>
    public double? Get(string key) => _averages.TryGetValue(key, out double value) ? value : null;

    public void Reset() => _averages = new Dictionary<string, double>(StringComparer.Ordinal);
}
