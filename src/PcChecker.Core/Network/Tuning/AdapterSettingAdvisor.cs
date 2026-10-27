using System.Text.RegularExpressions;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Network.Tuning;

/// <summary>The kinds of adapter properties PC Checker understands.</summary>
public enum AdapterSettingKind
{
    Unknown,
    PreferredBand,
    RoamingAggressiveness,
    MimoPowerSave,
    TransmitPower,
    WirelessMode,
    ChannelWidth24,
    ChannelWidth5,
    LeaveDefault,
    EnergyEfficientEthernet,
    EthernetPowerSaving,
    SpeedDuplex,
}

/// <summary>
/// Curated recommendations for Wi-Fi and Ethernet driver properties. Drivers name the same thing differently, so a
/// property is matched by registry keyword first and display name second. Nothing is recommended for properties PC
/// Checker does not know (they are shown read-only in Advanced mode). Pure.
/// </summary>
public static partial class AdapterSettingAdvisor
{
    private static readonly Dictionary<string, AdapterSettingKind> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RoamingPreferredBandType"] = AdapterSettingKind.PreferredBand,
        ["PreferredBand"] = AdapterSettingKind.PreferredBand,
        ["BandPreference"] = AdapterSettingKind.PreferredBand,
        ["*PreferredBand"] = AdapterSettingKind.PreferredBand,
        ["RoamAggressiveness"] = AdapterSettingKind.RoamingAggressiveness,
        ["RoamingAggressiveness"] = AdapterSettingKind.RoamingAggressiveness,
        ["RoamTrigger"] = AdapterSettingKind.RoamingAggressiveness,
        ["MIMOPowerSaveMode"] = AdapterSettingKind.MimoPowerSave,
        ["SmPowerSave"] = AdapterSettingKind.MimoPowerSave,
        ["IbssTxPower"] = AdapterSettingKind.TransmitPower,
        ["TxPower"] = AdapterSettingKind.TransmitPower,
        ["TransmitPower"] = AdapterSettingKind.TransmitPower,
        ["TxPowerLevel"] = AdapterSettingKind.TransmitPower,
        ["IEEE11nMode"] = AdapterSettingKind.WirelessMode,
        ["ChannelWidth24"] = AdapterSettingKind.ChannelWidth24,
        ["ChannelWidth52"] = AdapterSettingKind.ChannelWidth5,
        ["ChannelWidth5"] = AdapterSettingKind.ChannelWidth5,
        ["*PacketCoalescing"] = AdapterSettingKind.LeaveDefault,
        ["*PMARPOffload"] = AdapterSettingKind.LeaveDefault,
        ["*PMNSOffload"] = AdapterSettingKind.LeaveDefault,
        ["*InterruptModeration"] = AdapterSettingKind.LeaveDefault,
        ["*FlowControl"] = AdapterSettingKind.LeaveDefault,
        ["*RSS"] = AdapterSettingKind.LeaveDefault,
        ["*EEE"] = AdapterSettingKind.EnergyEfficientEthernet,
        ["EEE"] = AdapterSettingKind.EnergyEfficientEthernet,
        ["AdvancedEEE"] = AdapterSettingKind.EnergyEfficientEthernet,
        ["EnableGreenEthernet"] = AdapterSettingKind.EthernetPowerSaving,
        ["GreenEthernet"] = AdapterSettingKind.EthernetPowerSaving,
        ["PowerSavingMode"] = AdapterSettingKind.EthernetPowerSaving,
        ["GigaLite"] = AdapterSettingKind.EthernetPowerSaving,
        ["*SpeedDuplex"] = AdapterSettingKind.SpeedDuplex,
    };

    public static AdapterSettingKind Identify(string? keyword, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(keyword) && Keywords.TryGetValue(keyword.Trim(), out var kind))
        {
            return kind;
        }

        string name = displayName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return AdapterSettingKind.Unknown;
        }

        if (Has(name, "Preferred Band") || Has(name, "Band Preference")) return AdapterSettingKind.PreferredBand;
        if (Has(name, "Roaming Aggressiveness") || Has(name, "Roaming Sensitivity")) return AdapterSettingKind.RoamingAggressiveness;
        if (Has(name, "MIMO Power Save")) return AdapterSettingKind.MimoPowerSave;
        if (Has(name, "Transmit Power")) return AdapterSettingKind.TransmitPower;
        if (WirelessModeNameRegex().IsMatch(name)) return AdapterSettingKind.WirelessMode;
        if (Has(name, "Channel Width") && name.Contains("2.4")) return AdapterSettingKind.ChannelWidth24;
        if (Has(name, "Channel Width") && name.Contains('5')) return AdapterSettingKind.ChannelWidth5;
        if (Has(name, "Packet Coalescing") || Has(name, "ARP Offload") || Has(name, "NS Offload") || Has(name, "Interrupt Moderation") || Has(name, "Flow Control"))
            return AdapterSettingKind.LeaveDefault;
        if (Has(name, "Energy Efficient Ethernet") || Has(name, "Energy-Efficient Ethernet") || Has(name, "Advanced EEE")) return AdapterSettingKind.EnergyEfficientEthernet;
        if (Has(name, "Green Ethernet") || Has(name, "Power Saving Mode") || Has(name, "Gigabit Lite")) return AdapterSettingKind.EthernetPowerSaving;
        if (Has(name, "Speed & Duplex") || Has(name, "Speed and Duplex") || Has(name, "Link Speed")) return AdapterSettingKind.SpeedDuplex;
        return AdapterSettingKind.Unknown;
    }

    public static bool IsKnown(string? keyword, string? displayName) => Identify(keyword, displayName) != AdapterSettingKind.Unknown;

    /// <summary>Builds the advised setting for every property, in the order given.</summary>
    public static IReadOnlyList<AdapterSetting> Advise(IEnumerable<AdapterAdvancedProperty> properties, AdvisorContext context)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.Select(p => Advise(p, context)).ToList();
    }

    public static AdapterSetting Advise(AdapterAdvancedProperty property, AdvisorContext context)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(context);
        var allowed = AllowedValues(property);
        var kind = Identify(property.RegistryKeyword, property.DisplayName);
        var (recommended, explanation, risk) = Recommend(kind, allowed, context);
        return new AdapterSetting(property.AdapterName, property.RegistryKeyword, property.DisplayName, property.RegistryValue, allowed, recommended, explanation, risk);
    }

    /// <summary>Pairs registry values with their display labels; a missing or mismatched label list falls back to the values.</summary>
    public static IReadOnlyList<(string Value, string Label)> AllowedValues(AdapterAdvancedProperty property)
    {
        var values = property.ValidRegistryValues;
        var labels = property.ValidDisplayValues;
        var list = new List<(string, string)>();
        for (int i = 0; i < values.Count; i++)
        {
            string label = labels.Count == values.Count && !string.IsNullOrWhiteSpace(labels[i]) ? labels[i] : values[i];
            list.Add((values[i], label));
        }

        return list;
    }

    /// <summary>True when <paramref name="value"/> is one of the setting's allowed registry values.</summary>
    public static bool IsAllowed(AdapterSetting setting, string? value) =>
        value is not null && setting.AllowedValues.Any(a => string.Equals(a.Value, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds the advisor context from the Wi-Fi analysis.</summary>
    public static AdvisorContext ContextFrom(WifiReport? report, bool isWifi, bool isLaptop)
    {
        var current = report?.Current;
        var nearby = report?.Nearby ?? [];
        string? ssid = current?.Ssid;
        var same = ssid is null ? [] : nearby.Where(n => n.Ssid.Equals(ssid, StringComparison.Ordinal)).ToList();
        bool congested = current is { Band: WifiBands.Band24, Channel: { } ch }
            && ChannelCongestionCalculator.Overlapping(nearby, WifiBands.Band24, ch, current.Bssid).Count() > WifiRules.CrowdedNetworkCount;
        if (current?.Band != WifiBands.Band24)
        {
            // Not on 2.4 GHz now: judge the band by how crowded it is in general.
            congested = nearby.Count(n => n.Band == WifiBands.Band24) > 2 * WifiRules.CrowdedNetworkCount;
        }

        // Radios of one router usually share all but the last byte of their MAC; count distinct access points by the first five bytes.
        int accessPoints = same.Select(n => n.Bssid.Length >= 14 ? n.Bssid[..14] : n.Bssid).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new AdvisorContext(
            isWifi,
            isLaptop,
            same.Any(n => n.Band == WifiBands.Band5 && n.SignalPercent >= WifiRules.UsableFastBandSignal),
            same.Any(n => n.Band == WifiBands.Band6 && n.SignalPercent >= WifiRules.UsableFastBandSignal),
            congested,
            Math.Max(accessPoints, current?.IsConnected == true ? 1 : 0));
    }

    private static (string? Value, string Explanation, RiskLevel Risk) Recommend(AdapterSettingKind kind, IReadOnlyList<(string Value, string Label)> allowed, AdvisorContext ctx)
    {
        switch (kind)
        {
            case AdapterSettingKind.PreferredBand:
            {
                string text = "Which band the adapter picks when your router offers several under one name. ";
                if (ctx.SameSsidOn6GhzVisible && Pick(allowed, l => BandLabel(l, "6")) is { } six)
                {
                    return (six, text + "Your network is visible on 6 GHz, the fastest and least crowded band, so prefer it.", RiskLevel.Safe);
                }

                if ((ctx.SameSsidOn5GhzVisible || ctx.SameSsidOn6GhzVisible) && Pick(allowed, l => BandLabel(l, "5")) is { } five)
                {
                    return (five, text + "Your network is also visible on 5 GHz, which is usually several times faster than 2.4 GHz, so prefer it.", RiskLevel.Safe);
                }

                return (null, text + "No 5 or 6 GHz version of your network is visible right now, so PC Checker leaves this alone.", RiskLevel.Safe);
            }

            case AdapterSettingKind.RoamingAggressiveness:
            {
                string text = "How eagerly the adapter jumps to a stronger access point. ";
                bool desktopSingleAp = !ctx.IsLaptop && ctx.AccessPointsForSsid <= 1;
                if (desktopSingleAp)
                {
                    var lowest = Pick(allowed, l => Has(l, "lowest")) ?? (allowed.Any(a => a.Value == "1") ? "1" : null);
                    return (lowest, text + "This PC stays in one place and sees one access point for your network, so searching for another only causes short lag spikes. Lowest is best.", RiskLevel.Safe);
                }

                var medium = Pick(allowed, l => Has(l, "medium") && !Has(l, "low") && !Has(l, "high")) ?? (allowed.Any(a => a.Value == "3") ? "3" : null);
                return (medium, text + (ctx.IsLaptop
                    ? "On a laptop that moves between rooms, the balanced Medium setting (the default) switches when it should."
                    : "Several access points (mesh) broadcast your network, so the balanced Medium setting (the default) lets the PC move to the best one."), RiskLevel.Safe);
            }

            case AdapterSettingKind.MimoPowerSave:
                return (Pick(allowed, l => Has(l, "no smps") || Has(l, "no mimo")),
                    "MIMO power save switches off some of the adapter's antennas to save battery. No SMPS keeps them all active for full speed.", RiskLevel.Safe);

            case AdapterSettingKind.TransmitPower:
                return (Pick(allowed, l => Has(l, "highest") || Has(l, "100")) ?? (allowed.Count > 0 ? allowed[^1].Value : null),
                    "How loud the adapter transmits. Highest gives the router the clearest signal from this PC.", RiskLevel.Safe);

            case AdapterSettingKind.WirelessMode:
            {
                var best = allowed.Select(a => (a.Value, Gen: ModeGeneration(a.Label))).Where(a => a.Gen > 0).OrderByDescending(a => a.Gen).FirstOrDefault();
                return (best.Gen > 0 ? best.Value : null,
                    "The newest Wi-Fi standard the adapter may use. The highest one supported lets the adapter connect at full speed when the router supports it too.", RiskLevel.Moderate);
            }

            case AdapterSettingKind.ChannelWidth24:
                return ctx.Band24Congested
                    ? (Pick(allowed, l => Has(l, "20") && !Has(l, "40")), "Channel width on 2.4 GHz. The band is crowded here, so the narrow 20 MHz width avoids overlapping neighbours and gives a steadier connection.", RiskLevel.Moderate)
                    : (Pick(allowed, l => Has(l, "auto")), "Channel width on 2.4 GHz. Auto lets the adapter use 40 MHz when the air is clear.", RiskLevel.Moderate);

            case AdapterSettingKind.ChannelWidth5:
                return (Pick(allowed, l => Has(l, "auto")), "Channel width on 5 GHz. Auto lets the adapter use the widest channel (80 or 160 MHz) the router offers, which is where Wi-Fi 5/6 speed comes from.", RiskLevel.Moderate);

            case AdapterSettingKind.LeaveDefault:
                return (null, "A driver feature that is best left at the manufacturer's default. PC Checker does not change it.", RiskLevel.Safe);

            case AdapterSettingKind.EnergyEfficientEthernet:
            case AdapterSettingKind.EthernetPowerSaving:
                return ctx.IsLaptop
                    ? (null, "Ethernet power saving lowers power use when the cable is idle. On a laptop it helps battery life, so PC Checker leaves it on.", RiskLevel.Safe)
                    : (Disabled(allowed), "Ethernet power saving (EEE / Green Ethernet) puts the link to sleep between packets. On a desktop it saves almost nothing and can cause short lag spikes or link drops with some switches, so turn it off.", RiskLevel.Safe);

            case AdapterSettingKind.SpeedDuplex:
                return (Pick(allowed, l => Has(l, "auto")) ?? (allowed.Any(a => a.Value == "0") ? "0" : null),
                    "Link speed and duplex. Auto Negotiation lets the adapter and router agree on the fastest speed; a fixed value often causes slow or dropped links.", RiskLevel.Moderate);

            default:
                return (null, "A driver-specific setting PC Checker has no recommendation for. Change it only if the adapter maker tells you to.", RiskLevel.Advanced);
        }
    }

    /// <summary>The registry value whose label matches, or null.</summary>
    private static string? Pick(IReadOnlyList<(string Value, string Label)> allowed, Func<string, bool> predicate) =>
        allowed.FirstOrDefault(a => predicate(a.Label)).Value;

    private static string? Disabled(IReadOnlyList<(string Value, string Label)> allowed) =>
        Pick(allowed, l => Has(l, "disab") || Has(l, "off") || Has(l, "aus") || Has(l, "désactiv"))
        ?? (allowed.Any(a => a.Value == "0") ? "0" : null);

    private static bool BandLabel(string label, string band) =>
        Regex.IsMatch(label, $@"(?<![\d.]){band}\s?G(Hz)?", RegexOptions.IgnoreCase) && !Has(label, "no pref");

    /// <summary>Rank of a wireless-mode label ("802.11ax" → 6).</summary>
    public static int ModeGeneration(string label)
    {
        var l = label.ToLowerInvariant();
        if (l.Contains("disab")) return 0;
        if (l.Contains("11be") || l.EndsWith(" be", StringComparison.Ordinal)) return 7;
        if (l.Contains("11ax") || l.EndsWith(" ax", StringComparison.Ordinal)) return 6;
        if (l.Contains("11ac") || l.EndsWith(" ac", StringComparison.Ordinal)) return 5;
        if (l.Contains("11n") || l.EndsWith(" n", StringComparison.Ordinal)) return 4;
        return 0;
    }

    private static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"802\.11n.*(?:ac|ax|mode)", RegexOptions.IgnoreCase)]
    private static partial Regex WirelessModeNameRegex();
}
