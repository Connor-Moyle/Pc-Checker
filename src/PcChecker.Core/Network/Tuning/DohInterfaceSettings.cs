using System.Net;
using System.Net.Sockets;

namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// Where Windows 11 keeps the per-adapter DNS-over-HTTPS switch (the same values Settings → Network &amp; internet →
/// adapter → DNS server assignment → "DNS over HTTPS: On (automatic template)" writes), plus the global auto-upgrade
/// switch. A server template alone is not enough: Windows only encrypts lookups on an adapter whose DNS server has a
/// non-zero <c>DohFlags</c> value. Pure string logic so it can be tested on any OS.
/// </summary>
public static class DohInterfaceSettings
{
    /// <summary>Per-adapter settings: <c>{guid}\DohInterfaceSettings\Doh|Doh6\&lt;server&gt;</c>.</summary>
    public const string InterfaceParametersKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters";

    /// <summary>Global DNS client settings (holds <see cref="EnableAutoDohValue"/>).</summary>
    public const string DnscacheParametersKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters";

    /// <summary>QWORD under the per-server key; <see cref="AutomaticTemplate"/> means "On (automatic template)".</summary>
    public const string FlagsValue = "DohFlags";

    /// <summary>DWORD that lets Windows upgrade known DoH servers to encrypted lookups automatically.</summary>
    public const string EnableAutoDohValue = "EnableAutoDoh";

    /// <summary><see cref="EnableAutoDohValue"/> value that turns automatic upgrade on.</summary>
    public const int EnableAutoDohOn = 2;

    /// <summary><see cref="FlagsValue"/> value for "use the server's well-known template".</summary>
    public const long AutomaticTemplate = 1;

    /// <summary>Capture-file key for the global switch.</summary>
    public const string GlobalCaptureKey = "global|" + EnableAutoDohValue;

    private const string CapturePrefix = "if:";

    /// <summary>Registry key of the DoH switch for one adapter and one DNS server, or null when the input is unusable.</summary>
    public static string? KeyFor(string interfaceId, string server)
    {
        if (string.IsNullOrWhiteSpace(interfaceId) || !IPAddress.TryParse(server?.Trim(), out var address))
        {
            return null;
        }

        string id = interfaceId.Trim();
        if (!id.StartsWith('{'))
        {
            id = "{" + id + "}";
        }

        string family = address.AddressFamily == AddressFamily.InterNetworkV6 ? "Doh6" : "Doh";
        return $@"{InterfaceParametersKey}\{id.ToUpperInvariant()}\DohInterfaceSettings\{family}\{address}";
    }

    /// <summary>Capture-file key for one adapter/server pair (interface id and server must not contain '|').</summary>
    public static string CaptureKey(string interfaceId, string server) => $"{CapturePrefix}{interfaceId}|{server}|{FlagsValue}";

    /// <summary>Splits a key made by <see cref="CaptureKey"/>; false for any other key.</summary>
    public static bool TryParseCaptureKey(string key, out string interfaceId, out string server)
    {
        interfaceId = server = string.Empty;
        if (!key.StartsWith(CapturePrefix, StringComparison.Ordinal) || !key.EndsWith("|" + FlagsValue, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = key[CapturePrefix.Length..^(FlagsValue.Length + 1)].Split('|');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return false;
        }

        interfaceId = parts[0];
        server = parts[1];
        return true;
    }

    /// <summary>True when a <c>DohFlags</c> value turns encryption on for that server.</summary>
    public static bool IsEncrypted(long? flags) => flags is > 0;
}
