using PcChecker.Core.Common;

namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// DnsClient commands for pointing an adapter at a provider and putting its captured servers back. The captured
/// values are the registry <c>NameServer</c> strings of Tcpip (IPv4) and Tcpip6 (IPv6); empty means "automatic". Pure.
/// </summary>
public static class DnsCommands
{
    /// <summary>
    /// The servers to set for <paramref name="provider"/>: its IPv4 pair, plus its IPv6 pair when the adapter uses IPv6
    /// DNS servers (otherwise those, often the internet provider's, would keep answering lookups).
    /// </summary>
    public static IReadOnlyList<string> ServersFor(DnsProvider provider, bool adapterUsesIpv6Dns)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return adapterUsesIpv6Dns ? [.. provider.Servers, .. provider.Servers6] : provider.Servers;
    }

    public static string Set(int interfaceIndex, IEnumerable<string> servers) =>
        $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ServerAddresses @({string.Join(",", servers.Select(PowerShellRunner.Quote))}) -ErrorAction Stop";

    public static string Reset(int interfaceIndex) =>
        $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ResetServerAddresses -ErrorAction Stop";

    /// <summary>
    /// Puts an adapter's captured servers back. <paramref name="ipv6NameServer"/> is null for a capture made before IPv6
    /// was recorded; only IPv4 is restored then, as PC Checker only changed IPv4. Otherwise both families are reset to
    /// automatic first and the ones that had servers typed in get them back.
    /// </summary>
    public static string Restore(int interfaceIndex, string? ipv4NameServer, string? ipv6NameServer)
    {
        var ipv4 = Split(ipv4NameServer);
        if (ipv6NameServer is null)
        {
            return ipv4.Count == 0 ? Reset(interfaceIndex) : Set(interfaceIndex, ipv4);
        }

        var all = ipv4.Concat(Split(ipv6NameServer)).ToList();
        return all.Count == 0 ? Reset(interfaceIndex) : Reset(interfaceIndex) + "; " + Set(interfaceIndex, all);
    }

    /// <summary>Splits a registry NameServer value ("1.1.1.1,1.0.0.1" or space separated).</summary>
    public static IReadOnlyList<string> Split(string? nameServer) =>
        (nameServer ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
