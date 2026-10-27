namespace PcChecker.Core.Network;

/// <summary>
/// Counts how many access points share each channel. 2.4 GHz channels are 5 MHz apart while a network is 20 MHz wide,
/// so networks within ±4 channels overlap; on 5 and 6 GHz only the same channel number counts. Pure.
/// </summary>
public static class ChannelCongestionCalculator
{
    /// <summary>
    /// One entry per channel in use, plus the non-overlapping 2.4 GHz channels 1, 6 and 11 when any 2.4 GHz network is
    /// visible (so the page can compare them). Sorted by band, then channel.
    /// </summary>
    public static IReadOnlyList<ChannelCongestion> Calculate(IReadOnlyList<WifiNetworkInfo> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        var valid = networks.Where(n => n.Channel > 0 && n.Band != WifiBands.Unknown).ToList();
        var channels = valid.Select(n => (n.Band, n.Channel)).ToHashSet();
        if (valid.Any(n => n.Band == WifiBands.Band24))
        {
            foreach (var c in WifiBands.NonOverlapping24)
            {
                channels.Add((WifiBands.Band24, c));
            }
        }

        return channels
            .Select(c => Congestion(valid, c.Band, c.Channel))
            .OrderBy(c => BandOrder(c.Band))
            .ThenBy(c => c.Channel)
            .ToList();
    }

    /// <summary>Networks that share or overlap <paramref name="channel"/> on <paramref name="band"/>.</summary>
    public static ChannelCongestion Congestion(IReadOnlyList<WifiNetworkInfo> networks, string band, int channel, string? excludeBssid = null)
    {
        var overlapping = Overlapping(networks, band, channel, excludeBssid).ToList();
        return new ChannelCongestion(band, channel, overlapping.Count, overlapping.Count == 0 ? 0 : overlapping.Max(n => n.SignalPercent));
    }

    /// <summary>The networks that interfere with <paramref name="channel"/>; <paramref name="excludeBssid"/> leaves out your own access point.</summary>
    public static IEnumerable<WifiNetworkInfo> Overlapping(IEnumerable<WifiNetworkInfo> networks, string band, int channel, string? excludeBssid = null) =>
        networks.Where(n => n.Band == band
            && (band == WifiBands.Band24 ? WifiBands.Overlaps24(n.Channel, channel) : n.Channel == channel)
            && (excludeBssid is null || !n.Bssid.Equals(excludeBssid, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The least crowded of channels 1, 6 and 11. Strong neighbours count more than faint ones (a network at 20 %
    /// barely interferes). Ties go to the lower channel. <paramref name="excludeBssid"/> ignores your own router.
    /// </summary>
    public static int BestChannel24(IReadOnlyList<WifiNetworkInfo> networks, string? excludeBssid = null)
    {
        ArgumentNullException.ThrowIfNull(networks);
        return WifiBands.NonOverlapping24
            .Select(c => (Channel: c, Load: Overlapping(networks, WifiBands.Band24, c, excludeBssid).Sum(n => Weight(n.SignalPercent))))
            .OrderBy(x => x.Load)
            .ThenBy(x => x.Channel)
            .First().Channel;
    }

    /// <summary>Interference weight of a neighbour by signal: ≥ 60 % counts 1, 30–59 % counts 0.5, weaker 0.2.</summary>
    public static double Weight(int signalPercent) => signalPercent switch
    {
        >= 60 => 1.0,
        >= 30 => 0.5,
        _ => 0.2,
    };

    private static int BandOrder(string band) => band switch
    {
        WifiBands.Band24 => 0,
        WifiBands.Band5 => 1,
        WifiBands.Band6 => 2,
        _ => 3,
    };
}
