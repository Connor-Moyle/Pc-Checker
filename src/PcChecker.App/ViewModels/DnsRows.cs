using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Network.Resolvers;
using PcChecker.Core.Network.Tuning;

namespace PcChecker.App.ViewModels;

/// <summary>One connected adapter on the DNS page, with a tick box for "change this one".</summary>
public sealed partial class DnsAdapterRow : ObservableObject
{
    public DnsAdapterRow(DnsAdapterInfo info)
    {
        Info = info;
        _isSelected = true;
    }

    public DnsAdapterInfo Info { get; }

    [ObservableProperty] private bool _isSelected;

    public string Name => Info.Name;

    public string Description => Info.Description;

    public string ServersText => Info.Servers4.Count == 0 ? Loc.T("None") : string.Join(", ", Info.Servers4);

    public string Ipv6Text => Info.Servers6.Count == 0 ? Loc.T("None") : string.Join(", ", Info.Servers6);

    public bool HasIpv6 => Info.Servers6.Count > 0;

    public string ModeText => Info.IsAutomatic4
        ? Loc.T("Automatic (from your router)")
        : DnsText.ProviderName(Info.Servers4) is { } provider ? Loc.F("Set by hand: {0}", provider) : Loc.T("Set by hand");

    public string RouterText => Info.RouterServers.Count == 0
        ? Loc.T("Your router does not hand out DNS servers")
        : Loc.F("Your router hands out {0}", string.Join(", ", Info.RouterServers));

    public string EncryptionText => DnsText.Encryption(Info.Encryption);
}

/// <summary>One server in the benchmark table.</summary>
public sealed class DnsResultRow
{
    public DnsResultRow(DnsServerResult result, double? score, double? bestScore)
    {
        Result = result;
        Score = score;
        var target = result.Target;
        Name = target.ProviderName ?? (target.IsRouter ? Loc.T("Your router") : target.IsCustom ? Loc.T("Your server") : Loc.T("Your DNS"));
        var tags = new List<string>();
        if (target.IsCurrent)
        {
            tags.Add(Loc.T("in use now"));
        }

        if (target.IsRouter)
        {
            tags.Add(Loc.T("router"));
        }

        if (target.IsCustom)
        {
            tags.Add(Loc.T("added by you"));
        }

        Tags = string.Join(", ", tags);
        // The bar is full for the best score and shorter for slower servers (nothing for servers that gave no answer).
        SpeedPercent = score is { } s && bestScore is { } b && s > 0 ? Math.Clamp(100 * b / s, 4, 100) : 0;
    }

    public DnsServerResult Result { get; }

    public double? Score { get; }

    public string Name { get; }

    public string Address => Result.Target.Address;

    public string Tags { get; }

    public bool HasTags => Tags.Length > 0;

    public double SpeedPercent { get; }

    public string CachedText => DnsText.Ms(Result.Cached.MedianMs);

    public string UncachedText => DnsText.Ms(Result.Uncached.MedianMs);

    public string P90Text => DnsText.Ms(Result.Cached.P90Ms);

    public string ReliabilityText => Result.Unreachable
        ? Loc.T("No answer")
        : Result.Sent == 0 ? "—" : (Result.Reliability * 100).ToString("0", CultureInfo.CurrentCulture) + " %";

    public bool IsUnreliable => Result.Sent > 0 && Result.Reliability < DnsRecommendationRules.MinReliability;

    public string DnssecText => Result.ValidatesDnssec switch
    {
        true => Loc.T("Checks"),
        false => Loc.T("No"),
        null => "—",
    };

    public string BlocksText => DnsText.Blocks(Result.Target.Filtering);

    public string ToolTipText => Loc.F("{0} lookups sent, {1} answered, {2} timed out.", Result.Sent, Result.Answered, Result.Timeouts)
        + (Result.Partial ? " " + Loc.T("The test ran out of time before this server finished.") : string.Empty);

