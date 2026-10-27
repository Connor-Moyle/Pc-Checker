using PcChecker.Core.Common;
using PcChecker.Core.Network.Tuning;

namespace PcChecker.Core.Network;

/// <summary>
/// Demo network data for the sample desktop (ASUS Z790-E with Intel Wi-Fi 6E AX211 and Intel I226-V 2.5GbE):
/// "HomeNet" on 5 GHz channel 36 at 78 %, nine nearby access points with a crowded 2.4 GHz channel 6, a clean packet
/// loss test and a 412/38 Mbps speed test with grade B bufferbloat. Pure .NET with short delays so busy states show.
/// When the demo tuning service is registered, applied tuning changes the next Wi-Fi report.
/// </summary>
public sealed class DemoNetworkService : INetworkService
{
    public const string HomeSsid = "HomeNet";
    public const string HomeBssid5 = "70:3a:cb:5e:21:4c";
    public const string HomeBssid24 = "70:3a:cb:5e:21:4b";
    public const string WifiAdapterName = "Wi-Fi";
    public const string EthernetAdapterName = "Ethernet";

    private readonly DemoNetworkTuningService? _tuning;

    public DemoNetworkService()
    {
    }

    public DemoNetworkService(INetworkTuningService tuning)
    {
        _tuning = tuning as DemoNetworkTuningService;
    }

