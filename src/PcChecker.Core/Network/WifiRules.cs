using PcChecker.Core.Common;
using PcChecker.Core.Network.Tuning;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Network;

/// <summary>Turns the Wi-Fi facts into plain-English findings, most severe first. Pure.</summary>
public static class WifiRules
{
    public const int WeakSignalPercent = 40;
    public const int FairSignalPercent = 60;

    /// <summary>More than this many other access points overlapping your 2.4 GHz channel counts as crowded.</summary>
    public const int CrowdedNetworkCount = 3;

    /// <summary>A same-name 5/6 GHz network weaker than this is not worth switching to.</summary>
    public const int UsableFastBandSignal = 35;

    public static IReadOnlyList<NetworkFinding> Build(WifiInterfaceInfo? current, IReadOnlyList<WifiNetworkInfo> nearby, bool? powerSavingOn = null)
    {
        ArgumentNullException.ThrowIfNull(nearby);
        var findings = new List<NetworkFinding>();
        if (current is null)
        {
            return findings;
        }

        if (!current.IsConnected)
        {
            findings.Add(new NetworkFinding("wifi.notconnected", Severity.Info, "Wi-Fi is not connected",
                "The Wi-Fi adapter is on but not connected to a network, so signal and speed cannot be checked. Connect to your network and run the test again.", null, RiskLevel.Safe));
            return findings;
        }

        AddSignal(findings, current);
        AddSecurity(findings, current);
        AddCrowdedChannel(findings, current, nearby);
        AddFasterBand(findings, current, nearby);
        AddLinkRate(findings, current);

        if (powerSavingOn == true)
        {
            findings.Add(new NetworkFinding("wifi.powersave", Severity.Low, "Wi-Fi power saving is on",
                "Windows lets the Wi-Fi adapter doze between packets to save battery. That can add a few milliseconds of delay and occasional lag spikes. Setting it to Maximum Performance keeps the radio awake (slightly more battery use on laptops).",
                TuningActionIds.WifiPowerSave, RiskLevel.Safe));
        }

        return findings.OrderByDescending(f => f.Severity).ToList();
    }

    private static void AddSignal(List<NetworkFinding> findings, WifiInterfaceInfo current)
    {
        if (current.SignalPercent is not { } signal)
        {
            return;
        }

        int dbm = current.RssiDbm ?? WifiBands.SignalToDbm(signal);
        if (signal < WeakSignalPercent)
        {
            findings.Add(new NetworkFinding("wifi.signal.weak", Severity.High, "Weak Wi-Fi signal",
                $"The signal is {signal} % (about {dbm} dBm). Expect slow speeds, lag spikes and dropouts. Move closer to the router or remove obstacles between them; if that is not possible, a mesh system, a Wi-Fi extender or an Ethernet cable will help far more than any setting.",
                null, RiskLevel.Safe));
        }
        else if (signal < FairSignalPercent)
        {
            findings.Add(new NetworkFinding("wifi.signal.fair", Severity.Medium, "Wi-Fi signal is only fair",
                $"The signal is {signal} % (about {dbm} dBm). Browsing works, but speed drops and latency can jump. Walls, floors, mirrors and fish tanks weaken Wi-Fi; moving the router higher and more central usually helps.",
                null, RiskLevel.Safe));
        }
    }

    private static void AddSecurity(List<NetworkFinding> findings, WifiInterfaceInfo current)
    {
        string auth = current.Authentication?.Trim() ?? string.Empty;
        string cipher = current.Cipher?.Trim() ?? string.Empty;
        if (NetshWlanParser.OpenWords.Any(w => auth.Equals(w, StringComparison.OrdinalIgnoreCase)) && !cipher.StartsWith("WEP", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new NetworkFinding("wifi.security.open", Severity.Critical, "This Wi-Fi network has no password",
                "Anyone nearby can join an open network and may see unencrypted traffic. At home, turn on WPA2 or WPA3 security in the router settings. On public Wi-Fi, avoid logging in to sensitive sites or use a VPN.",
                null, RiskLevel.Safe));
        }
        else if (auth.StartsWith("WEP", StringComparison.OrdinalIgnoreCase) || cipher.StartsWith("WEP", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new NetworkFinding("wifi.security.wep", Severity.Critical, "WEP security can be cracked in minutes",
                "WEP was broken many years ago. Change the router's security to WPA2-Personal (AES) or WPA3-Personal. Most devices made after 2006 support WPA2.",
                null, RiskLevel.Safe));
        }
        else if (cipher.Equals("TKIP", StringComparison.OrdinalIgnoreCase) || IsWpa1(auth))
        {
            findings.Add(new NetworkFinding("wifi.security.tkip", Severity.High, "Outdated WPA/TKIP security",
                "Your network uses the original WPA or TKIP encryption. It is weak and also limits Wi-Fi speed to 54 Mbps on most adapters. Switch the router to WPA2 (AES/CCMP) or WPA3.",
                null, RiskLevel.Safe));
        }
    }

