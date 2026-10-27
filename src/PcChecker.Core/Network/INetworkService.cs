using PcChecker.Core.Common;

namespace PcChecker.Core.Network;

/// <summary>Network adapters, Wi-Fi analysis, packet loss and speed tests, and the basic network repair tools.</summary>
public interface INetworkService
{
    Task<IReadOnlyList<NetworkAdapterStatus>> GetAdaptersAsync(CancellationToken cancellationToken);

    Task<WifiReport> AnalyzeWifiAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    Task<IReadOnlyList<PacketLossResult>> RunPacketLossTestAsync(int count, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    Task<SpeedTestResult> RunSpeedTestAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Winsock and TCP/IP reset. Needs a restart; goes through the change guard.</summary>
    Task<OperationResult> ResetNetworkStackAsync(CancellationToken cancellationToken);

    Task<OperationResult> FlushDnsAsync(CancellationToken cancellationToken);

    /// <summary>Releases and renews the DHCP lease (the connection drops for a few seconds).</summary>
    Task<OperationResult> RenewDhcpAsync(CancellationToken cancellationToken);

    OperationResult OpenNetworkSettings();

    OperationResult OpenWifiSettings();
}
