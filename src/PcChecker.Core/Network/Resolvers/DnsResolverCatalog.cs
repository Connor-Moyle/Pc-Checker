using System.Net;
using System.Net.Sockets;
using PcChecker.Core.Network.Tuning;

namespace PcChecker.Core.Network.Resolvers;

/// <summary>What a DNS service blocks on purpose (by answering "does not exist" for those sites).</summary>
[Flags]
public enum DnsFiltering
{
    None = 0,
    Malware = 1,
    Phishing = 2,
    Ads = 4,
    Adult = 8,
}

/// <summary>One server address to test, with where it came from.</summary>
public sealed record DnsBenchmarkTarget(
    string Address,
    string? ProviderId,
    string? ProviderName,
    DnsFiltering Filtering,
    bool IsCurrent,
    bool IsRouter,
    bool IsCustom)
{
    public bool IsIpv6 => IPAddress.TryParse(Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
}

/// <summary>
/// The public DNS services PC Checker tests and can switch to, built on the Network page's providers so both pages agree
/// on addresses and DNS-over-HTTPS templates, plus which test list to run. Pure.
/// </summary>
public static class DnsResolverCatalog
{
    public const string OpenDnsId = "opendns";
    public const string AdGuardId = "adguard";
    public const string CloudflareFamilyId = "cloudflare-family";
    public const string ControlDId = "controld";

    /// <summary>Every provider: Cloudflare, Google and Quad9 from the Network page, then the ones only this page offers.</summary>
    public static IReadOnlyList<DnsProvider> Providers { get; } =
    [
        .. NetworkTuningCatalog.DnsProviders,
        new(OpenDnsId, "OpenDNS", "208.67.222.222", "208.67.220.220", "https://doh.opendns.com/dns-query",
            "Cisco's service; blocks known phishing sites.", "2620:119:35::35", "2620:119:53::53"),
        new(AdGuardId, "AdGuard DNS", "94.140.14.14", "94.140.15.15", "https://dns.adguard-dns.com/dns-query",
            "Blocks ads and trackers on every device and browser.", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"),
        new(CloudflareFamilyId, "Cloudflare for Families", "1.1.1.3", "1.0.0.3", "https://family.cloudflare-dns.com/dns-query",
            "Blocks malware and adult sites.", "2606:4700:4700::1113", "2606:4700:4700::1003"),
        new(ControlDId, "Control D", "76.76.2.0", "76.76.10.0", "https://freedns.controld.com/p0",
            "Free, unfiltered service.", "2606:1a40::", "2606:1a40:1::"),
    ];

    /// <summary>The public addresses the benchmark tests: both servers of the big three, the main one of the rest.</summary>
    public static IReadOnlyList<string> BenchmarkAddresses { get; } =
    [
        "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112",
        "208.67.222.222", "94.140.14.14", "1.1.1.3", "76.76.2.0",
    ];

    /// <summary>What each provider blocks on purpose.</summary>
    public static DnsFiltering FilteringOf(string? providerId) => providerId switch
    {
        "quad9" => DnsFiltering.Malware | DnsFiltering.Phishing,
        OpenDnsId => DnsFiltering.Phishing,
        AdGuardId => DnsFiltering.Ads,
        CloudflareFamilyId => DnsFiltering.Malware | DnsFiltering.Adult,
        _ => DnsFiltering.None,
    };

    public static DnsProvider? Find(string? providerId) =>
        providerId is null ? null : Providers.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The provider that owns <paramref name="address"/> (IPv4 or IPv6), or null.</summary>
    public static DnsProvider? ProviderOf(string? address)
    {
        string normalized = Normalize(address);
        return normalized.Length == 0
            ? null
            : Providers.FirstOrDefault(p => p.Servers.Concat(p.Servers6).Any(s => Normalize(s).Equals(normalized, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The DNS-over-HTTPS template for a server address of a known provider, or null.</summary>
    public static string? DohTemplateFor(string? address) => ProviderOf(address)?.DohTemplate;

    /// <summary>
    /// The servers to test, in this order and without duplicates: the ones the PC uses now, the ones the router hands
    /// out, the public list and a custom one. An address in several groups keeps all its flags. Windows' fec0::
    /// placeholders and anything that is not an IP address are left out.
    /// </summary>
    public static IReadOnlyList<DnsBenchmarkTarget> BuildTargets(IEnumerable<string> current, IEnumerable<string> router, string? custom)
    {
        var list = new List<DnsBenchmarkTarget>();
        void Add(string? address, bool isCurrent, bool isRouter, bool isCustom)
        {
            string normalized = Normalize(address);
            if (normalized.Length == 0 || NetworkTuningCatalog.IsPlaceholderIpv6Dns(normalized))
            {
                return;
            }

            int existing = list.FindIndex(t => t.Address.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var t = list[existing];
                list[existing] = t with { IsCurrent = t.IsCurrent || isCurrent, IsRouter = t.IsRouter || isRouter, IsCustom = t.IsCustom || isCustom };
                return;
            }

            var provider = ProviderOf(normalized);
            list.Add(new DnsBenchmarkTarget(normalized, provider?.Id, provider?.Name, FilteringOf(provider?.Id), isCurrent, isRouter, isCustom));
        }

        foreach (var address in current ?? [])
        {
            Add(address, true, false, false);
        }

        foreach (var address in router ?? [])
        {
            Add(address, false, true, false);
        }

        foreach (var address in BenchmarkAddresses)
        {
            Add(address, false, false, false);
        }

        Add(custom, false, false, true);
        return list;
    }

    /// <summary>True when <paramref name="text"/> is an IPv4 or IPv6 address (with no port or scope).</summary>
    public static bool IsServerAddress(string? text) =>
        IPAddress.TryParse(text?.Trim(), out var ip)
        && ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
        && !text!.Contains('%')
        && (ip.AddressFamily == AddressFamily.InterNetworkV6 || text.Count(c => c == '.') == 3);

    /// <summary>The canonical text of an address ("2620:FE::FE" → "2620:fe::fe"); empty for anything that is not one.</summary>
    public static string Normalize(string? address)
    {
        string text = address?.Trim() ?? string.Empty;
        return IPAddress.TryParse(text, out var ip) && (ip.AddressFamily == AddressFamily.InterNetworkV6 || text.Count(c => c == '.') == 3)
            ? ip.ToString()
            : string.Empty;
    }
}
