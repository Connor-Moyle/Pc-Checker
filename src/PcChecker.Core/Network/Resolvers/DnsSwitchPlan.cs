using System.Net;
using System.Net.Sockets;
using PcChecker.Core.Network.Tuning;

namespace PcChecker.Core.Network.Resolvers;

/// <summary>Why a switch cannot run.</summary>
public enum DnsSwitchProblem
{
    None,
    NoAdapter,
    NoServers,
    InvalidAddress,
    WrongFamily,
    TooManyServers,
}

/// <summary>Builds and checks DNS switches, and reads the DNS-over-HTTPS state. Pure.</summary>
public static class DnsSwitchPlan
{
    /// <summary>Windows takes any number of servers, but more than three per family only slows down failures.</summary>
    public const int MaxServersPerFamily = 3;

    /// <summary>A switch to <paramref name="provider"/>'s pair, plus its IPv6 pair when <paramref name="includeIpv6"/>.</summary>
    public static DnsChangeRequest ForProvider(DnsProvider provider, IReadOnlyList<string> adapterIds, bool includeIpv6, bool encrypt)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new DnsChangeRequest(adapterIds, provider.Name, provider.Servers, includeIpv6 ? provider.Servers6 : [], encrypt);
    }

    /// <summary>A switch to servers typed in by hand. Blank entries are dropped; addresses are sorted into IPv4 and IPv6.</summary>
    public static DnsChangeRequest ForCustom(IEnumerable<string?> servers, IReadOnlyList<string> adapterIds, string label, bool encrypt)
    {
        var list = servers.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
        var v4 = list.Where(s => !IsIpv6(s)).ToList();
        var v6 = list.Where(IsIpv6).ToList();
        return new DnsChangeRequest(adapterIds, label, v4, v6, encrypt);
    }

    public static DnsSwitchProblem Validate(DnsChangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AdapterIds is null || request.AdapterIds.Count == 0)
        {
            return DnsSwitchProblem.NoAdapter;
        }

        if (request.Servers4.Count == 0 && request.Servers6.Count == 0)
        {
            return DnsSwitchProblem.NoServers;
        }

        if (request.Servers4.Concat(request.Servers6).Any(s => !DnsResolverCatalog.IsServerAddress(s)))
        {
            return DnsSwitchProblem.InvalidAddress;
        }

        if (request.Servers4.Any(IsIpv6) || request.Servers6.Any(s => !IsIpv6(s)))
        {
            return DnsSwitchProblem.WrongFamily;
        }

        return request.Servers4.Count > MaxServersPerFamily || request.Servers6.Count > MaxServersPerFamily
            ? DnsSwitchProblem.TooManyServers
            : DnsSwitchProblem.None;
    }

    /// <summary>The servers to set, IPv4 first, in canonical form and without duplicates.</summary>
    public static IReadOnlyList<string> Servers(DnsChangeRequest request) =>
        request.Servers4.Concat(request.Servers6).Select(DnsResolverCatalog.Normalize).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The servers of a request that have a known DNS-over-HTTPS template (empty when encryption was not asked for).</summary>
    public static IReadOnlyList<(string Server, string Template)> DohServers(DnsChangeRequest request) =>
        !request.EncryptWithDoh
            ? []
            : Servers(request).Select(s => (Server: s, Template: DnsResolverCatalog.DohTemplateFor(s))).Where(x => x.Template is not null).Select(x => (x.Server, x.Template!)).ToList();

    /// <summary>True when every server of the request offers DNS over HTTPS (so "Encrypt" can be offered).</summary>
    public static bool CanEncrypt(DnsChangeRequest request)
    {
        var servers = Servers(request);
        return servers.Count > 0 && servers.All(s => DnsResolverCatalog.DohTemplateFor(s) is not null);
    }

    /// <summary>
    /// Whether one server's lookups are encrypted: its per-adapter switch is on, or Windows' automatic upgrade is on
    /// and the server's template allows it.
    /// </summary>
    public static bool IsServerEncrypted(long? dohFlags, bool globalAutoUpgrade, bool serverAutoUpgrade) =>
        DohInterfaceSettings.IsEncrypted(dohFlags) || (globalAutoUpgrade && serverAutoUpgrade);

    /// <summary>The adapter's state from its servers' states: On when all are encrypted, Partly when some, Off when none.</summary>
    public static DnsEncryptionState Combine(IReadOnlyList<bool> serversEncrypted)
    {
        ArgumentNullException.ThrowIfNull(serversEncrypted);
        if (serversEncrypted.Count == 0)
        {
            return DnsEncryptionState.Off;
        }

        int on = serversEncrypted.Count(e => e);
        return on == serversEncrypted.Count ? DnsEncryptionState.On : on == 0 ? DnsEncryptionState.Off : DnsEncryptionState.Partly;
    }

    private static bool IsIpv6(string server) =>
        IPAddress.TryParse(server?.Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
}