    /// <summary>A line for "Copy results".</summary>
    public string CopyText() => string.Join("\t", Name, Address, CachedText, UncachedText, P90Text, ReliabilityText, DnssecText, BlocksText);
}

/// <summary>A choice in the "Switch to" list: a public service or servers typed in by hand.</summary>
public sealed record DnsChoice(string Id, string Name, string Detail, DnsProvider? Provider)
{
    public const string CustomId = "custom";

    public bool IsCustom => Provider is null;

    public override string ToString() => Name;
}

/// <summary>Plain-words text for the DNS page.</summary>
public static class DnsText
{
    public static string Ms(double? value) =>
        value is { } v ? (v < 10 ? v.ToString("0.0", CultureInfo.CurrentCulture) : v.ToString("0", CultureInfo.CurrentCulture)) + " ms" : "—";

    public static string Encryption(DnsEncryptionState state) => state switch
    {
        DnsEncryptionState.On => Loc.T("Encrypted (DNS over HTTPS)"),
        DnsEncryptionState.Partly => Loc.T("Partly encrypted"),
        DnsEncryptionState.Off => Loc.T("Not encrypted"),
        DnsEncryptionState.NotSupported => Loc.T("Needs Windows 11"),
        _ => Loc.T("Unknown"),
    };

    public static string Blocks(DnsFiltering filtering)
    {
        if (filtering == DnsFiltering.None)
        {
            return Loc.T("Nothing");
        }

        var parts = new List<string>();
        if (filtering.HasFlag(DnsFiltering.Malware))
        {
            parts.Add(Loc.T("malware"));
        }

        if (filtering.HasFlag(DnsFiltering.Phishing))
        {
            parts.Add(Loc.T("phishing"));
        }

        if (filtering.HasFlag(DnsFiltering.Ads))
        {
            parts.Add(Loc.T("ads and trackers"));
        }

        if (filtering.HasFlag(DnsFiltering.Adult))
        {
            parts.Add(Loc.T("adult sites"));
        }

        string text = string.Join(", ", parts);
        return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }

    /// <summary>"Cloudflare" when every server belongs to one known service, otherwise null.</summary>
    public static string? ProviderName(IReadOnlyList<string> servers)
    {
        var providers = servers.Select(DnsResolverCatalog.ProviderOf).ToList();
        return providers.Count > 0 && providers.All(p => p is not null) && providers.Select(p => p!.Id).Distinct().Count() == 1 ? providers[0]!.Name : null;
    }

    /// <summary>"Wi-Fi: automatic (from your router)" or "Wi-Fi: 8.8.8.8, 8.8.4.4" for each adapter of a capture.</summary>
    public static string Previous(DnsPreviousSettings previous) =>
        string.Join("\n", previous.Adapters.Select(a =>
        {
            var view = DnsSettingsCapture.Describe(a);
            string v4 = view.Automatic4 ? Loc.T("automatic (from your router)") : string.Join(", ", view.Servers4);
            string v6 = view.Servers6.Count > 0 ? " · IPv6: " + string.Join(", ", view.Servers6) : string.Empty;
            return $"{view.Name}: {v4}{v6}";
        }));

    public static string Problem(DnsSwitchProblem problem) => problem switch
    {
        DnsSwitchProblem.NoAdapter => Loc.T("Tick at least one network adapter to change."),
        DnsSwitchProblem.NoServers => Loc.T("Type at least one DNS server address."),
        DnsSwitchProblem.InvalidAddress => Loc.T("One of the addresses is not a valid IP address, such as 1.1.1.1 or 2606:4700:4700::1111."),
        DnsSwitchProblem.WrongFamily => Loc.T("An IPv6 address was typed where an IPv4 address belongs, or the other way round."),
        DnsSwitchProblem.TooManyServers => Loc.T("Use at most three servers of each kind."),
        _ => string.Empty,
    };
}
