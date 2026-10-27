using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Network;

/// <summary>The Wi-Fi adapter and the connection it currently holds (from <c>netsh wlan show interfaces</c>).</summary>
public sealed record WifiInterfaceInfo(
    string Name,
    string Description,
    string State,
    string? Ssid,
    string? Bssid,
    string? Radio,
    string? Band,
    int? Channel,
    int? SignalPercent,
    int? RssiDbm,
    double? ReceiveRateMbps,
    double? TransmitRateMbps,
    string? Authentication,
    string? Cipher)
{
    /// <summary>True when the adapter is associated with an access point.</summary>
    public bool IsConnected => !string.IsNullOrWhiteSpace(Bssid) || !string.IsNullOrWhiteSpace(Ssid);
}

/// <summary>One access point radio (BSSID) visible nearby.</summary>
public sealed record WifiNetworkInfo(string Ssid, string Bssid, int SignalPercent, int Channel, string Band, string Radio, string Authentication, bool IsConnected);

/// <summary>How many access points share or overlap a channel.</summary>
public sealed record ChannelCongestion(string Band, int Channel, int NetworkCount, int StrongestSignalPercent);

/// <summary>Result of the Wi-Fi analysis.</summary>
public sealed record WifiReport(
    WifiInterfaceInfo? Current,
    IReadOnlyList<WifiNetworkInfo> Nearby,
    IReadOnlyList<ChannelCongestion> Channels,
    IReadOnlyList<NetworkFinding> Findings,
    IReadOnlyList<string> Warnings,
    DateTimeOffset Captured)
{
    /// <summary>Warning text prefix used when Windows refuses the nearby-network scan until Location is allowed.</summary>
    public const string LocationPermissionPrefix = "Location permission needed:";

    /// <summary>Warning text prefix used when the PC has no Wi-Fi adapter (not an error).</summary>
    public const string NoAdapterPrefix = "No Wi-Fi adapter:";

    /// <summary>True when Windows 11 blocked the BSSID scan because apps may not use the location.</summary>
    public bool NeedsLocationPermission => Warnings.Any(w => w.StartsWith(LocationPermissionPrefix, StringComparison.Ordinal));

    /// <summary>True when no wireless adapter was found at all.</summary>
    public bool HasNoWifiAdapter => Warnings.Any(w => w.StartsWith(NoAdapterPrefix, StringComparison.Ordinal));

    public static WifiReport Empty(params string[] warnings) => new(null, [], [], [], warnings, DateTimeOffset.Now);
}

/// <summary>Live status and counters of one network adapter.</summary>
public sealed record NetworkAdapterStatus(
    string Name,
    string Description,
    string Kind,
    string Status,
    long? LinkSpeedBps,
    string? Ipv4,
    string? Gateway,
    IReadOnlyList<string> Dns,
    string? Mac,
    long BytesSent,
    long BytesReceived,
    long? ErrorsIn,
    long? ErrorsOut,
    long? DiscardsIn,
    long? DiscardsOut)
{
    public const string WifiKind = "Wi-Fi";
    public const string EthernetKind = "Ethernet";
    public const string OtherKind = "Other";

    public bool IsUp => string.Equals(Status, "Up", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Packet loss and latency towards one target.</summary>
public sealed record PacketLossResult(string Target, string Label, int Sent, int Received, double LossPercent, double? AvgMs, double? MinMs, double? MaxMs, double? JitterMs, double? P95Ms);

/// <summary>Result of the HTTP speed test.</summary>
public sealed record SpeedTestResult(
    string Server,
    double? DownloadMbps,
    double? UploadMbps,
    double? IdleLatencyMs,
    double? LoadedLatencyMs,
    double? JitterMs,
    double PacketLossPercent,
    long BytesDownloaded,
    long BytesUploaded,
    TimeSpan Duration,
    string Grade,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Extra delay under load (loaded minus idle latency), the bufferbloat measure.</summary>
    public double? BufferbloatMs => LoadedLatencyMs is { } l && IdleLatencyMs is { } i ? Math.Max(0, l - i) : null;
}

/// <summary>One Wi-Fi or network problem with plain-English advice. <see cref="ActionId"/> names a tuning action that fixes it.</summary>
public sealed record NetworkFinding(string Id, Severity Severity, string Title, string Detail, string? ActionId, RiskLevel Risk);

/// <summary>Network page settings (AppSettings.Network). Missing fields in the JSON file keep these defaults.</summary>
public sealed class NetworkSettings
{
    public const string DefaultServer = "speed.cloudflare.com";

    public string SpeedTestServer { get; set; } = DefaultServer;
    public int DownloadMegabytes { get; set; } = 100;
    public int UploadMegabytes { get; set; } = 25;
    public int PacketLossCount { get; set; } = 100;

    /// <summary>Hosts to ping; "gateway" means the default gateway.</summary>
    public List<string> PacketLossTargets { get; set; } = ["gateway", "1.1.1.1", "8.8.8.8"];
}