    public async Task<IReadOnlyList<NetworkAdapterStatus>> GetAdaptersAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 250).ConfigureAwait(false);
        string[] dns = _tuning?.DemoDnsServers ?? ["192.168.1.1"];
        return
        [
            new NetworkAdapterStatus(WifiAdapterName, "Intel(R) Wi-Fi 6E AX211 160MHz", NetworkAdapterStatus.WifiKind, "Up", 1_201_000_000,
                "192.168.1.42", "192.168.1.1", dns, "a4:42:3b:9c:10:7e", 1_874_220_311, 23_406_118_902, 0, 0, 12, 0),
            new NetworkAdapterStatus(EthernetAdapterName, "Intel(R) Ethernet Controller I226-V", NetworkAdapterStatus.EthernetKind, "Down", null,
                null, null, [], "a4:42:3b:9c:10:7d", 0, 0, 0, 0, 0, 0),
            new NetworkAdapterStatus("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", NetworkAdapterStatus.OtherKind, "Down", null,
                null, null, [], "a4:42:3b:9c:10:81", 0, 0, null, null, null, null),
        ];
    }

    public async Task<WifiReport> AnalyzeWifiAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading the Wi-Fi adapter", 10));
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Looking at nearby networks", 40));
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Checking Wi-Fi power saving", 75));
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        bool preferFast = _tuning?.IsAdapterSettingRecommended(WifiAdapterName, "RoamingPreferredBandType") == true;
        int signal = preferFast ? 81 : 78;
        var current = new WifiInterfaceInfo(WifiAdapterName, "Intel(R) Wi-Fi 6E AX211 160MHz", "connected", HomeSsid, HomeBssid5, "802.11ax", WifiBands.Band5, 36,
            signal, WifiBands.SignalToDbm(signal), 1201, 1201, "WPA2-Personal", "CCMP");
        IReadOnlyList<WifiNetworkInfo> nearby =
        [
            new(HomeSsid, HomeBssid5, signal, 36, WifiBands.Band5, "802.11ax", "WPA2-Personal", true),
            new(HomeSsid, HomeBssid24, 92, 6, WifiBands.Band24, "802.11ax", "WPA2-Personal", false),
            new("Vodafone-7A2C", "dc:15:c8:7a:2c:10", 64, 6, WifiBands.Band24, "802.11n", "WPA2-Personal", false),
            new("FRITZ!Box 7590 KQ", "3c:a6:2f:11:90:01", 51, 4, WifiBands.Band24, "802.11n", "WPA2-Personal", false),
            new("DIRECT-6B-HP LaserJet", "86:2a:fd:6b:00:12", 44, 6, WifiBands.Band24, "802.11n", "WPA2-Personal", false),
            new("TP-Link_3F10", "50:c7:bf:3f:10:aa", 38, 8, WifiBands.Band24, "802.11n", "WPA2-Personal", false),
            new("Telekom_FON", "34:31:c4:0d:22:7f", 31, 11, WifiBands.Band24, "802.11n", "Open", false),
            new("Vodafone-7A2C-5G", "dc:15:c8:7a:2c:18", 33, 44, WifiBands.Band5, "802.11ac", "WPA2-Personal", false),
            new("TP-Link_3F10_5G", "50:c7:bf:3f:10:ab", 18, 149, WifiBands.Band5, "802.11ac", "WPA2-Personal", false),
        ];

        bool powerSaving = _tuning?.IsActionApplied(TuningActionIds.WifiPowerSave) != true;
        var findings = WifiRules.Build(current, nearby, powerSaving);
        return new WifiReport(current, nearby, ChannelCongestionCalculator.Calculate(nearby), findings, [], DateTimeOffset.Now);
    }

    public async Task<IReadOnlyList<PacketLossResult>> RunPacketLossTestAsync(int count, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 1000);
        var random = DemoData.CreateRandom(count + 11);
        var targets = new (string Target, string Label, double BaseMs, double Spread, double LossRate)[]
        {
            ("gateway", "Your router", 1.6, 0.9, 0),
            ("1.1.1.1", "Cloudflare DNS", 9.8, 1.6, 0),
            ("8.8.8.8", "Google DNS", 13.1, 2.4, 0.01),
        };

        int total = count * targets.Length;
        const int steps = 12;
        for (int step = 1; step <= steps; step++)
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            progress?.Report(PacketLossProbe.Progress(total * step / steps, total, targets.Length));
        }

        var results = new List<PacketLossResult>();
        foreach (var (target, label, baseMs, spread, lossRate) in targets)
        {
            int lost = (int)Math.Round(count * lossRate);
            var samples = Enumerable.Range(0, count - lost)
                .Select(i => Math.Round(Math.Max(0.3, DemoData.Jitter(random, baseMs, spread) + (i % 37 == 0 ? spread * 3 : 0)), 2))
                .ToList();
            results.Add(PacketLossMath.Summarize(target, label, samples, lost));
        }

        return results;
    }

    public async Task<SpeedTestResult> RunSpeedTestAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var random = DemoData.CreateRandom(412);
        for (int i = 0; i <= 10; i++)
        {
            progress?.Report(new ProgressUpdate("Measuring idle latency", 2 + 0.8 * i, $"{DemoData.Jitter(random, 12, 1.5):0} ms"));
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);
        }

        for (int i = 1; i <= 20; i++)
        {
            double mbps = 412 * Math.Min(1, i / 4.0) + DemoData.Jitter(random, 0, 12);
            progress?.Report(new ProgressUpdate("Download", 10 + 45.0 * i / 20, $"{mbps:0} Mbps · {ByteSize.Format(5_000_000L * i)}"));
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }

        for (int i = 1; i <= 15; i++)
        {
            double mbps = 38 * Math.Min(1, i / 3.0) + DemoData.Jitter(random, 0, 1.5);
            progress?.Report(new ProgressUpdate("Upload", 55 + 43.0 * i / 15, $"{mbps:0} Mbps · {ByteSize.Format(25_000_000L * i / 15)}"));
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ProgressUpdate("Finishing", 99));
        return new SpeedTestResult(NetworkSettings.DefaultServer, 412.3, 38.6, 12.0, 48.0, 1.8, 0, 100_000_000, 25_000_000,
            TimeSpan.FromSeconds(11.4), SpeedMath.BufferbloatGrade(12, 48), []);
    }

    public async Task<OperationResult> ResetNetworkStackAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 400, 700).ConfigureAwait(false);
        return OperationResult.Ok("Demo mode: nothing was changed. On a real PC, Winsock and TCP/IP would now be reset and a restart would finish it.", rebootRequired: true);
    }

    public async Task<OperationResult> FlushDnsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 150, 300).ConfigureAwait(false);
        return OperationResult.Ok("Demo mode: the DNS cache would now be cleared.");
    }

    public async Task<OperationResult> RenewDhcpAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 400, 600).ConfigureAwait(false);
        return OperationResult.Ok("Demo mode: the PC would now ask the router for a fresh IP address (it keeps 192.168.1.42).");
    }

    public OperationResult OpenNetworkSettings() => OperationResult.Ok("Demo mode: Settings → Network & internet would open.");

    public OperationResult OpenWifiSettings() => OperationResult.Ok("Demo mode: Settings → Wi-Fi would open.");
}
