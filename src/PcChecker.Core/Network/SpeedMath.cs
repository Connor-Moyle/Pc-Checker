namespace PcChecker.Core.Network;

/// <summary>Throughput, percentiles, jitter and the bufferbloat grade for the speed test. Pure.</summary>
public static class SpeedMath
{
    /// <summary>Megabits per second for <paramref name="bytes"/> moved in <paramref name="elapsed"/>; null when no time passed.</summary>
    public static double? Mbps(long bytes, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || bytes < 0)
        {
            return null;
        }

        return bytes * 8.0 / elapsed.TotalSeconds / 1_000_000.0;
    }

    /// <summary>
    /// Throughput over the measurement window, ignoring the TCP ramp-up: bytes counted after <paramref name="rampSeconds"/>
    /// divided by the time after it. When the whole transfer was shorter than the ramp, the full window is used.
    /// <paramref name="samples"/> are (seconds since start, cumulative bytes) pairs in time order.
    /// </summary>
    public static double? WindowedMbps(IReadOnlyList<(double Seconds, long Bytes)> samples, double rampSeconds = 0.5)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2)
        {
            return samples.Count == 1 && samples[0].Seconds > 0 ? Mbps(samples[0].Bytes, TimeSpan.FromSeconds(samples[0].Seconds)) : null;
        }

        var last = samples[^1];
        if (last.Seconds <= rampSeconds + 0.25)
        {
            return Mbps(last.Bytes, TimeSpan.FromSeconds(last.Seconds));
        }

        // The last sample taken at or before the ramp ends is the baseline.
        var baseline = samples.LastOrDefault(s => s.Seconds <= rampSeconds);
        if (baseline == default)
        {
            baseline = samples[0];
        }

        return Mbps(last.Bytes - baseline.Bytes, TimeSpan.FromSeconds(last.Seconds - baseline.Seconds));
    }

    /// <summary>Linear-interpolated percentile (0–100) of the values; null for an empty list.</summary>
    public static double? Percentile(IReadOnlyList<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        double p = Math.Clamp(percentile, 0, 100) / 100.0;
        double position = p * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    public static double? Median(IReadOnlyList<double> values) => Percentile(values, 50);

    /// <summary>Average absolute difference between consecutive samples (how much the delay wobbles); null with fewer than two.</summary>
    public static double? Jitter(IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2)
        {
            return null;
        }

        double sum = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            sum += Math.Abs(samples[i] - samples[i - 1]);
        }

        return sum / (samples.Count - 1);
    }

    /// <summary>
    /// Bufferbloat grade from the extra delay under load (loaded − idle latency): under 5 ms A+, under 30 A, under 60 B,
    /// under 200 C, under 400 D, otherwise F. "?" when either value is missing.
    /// </summary>
    public static string BufferbloatGrade(double? idleMs, double? loadedMs)
    {
        if (idleMs is null || loadedMs is null)
        {
            return "?";
        }

        double extra = Math.Max(0, loadedMs.Value - idleMs.Value);
        return extra switch
        {
            < 5 => "A+",
            < 30 => "A",
            < 60 => "B",
            < 200 => "C",
            < 400 => "D",
            _ => "F",
        };
    }

    /// <summary>One-sentence explanation of a bufferbloat grade.</summary>
    public static string DescribeGrade(string grade) => grade switch
    {
        "A+" or "A" => "Excellent: your connection stays responsive even when it is busy. Games and calls will not lag during downloads.",
        "B" => "Good: a little extra delay when the line is busy. Most people will not notice.",
        "C" => "Fair: video calls and games may lag while someone else downloads or uploads. A router with SQM / Smart Queue (often called QoS) fixes this.",
        "D" or "F" => "Poor: the router's buffers fill up under load, so everything gets slow while the line is busy. Turn on SQM / Smart Queue (QoS) in the router, or ask your provider about it.",
        _ => "Not measured.",
    };

    /// <summary>"1.2 GB" style text for data used by a test.</summary>
    public static string DataUsedText(long downloaded, long uploaded) => Common.ByteSize.Format(Math.Max(0, downloaded) + Math.Max(0, uploaded));
}
