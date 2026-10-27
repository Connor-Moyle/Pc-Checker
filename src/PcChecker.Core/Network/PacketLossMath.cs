using PcChecker.Core.Common;

namespace PcChecker.Core.Network;

/// <summary>Summarises ping samples into a <see cref="PacketLossResult"/> and grades it. Pure.</summary>
public static class PacketLossMath
{
    /// <summary>Any loss above this is a real problem (games rubber-band, calls break up).</summary>
    public const double BadLossPercent = 2;

    /// <summary>Jitter above this makes calls and games feel uneven.</summary>
    public const double HighJitterMs = 30;

    /// <summary>
    /// <paramref name="roundTripsMs"/> are the replies in the order they were sent; <paramref name="lost"/> counts the
    /// requests without a reply. Times are rounded to two decimals, loss to one.
    /// </summary>
    public static PacketLossResult Summarize(string target, string label, IReadOnlyList<double> roundTripsMs, int lost)
    {
        ArgumentNullException.ThrowIfNull(roundTripsMs);
        int received = roundTripsMs.Count;
        int sent = received + Math.Max(0, lost);
        double loss = sent == 0 ? 0 : Math.Round(100.0 * Math.Max(0, lost) / sent, 1);
        if (received == 0)
        {
            return new PacketLossResult(target, label, sent, 0, sent == 0 ? 0 : 100, null, null, null, null, null);
        }

        return new PacketLossResult(
            target,
            label,
            sent,
            received,
            loss,
            Math.Round(roundTripsMs.Average(), 2),
            Math.Round(roundTripsMs.Min(), 2),
            Math.Round(roundTripsMs.Max(), 2),
            SpeedMath.Jitter(roundTripsMs) is { } j ? Math.Round(j, 2) : null,
            SpeedMath.Percentile(roundTripsMs, 95) is { } p ? Math.Round(p, 2) : null);
    }

    /// <summary>Good (no loss, steady), Warning (a little loss or jitter), Critical (2 % loss or more, or no replies).</summary>
    public static HealthStatus Grade(PacketLossResult? result)
    {
        if (result is null || result.Sent == 0)
        {
            return HealthStatus.Unknown;
        }

        if (result.Received == 0 || result.LossPercent >= BadLossPercent)
        {
            return HealthStatus.Critical;
        }

        if (result.LossPercent > 0 || result.JitterMs is > HighJitterMs)
        {
            return HealthStatus.Warning;
        }

        return HealthStatus.Good;
    }

    /// <summary>Short verdict for the colored badge.</summary>
    public static string Describe(PacketLossResult? result) => Grade(result) switch
    {
        HealthStatus.Good => "No loss",
        HealthStatus.Warning => result!.LossPercent > 0 ? "Minor loss" : "Jittery",
        HealthStatus.Critical => result!.Received == 0 ? "No reply" : "Packet loss",
        _ => "Not tested",
    };

    /// <summary>
    /// Where the loss most likely starts: at the router (Wi-Fi or cable inside the home) when the router itself loses
    /// packets, otherwise beyond it (the provider or the internet).
    /// </summary>
    public static string? Diagnose(IReadOnlyList<PacketLossResult> results, string gatewayTarget = "gateway")
    {
        ArgumentNullException.ThrowIfNull(results);
        var router = results.FirstOrDefault(r => r.Target.Equals(gatewayTarget, StringComparison.OrdinalIgnoreCase));
        var internet = results.Where(r => !r.Target.Equals(gatewayTarget, StringComparison.OrdinalIgnoreCase) && r.Sent > 0).ToList();
        bool routerLoss = router is { Received: > 0, LossPercent: > 0 };
        bool internetLoss = internet.Count > 0 && internet.All(r => r.LossPercent > 0);
        if (routerLoss)
        {
            return "Packets are already lost between this PC and your router, so the problem is inside your home: usually Wi-Fi signal or interference, sometimes a bad cable or port.";
        }

        if (internetLoss)
        {
            return "Your router answers every ping, but packets to the internet get lost. The problem is beyond your home network (your provider or its equipment); contact them if it keeps happening.";
        }

        if (router is { Received: 0, Sent: > 0 } && internet.Any(r => r.Received > 0))
        {
            return "Your router does not answer ping (many block it on purpose), but the internet does, so this is usually not a fault.";
        }

        return null;
    }
}
