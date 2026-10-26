using PcChecker.Core.Common;

namespace PcChecker.Core.Latency;

/// <summary>Turns raw ping samples into a <see cref="NetworkLatencyResult"/> and grades results. Pure.</summary>
public static class NetworkLatencyMath
{
    /// <summary>Round trips above this count as a slow connection to a public DNS server.</summary>
    public const double SlowMs = 60;

    /// <summary>Jitter above this makes a connection feel unstable in games and calls.</summary>
    public const double UnstableJitterMs = 10;

    /// <summary>
    /// Summarizes one target. <paramref name="roundTripsMs"/> holds the successful replies; <paramref name="lost"/> the
    /// number of requests that got no reply. Averages are rounded to two decimals.
    /// </summary>
    public static NetworkLatencyResult Summarize(string target, string label, IReadOnlyList<double> roundTripsMs, int lost)
    {
        ArgumentNullException.ThrowIfNull(roundTripsMs);
        int sent = roundTripsMs.Count + Math.Max(0, lost);
        double loss = sent == 0 ? 100 : 100.0 * Math.Max(0, lost) / sent;
        if (roundTripsMs.Count == 0)
        {
            return new NetworkLatencyResult(target, label, null, null, null, null, Math.Round(loss, 1), sent);
        }

        return new NetworkLatencyResult(
            target,
            label,
            Math.Round(roundTripsMs.Average(), 2),
            Math.Round(roundTripsMs.Min(), 2),
            Math.Round(roundTripsMs.Max(), 2),
            LatencyRules.Jitter(roundTripsMs) is { } j ? Math.Round(j, 2) : null,
            Math.Round(loss, 1),
            sent);
    }

    /// <summary>Good / Warning / Critical for the colored badge next to a ping result.</summary>
    public static HealthStatus Grade(NetworkLatencyResult result)
    {
        if (result is null || result.AvgMs is null)
        {
            return result is { LossPercent: >= 100 } ? HealthStatus.Critical : HealthStatus.Unknown;
        }

        if (result.LossPercent > 0 || result.JitterMs is > UnstableJitterMs)
        {
            return HealthStatus.Critical;
        }

        if (result.AvgMs > SlowMs || result.JitterMs is > UnstableJitterMs / 2)
        {
            return HealthStatus.Warning;
        }

        return HealthStatus.Good;
    }

    /// <summary>Short plain-English verdict for a ping result.</summary>
    public static string Describe(NetworkLatencyResult result) => Grade(result) switch
    {
        HealthStatus.Good => "Stable",
        HealthStatus.Warning => result.AvgMs > SlowMs ? "Slow" : "Some jitter",
        HealthStatus.Critical => result.LossPercent >= 100 ? "No reply" : result.LossPercent > 0 ? "Packet loss" : "Unstable",
        _ => "Not tested",
    };
}
