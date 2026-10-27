using System.Net;
using PcChecker.Core.Common;

namespace PcChecker.Core.Optimization;

/// <summary>
/// The DNS servers one network adapter had before the Cloudflare DNS action changed them. An empty
/// <see cref="Servers"/> list means the adapter took its DNS servers from DHCP.
/// </summary>
public sealed record DnsBackupEntry(string InterfaceGuid, IReadOnlyList<string> Servers)
{
    public bool WasAutomatic => Servers.Count == 0;
}

/// <summary>
/// Pure helpers that save and restore each adapter's original DNS servers, so Revert puts back exactly what was
/// there (static servers, a Pi-hole, a company DNS server) instead of resetting every adapter to DHCP.
/// </summary>
public static class DnsBackup
{
    public const string CloudflarePrimary = "1.1.1.1";
    public const string CloudflareSecondary = "1.0.0.1";

    /// <summary>
    /// Lists the connected physical adapters with their static IPv4 DNS servers as one JSON array. NameServer is the
    /// registry value Windows keeps for statically set servers; it is empty when the servers come from DHCP.
    /// </summary>
    public const string CaptureScript =
        "ConvertTo-Json -Compress -InputObject @(Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { " +
        "$ns = (Get-ItemProperty -Path ('HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\' + $_.InterfaceGuid) -Name NameServer -ErrorAction SilentlyContinue).NameServer; " +
        "[pscustomobject]@{ Guid = [string]$_.InterfaceGuid; Index = [int]$_.ifIndex; NameServer = [string]$ns } })";

    /// <summary>
    /// Used only when no backup exists (the change was made by an older version that kept none): resets adapters that
    /// use Cloudflare and get their address from DHCP. Adapters with a static address are left alone, because a reset
    /// would leave them with no DNS server at all.
    /// </summary>
    public const string LegacyRevertScript =
        "Get-NetAdapter -Physical | ForEach-Object { " +
        "$dns = (Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses; " +
        "$ip = Get-NetIPInterface -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue; " +
        "if ($dns -contains '1.1.1.1' -and $ip -and $ip.Dhcp -eq 'Enabled') { Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ResetServerAddresses -ErrorAction Stop } }; " +
        "Clear-DnsClientCache";

    /// <summary>Splits a NameServer registry value ("192.168.1.2,8.8.8.8" or space separated) into valid IP addresses.</summary>
    public static IReadOnlyList<string> ParseServers(string? nameServer) =>
        (nameServer ?? string.Empty)
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => IPAddress.TryParse(s, out _))
            .ToList();

    /// <summary>Serializes entries as "{guid}=server,server;{guid}=" for one registry string.</summary>
    public static string Format(IEnumerable<DnsBackupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return string.Join(";", entries.Select(e => e.InterfaceGuid + "=" + string.Join(",", e.Servers)));
    }

    /// <summary>Reads what <see cref="Format"/> wrote; malformed parts (not a GUID, not an IP address) are dropped.</summary>
    public static IReadOnlyList<DnsBackupEntry> Parse(string? text)
    {
        var entries = new List<DnsBackupEntry>();
        foreach (var part in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            var guid = eq < 0 ? part : part[..eq];
            if (!Guid.TryParse(guid, out var id) || entries.Any(e => Guid.Parse(e.InterfaceGuid) == id))
            {
                continue;
            }

            entries.Add(new DnsBackupEntry(id.ToString("B").ToUpperInvariant(), ParseServers(eq < 0 ? null : part[(eq + 1)..])));
        }

        return entries;
    }

    /// <summary>
    /// Adds newly captured adapters to an existing backup. An adapter already in the backup keeps its first record,
    /// so applying twice never replaces the original servers with Cloudflare's.
    /// </summary>
    public static IReadOnlyList<DnsBackupEntry> Merge(IEnumerable<DnsBackupEntry> existing, IEnumerable<DnsBackupEntry> captured)
    {
        var merged = Parse(Format(existing)).ToList();
        foreach (var entry in Parse(Format(captured)))
        {
            if (!merged.Any(e => e.InterfaceGuid == entry.InterfaceGuid))
            {
                merged.Add(entry);
            }
        }

        return merged;
    }

    /// <summary>True when the servers are exactly Cloudflare's, i.e. there is nothing of the user's own to protect.</summary>
    public static bool IsCloudflare(IReadOnlyList<string> servers) =>
        servers.Count > 0 && servers.All(s => s is CloudflarePrimary or CloudflareSecondary);

    /// <summary>
    /// Builds the PowerShell that puts each backed-up adapter back: its own servers when it had static ones, DHCP
    /// (-ResetServerAddresses) only when it had none. Adapters that no longer exist are skipped.
    /// </summary>
    public static string BuildRestoreScript(IEnumerable<DnsBackupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var commands = new List<string>();
        foreach (var entry in Parse(Format(entries)))
        {
            string set = entry.WasAutomatic
                ? "Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction Stop"
                : $"Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses @({string.Join(",", entry.Servers.Select(PowerShellRunner.Quote))}) -ErrorAction Stop";
            commands.Add($"$a = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object {{ $_.InterfaceGuid -eq {PowerShellRunner.Quote(entry.InterfaceGuid)} }} | Select-Object -First 1; if ($a) {{ {set} }}");
        }

        commands.Add("Clear-DnsClientCache");
        return string.Join("; ", commands);
    }
}
