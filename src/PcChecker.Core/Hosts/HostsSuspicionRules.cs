using System.Net;

namespace PcChecker.Core.Hosts;

/// <summary>
/// Flags hosts entries that malware typically adds: sending well-known or bank-like sites to another server (to show
/// fake pages), and blocking Windows Update, Microsoft Defender or security vendors' update sites (to stop protection
/// from updating). Ordinary blocking (ads, trackers, telemetry) and local names are not flagged. Turned-off lines are
/// never flagged because Windows ignores them.
/// </summary>
public static class HostsSuspicionRules
{
    public const string Label = "Possibly unwanted (malware does this)";

    /// <summary>Sites people sign in to; sending them elsewhere is how fake login pages work.</summary>
    private static readonly string[] WellKnownSites =
    [
        "microsoft.com", "windows.com", "windowsupdate.com", "live.com", "office.com", "office365.com", "outlook.com",
        "microsoftonline.com", "bing.com", "msn.com", "google.com", "gmail.com", "googleapis.com", "youtube.com",
        "facebook.com", "instagram.com", "whatsapp.com", "twitter.com", "x.com", "apple.com", "icloud.com",
        "amazon.com", "paypal.com", "ebay.com", "github.com", "dropbox.com", "yahoo.com", "linkedin.com",
        "steampowered.com", "steamcommunity.com", "epicgames.com", "discord.com", "netflix.com", "spotify.com",
    ];

    /// <summary>Words in bank and payment site names.</summary>
    private static readonly string[] BankWords =
    [
        "bank", "paypal", "chase.com", "wellsfargo", "citi.com", "citibank", "barclays", "hsbc", "santander", "natwest",
        "lloyds", "capitalone", "americanexpress", "amex.", "visa.com", "mastercard", "creditunion", "sparkasse",
        "revolut", "monzo", "stripe.com", "venmo", "coinbase", "binance",
    ];

    /// <summary>Update and security sites, with the product named in the reason.</summary>
    private static readonly (string Domain, string Product)[] SecurityHosts =
    [
        ("windowsupdate.com", "Windows Update"), ("windowsupdate.microsoft.com", "Windows Update"),
        ("update.microsoft.com", "Windows Update"), ("delivery.mp.microsoft.com", "Windows Update"),
        ("definitionupdates.microsoft.com", "Microsoft Defender"), ("wdcp.microsoft.com", "Microsoft Defender"),
        ("wdcpalt.microsoft.com", "Microsoft Defender"), ("smartscreen.microsoft.com", "SmartScreen"),
        ("smartscreen-prod.microsoft.com", "SmartScreen"), ("avast.com", "Avast"), ("avcdn.net", "Avast"),
        ("avg.com", "AVG"), ("kaspersky.com", "Kaspersky"), ("kaspersky-labs.com", "Kaspersky"), ("eset.com", "ESET"),
        ("eset.sk", "ESET"), ("mcafee.com", "McAfee"), ("norton.com", "Norton"), ("symantec.com", "Symantec"),
        ("symantecliveupdate.com", "Symantec"), ("bitdefender.com", "Bitdefender"), ("bitdefender.net", "Bitdefender"),
        ("malwarebytes.com", "Malwarebytes"), ("mbamupdates.com", "Malwarebytes"), ("sophos.com", "Sophos"),
        ("trendmicro.com", "Trend Micro"), ("f-secure.com", "F-Secure"), ("webroot.com", "Webroot"),
        ("avira.com", "Avira"), ("comodo.com", "Comodo"), ("drweb.com", "Dr.Web"), ("emsisoft.com", "Emsisoft"),
        ("pandasecurity.com", "Panda"), ("gdata.de", "G DATA"), ("virustotal.com", "VirusTotal"),
    ];

    /// <summary>The flag for one line, or null when it looks fine.</summary>
    public static HostsFlag? Evaluate(HostsLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Kind != HostsLineKind.Entry || !line.Enabled)
        {
            return null;
        }

        bool blocks = IsBlockAddress(line.Address);
        foreach (var host in line.HostNames)
        {
            if (SecurityProduct(host) is { } product)
            {
                return blocks
                    ? new HostsFlag(HostsConcern.BlocksSecurityUpdates, $"Blocks {product} ({host}). Malware does this to stop your protection from updating.")
                    : new HostsFlag(HostsConcern.RedirectsKnownSite, $"Sends {host} ({product}) to {line.Address} instead of the real server. Malware does this to fake updates.");
            }

            if (!blocks && (IsWellKnown(host) || IsBankLike(host)))
            {
                return new HostsFlag(HostsConcern.RedirectsKnownSite, $"Sends {host} to {line.Address} instead of the real site. Malware does this to show fake pages.");
            }
        }

        return null;
    }

    /// <summary>0.0.0.0, the 127.x loopback range, :: and ::1: addresses used to block a name rather than redirect it.</summary>
    public static bool IsBlockAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address.Trim(), out var ip))
        {
            return false;
        }

        return IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None);
    }

    /// <summary>The product whose update or protection site <paramref name="host"/> is, or null.</summary>
    public static string? SecurityProduct(string host) =>
        SecurityHosts.FirstOrDefault(s => Matches(host, s.Domain)).Product;

    public static bool IsWellKnown(string host) => WellKnownSites.Any(d => Matches(host, d));

    public static bool IsBankLike(string host) =>
        !string.IsNullOrWhiteSpace(host) && BankWords.Any(w => host.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when <paramref name="host"/> is <paramref name="domain"/> or a name under it (not "notmicrosoft.com").</summary>
    public static bool Matches(string host, string domain)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string h = host.Trim().TrimEnd('.');
        return h.Equals(domain, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }
}
