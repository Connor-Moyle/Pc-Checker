using System.Net;
using System.Net.Sockets;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Network.Tuning;

/// <summary>Ids of the network tuning actions.</summary>
public static class TuningActionIds
{
    public const string WifiPowerSave = "wifi.powersave.max-performance";
    public const string WifiAllowTurnOff = "wifi.adapter.allow-turn-off.disable";
    public const string TcpAutoTuning = "net.tcp.autotuning.normal";
    public const string TcpEcnDefault = "net.tcp.ecn.default";
    public const string Rss = "net.rss.enable";
    public const string DnsFast = "net.dns.fast";
    public const string DnsOverHttps = "net.dns.doh";
    public const string PreferIpv4 = "net.ipv6.prefer-ipv4";

    /// <summary>Lives on the Latency page (Optimize catalog); the Network page links to it instead of duplicating it.</summary>
    public const string NagleOffLatency = "latency.nagle.off";
}

/// <summary>The tuning actions, the DNS providers and which actions "Quick improve" ticks by default. Pure.</summary>
public static class NetworkTuningCatalog
{
    public static IReadOnlyList<TuningAction> All { get; } =
    [
        new(TuningActionIds.WifiPowerSave, "Wi-Fi power saving: Maximum Performance",
            "Sets the power plan's \"Wireless Adapter Settings → Power Saving Mode\" to Maximum Performance on mains and battery, so the Wi-Fi radio does not doze between packets. Removes small lag spikes; uses slightly more battery on laptops.",
            RiskLevel.Safe, AdvancedOnly: false, RequiresReconnect: false, RequiresRestart: false),
        new(TuningActionIds.WifiAllowTurnOff, "Stop Windows turning off the Wi-Fi adapter",
            "Clears \"Allow the computer to turn off this device to save power\" for Wi-Fi adapters (PnPCapabilities = 24). Fixes Wi-Fi that drops or needs a reconnect after sleep. Takes effect after a restart. (The Latency page has a similar fix for every adapter.)",
            RiskLevel.Safe, AdvancedOnly: false, RequiresReconnect: true, RequiresRestart: true),
        new(TuningActionIds.TcpAutoTuning, "TCP receive window auto-tuning: Normal",
            "Lets Windows grow the TCP receive window on fast connections (the Windows default). \"Optimizer\" tools often set it to disabled or restricted, which caps downloads at a fraction of your plan's speed.",
            RiskLevel.Safe, AdvancedOnly: false, RequiresReconnect: false, RequiresRestart: false),
        new(TuningActionIds.TcpEcnDefault, "ECN back to the Windows default",
            "Resets Explicit Congestion Notification (a way for routers to signal \"slow down\" without dropping packets) to whatever Windows uses by default, undoing tweaks made by other tools.",
            RiskLevel.Safe, AdvancedOnly: false, RequiresReconnect: false, RequiresRestart: false),
        new(TuningActionIds.Rss, "Receive Side Scaling: on",
            "Spreads incoming network work across several CPU cores instead of one (the Windows default). Turning it off is an old tweak that hurts fast connections.",
            RiskLevel.Safe, AdvancedOnly: false, RequiresReconnect: false, RequiresRestart: false),
        new(TuningActionIds.DnsFast, "Use a fast public DNS server",
            "Points the active adapters at Cloudflare, Google or Quad9 DNS instead of your provider's. Websites start loading sooner when your provider's DNS is slow. Revert restores exactly what was there (automatic or your own servers).",
            RiskLevel.Moderate, AdvancedOnly: false, RequiresReconnect: true, RequiresRestart: false),
        new(TuningActionIds.DnsOverHttps, "Encrypt DNS (DNS over HTTPS)",
            "Windows 11 only. Tells Windows to send DNS lookups to your public DNS provider over encrypted HTTPS, so others on the network cannot read or change them. Falling back to normal DNS stays allowed so nothing breaks. Needs a public DNS provider set first.",
            RiskLevel.Advanced, AdvancedOnly: true, RequiresReconnect: false, RequiresRestart: false),
        new(TuningActionIds.PreferIpv4, "Prefer IPv4 over IPv6",
            "Sets the IPv6 DisabledComponents flag 0x20 so Windows tries IPv4 first. IPv6 stays on (this does not disable it). Helps when a provider's IPv6 route is slow or broken. Needs a restart.",
            RiskLevel.Advanced, AdvancedOnly: true, RequiresReconnect: false, RequiresRestart: true),
    ];

    /// <summary>Actions "Quick improve" ticks when they are not applied yet.</summary>
    public static IReadOnlyList<string> QuickImproveDefaults { get; } =
        [TuningActionIds.WifiPowerSave, TuningActionIds.WifiAllowTurnOff, TuningActionIds.TcpAutoTuning, TuningActionIds.Rss];

    /// <summary>Actions "Quick improve" offers unticked (a matter of preference).</summary>
    public static IReadOnlyList<string> QuickImproveOptional { get; } = [TuningActionIds.DnsFast];

    public static IReadOnlyList<DnsProvider> DnsProviders { get; } =
    [
        new("cloudflare", "Cloudflare", "1.1.1.1", "1.0.0.1", "https://cloudflare-dns.com/dns-query", "Usually the fastest; does not sell or log your lookups long-term.",
            "2606:4700:4700::1111", "2606:4700:4700::1001"),
        new("google", "Google Public DNS", "8.8.8.8", "8.8.4.4", "https://dns.google/dns-query", "Fast and reliable everywhere.",
            "2001:4860:4860::8888", "2001:4860:4860::8844"),
        new("quad9", "Quad9", "9.9.9.9", "149.112.112.112", "https://dns.quad9.net/dns-query", "Blocks known malware and phishing sites; run by a non-profit.",
            "2620:fe::fe", "2620:fe::9"),
    ];