    private static void AddCrowdedChannel(List<NetworkFinding> findings, WifiInterfaceInfo current, IReadOnlyList<WifiNetworkInfo> nearby)
    {
        if (current.Band != WifiBands.Band24 || current.Channel is not { } channel)
        {
            return;
        }

        var others = ChannelCongestionCalculator.Overlapping(nearby, WifiBands.Band24, channel, current.Bssid).ToList();
        if (others.Count <= CrowdedNetworkCount)
        {
            return;
        }

        int best = ChannelCongestionCalculator.BestChannel24(nearby, current.Bssid);
        string suggestion = best != channel
            ? $" Channel {best} is the least crowded of the non-overlapping channels 1, 6 and 11 here: change it in your router's admin page (Wi-Fi or Wireless settings)."
            : " Your channel is already the best of 1, 6 and 11, so the real fix is to use 5 GHz if your router and PC support it.";
        findings.Add(new NetworkFinding("wifi.channel.crowded", Severity.Medium, $"Crowded 2.4 GHz channel ({channel})",
            $"{others.Count} other networks share or overlap channel {channel}. They take turns on the air, which slows everyone down and adds lag.{suggestion}",
            null, RiskLevel.Safe));
    }

    private static void AddFasterBand(List<NetworkFinding> findings, WifiInterfaceInfo current, IReadOnlyList<WifiNetworkInfo> nearby)
    {
        if (current.Band != WifiBands.Band24 || string.IsNullOrWhiteSpace(current.Ssid))
        {
            return;
        }

        var faster = nearby
            .Where(n => n.Ssid.Equals(current.Ssid, StringComparison.Ordinal) && (n.Band == WifiBands.Band5 || n.Band == WifiBands.Band6) && n.SignalPercent >= UsableFastBandSignal)
            .OrderByDescending(n => n.Band == WifiBands.Band6)
            .ThenByDescending(n => n.SignalPercent)
            .FirstOrDefault();
        if (faster is null)
        {
            return;
        }

        findings.Add(new NetworkFinding("wifi.band.faster-available", Severity.Medium, $"Your router also offers {faster.Band}",
            $"You are connected on 2.4 GHz, but \"{current.Ssid}\" is also visible on {faster.Band} at {faster.SignalPercent} %. {faster.Band} is usually several times faster and less crowded (with a little less range). Set the adapter's Preferred Band to {faster.Band} in the tuning section below.",
            null, RiskLevel.Safe));
    }

    private static void AddLinkRate(List<NetworkFinding> findings, WifiInterfaceInfo current)
    {
        // A weak signal already explains a low rate; only judge the rate when the signal is good.
        if (current.SignalPercent is < FairSignalPercent)
        {
            return;
        }

        double? rate = Max(current.ReceiveRateMbps, current.TransmitRateMbps);
        int generation = WifiBands.RadioGeneration(current.Radio);
        if (generation is > 0 and <= 3)
        {
            findings.Add(new NetworkFinding("wifi.radio.legacy", Severity.Medium, $"Connected with an old Wi-Fi standard ({current.Radio})",
                $"{WifiBands.DescribeRadio(current.Radio)} tops out at 54 Mbps. If your router and adapter are newer, check the adapter's wireless mode setting and the router's compatibility mode, or update the Wi-Fi driver.",
                null, RiskLevel.Safe));
            return;
        }

        double expected = ExpectedMinimumRate(current.Radio, current.Band);
        if (rate is { } r && expected > 0 && r < expected)
        {
            findings.Add(new NetworkFinding("wifi.linkrate.low", Severity.Medium, $"Link speed is low for {WifiBands.DescribeRadio(current.Radio)}",
                $"The adapter negotiated {r:0} Mbps with a good signal; this standard normally reaches at least {expected:0} Mbps. Common causes: the router uses a narrow 20 MHz channel or a compatibility mode, an outdated Wi-Fi driver, or adapter power saving. Check the adapter settings below and update the driver from the Drivers page.",
                null, RiskLevel.Safe));
        }
    }

    /// <summary>The link rate below which a connection with a good signal looks misconfigured (0 when unknown).</summary>
    public static double ExpectedMinimumRate(string? radio, string? band)
    {
        bool slowBand = band == WifiBands.Band24;
        return WifiBands.RadioGeneration(radio) switch
        {
            >= 6 => slowBand ? 100 : 300,
            5 => slowBand ? 72 : 200,
            4 => slowBand ? 65 : 130,
            _ => 0,
        };
    }

    private static bool IsWpa1(string auth) =>
        auth.StartsWith("WPA-", StringComparison.OrdinalIgnoreCase) || auth.Equals("WPA", StringComparison.OrdinalIgnoreCase);

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