    public static DnsProvider DefaultDnsProvider => DnsProviders[0];

    /// <summary>Finds an action by id. "net.dns.fast.google" style ids resolve to the DNS action.</summary>
    public static TuningAction? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var exact = All.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        return DnsProviderFromActionId(id) is not null ? All.First(a => a.Id == TuningActionIds.DnsFast) : null;
    }

    public static bool Handles(string? id) => Find(id) is not null;

    /// <summary>"net.dns.fast.quad9" → the provider id used with <see cref="TuningActionIds.DnsFast"/>.</summary>
    public static string DnsActionId(string providerId) => $"{TuningActionIds.DnsFast}.{providerId}";

    /// <summary>The provider chosen by a DNS action id; the plain id means the default provider. Null for other ids.</summary>
    public static DnsProvider? DnsProviderFromActionId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (id.Equals(TuningActionIds.DnsFast, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultDnsProvider;
        }

        string prefix = TuningActionIds.DnsFast + ".";
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string provider = id[prefix.Length..];
        return DnsProviders.FirstOrDefault(p => p.Id.Equals(provider, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The provider whose servers are exactly (or start with) the given addresses, ignoring order; null when none matches.</summary>
    public static DnsProvider? MatchDnsProvider(IEnumerable<string> servers)
    {
        var list = servers.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (list.Count == 0)
        {
            return null;
        }

        return DnsProviders.FirstOrDefault(p => list.All(s => p.Servers.Contains(s)));
    }

    /// <summary>
    /// The provider of an adapter that uses <paramref name="ipv4Servers"/> and <paramref name="ipv6Servers"/>: the IPv4
    /// servers must match it, and every IPv6 server (other than Windows' fec0:0:0:ffff:: placeholders) must be the same
    /// provider's, because Windows also sends lookups to the IPv6 servers a router hands out. Null when none matches.
    /// </summary>
    public static DnsProvider? MatchDnsProvider(IEnumerable<string> ipv4Servers, IEnumerable<string> ipv6Servers)
    {
        var provider = MatchDnsProvider(ipv4Servers);
        if (provider is null)
        {
            return null;
        }

        var own = provider.Servers6.Select(NormalizeAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ipv6Servers.Where(s => !string.IsNullOrWhiteSpace(s) && !IsPlaceholderIpv6Dns(s)).All(s => own.Contains(NormalizeAddress(s))) ? provider : null;
    }

    /// <summary>True for the site-local fec0:0:0:ffff::1/2/3 addresses Windows lists when no IPv6 DNS server is configured.</summary>
    public static bool IsPlaceholderIpv6Dns(string? server) =>
        IPAddress.TryParse(server?.Trim(), out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6SiteLocal;

    /// <summary>The DNS-over-HTTPS template for a server address (IPv4 or IPv6), when it belongs to a known provider.</summary>
    public static string? DohTemplateFor(string server)
    {
        string normalized = NormalizeAddress(server);
        return DnsProviders.FirstOrDefault(p => p.Servers.Concat(p.Servers6).Any(s => NormalizeAddress(s).Equals(normalized, StringComparison.OrdinalIgnoreCase)))?.DohTemplate;
    }

    /// <summary>The canonical text of an IP address ("2620:FE::FE" → "2620:fe::fe"); other text is only trimmed.</summary>
    private static string NormalizeAddress(string server)
    {
        string text = server?.Trim() ?? string.Empty;
        return IPAddress.TryParse(text, out var address) ? address.ToString() : text;
    }

    /// <summary>Converts a Get-NetTCPSetting AutoTuningLevelLocal value to the netsh keyword (null when unknown).</summary>
    public static string? AutoTuningToNetsh(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "normal" => "normal",
        "disabled" => "disabled",
        "highlyrestricted" => "highlyrestricted",
        "restricted" => "restricted",
        "experimental" => "experimental",
        _ => null,
    };

    /// <summary>"Enabled"/"Disabled" (PowerShell) → "enabled"/"disabled" (netsh); null when unknown.</summary>
    public static string? EnabledToNetsh(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "enabled" or "true" or "1" => "enabled",
        "disabled" or "false" or "0" => "disabled",
        _ => null,
    };

    /// <summary>True when the IPv6 DisabledComponents value prefers IPv4 (bit 0x20).</summary>
    public static bool PrefersIpv4(int? disabledComponents) => disabledComponents is { } v && (v & 0x20) != 0;

    /// <summary>The DisabledComponents value with the prefer-IPv4 bit set and every other bit kept.</summary>
    public static int WithPreferIpv4(int? disabledComponents) => (disabledComponents ?? 0) | 0x20;

    /// <summary>The Windows power plan GUIDs for Wireless Adapter Settings → Power Saving Mode.</summary>
    public const string WirelessSubgroup = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
    public const string WirelessPowerSavingSetting = "12bbebe6-58d6-4636-95bb-3217ef867c1a";

    /// <summary>"Maximum Performance", "Low Power Saving", … for a Wireless Adapter Settings power index.</summary>
    public static string DescribeWifiPowerIndex(int? index) => index switch
    {
        0 => "Maximum Performance",
        1 => "Low Power Saving",
        2 => "Medium Power Saving",
        3 => "Maximum Power Saving",
        null => "Unknown",
        _ => $"Value {index}",
    };

    /// <summary>PnPCapabilities value that clears "Allow the computer to turn off this device to save power".</summary>
    public const int PnpCapabilitiesNoPowerOff = 24;
}
