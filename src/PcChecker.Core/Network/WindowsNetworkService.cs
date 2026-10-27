using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Latency;
using PcChecker.Core.Network.Tuning;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Network;

/// <summary>
/// Windows implementation of <see cref="INetworkService"/>: adapters from System.Net.NetworkInformation, Wi-Fi from
/// <c>netsh wlan</c> (parsed by shape so localized Windows works), ICMP packet loss, an HTTP speed test and the
/// ipconfig/netsh repair tools behind the change guard. Every probe is guarded; failures become warnings.
/// </summary>
public sealed class WindowsNetworkService : INetworkService
{
    private const string Module = "Network";

    private readonly IChangeGuard _guard;
    private readonly ISettingsService _settings;
    private readonly ILogger<WindowsNetworkService> _logger;

    public WindowsNetworkService(IChangeGuard guard, ISettingsService settings, ILogger<WindowsNetworkService> logger)
    {
        _guard = guard;
        _settings = settings;
        _logger = logger;
    }

    private NetworkSettings Options => _settings.Current.Network ?? new NetworkSettings();

    public Task<IReadOnlyList<NetworkAdapterStatus>> GetAdaptersAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadAdapters(), cancellationToken);

    private IReadOnlyList<NetworkAdapterStatus> ReadAdapters()
    {
        var list = new List<NetworkAdapterStatus>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list network interfaces.");
            return list;
        }

        foreach (var nic in interfaces)
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                list.Add(ReadAdapter(nic));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping adapter {Name}.", nic.Name);
            }
        }

        // Connected physical adapters first, then by kind (Wi-Fi, Ethernet, other).
        return list
            .OrderByDescending(a => a.IsUp)
            .ThenBy(a => a.Kind == NetworkAdapterStatus.WifiKind ? 0 : a.Kind == NetworkAdapterStatus.EthernetKind ? 1 : 2)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static NetworkAdapterStatus ReadAdapter(NetworkInterface nic)
    {
        string kind = nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => NetworkAdapterStatus.WifiKind,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => NetworkAdapterStatus.EthernetKind,
            _ => NetworkAdapterStatus.OtherKind,
        };

        string? ipv4 = null, gateway = null;
        var dns = new List<string>();
        try
        {
            var props = nic.GetIPProperties();
            ipv4 = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            gateway = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any))?.ToString()
                ?? props.GatewayAddresses.FirstOrDefault()?.Address.ToString();
            dns.AddRange(props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()));
            dns.AddRange(props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal).Select(a => a.ToString()));
        }
        catch
        {
            // Some virtual adapters have no IP properties.
        }

        long sent = 0, received = 0;
        long? errorsIn = null, errorsOut = null, discardsIn = null, discardsOut = null;
        try
        {
            var stats = nic.GetIPStatistics();
            sent = stats.BytesSent;
            received = stats.BytesReceived;
            errorsIn = Safe(() => stats.IncomingPacketsWithErrors);
            errorsOut = Safe(() => stats.OutgoingPacketsWithErrors);
            discardsIn = Safe(() => stats.IncomingPacketsDiscarded);
            discardsOut = Safe(() => stats.OutgoingPacketsDiscarded);
        }
        catch
        {
            // Counters are not available for every adapter.
        }

        long? speed = Safe(() => nic.Speed);
        string mac = string.Empty;
        try
        {
            var bytes = nic.GetPhysicalAddress().GetAddressBytes();
            mac = bytes.Length == 0 ? string.Empty : string.Join(":", bytes.Select(b => b.ToString("x2")));
        }
        catch
        {
        }

        return new NetworkAdapterStatus(nic.Name, nic.Description, kind, nic.OperationalStatus.ToString(),
            speed is > 0 ? speed : null, ipv4, gateway, dns, mac.Length == 0 ? null : mac, sent, received, errorsIn, errorsOut, discardsIn, discardsOut);
    }

    public Task<WifiReport> AnalyzeWifiAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(WifiReport.Empty("The Wi-Fi analysis needs Windows."));
        }

        return Task.Run(() => AnalyzeWifiCoreAsync(progress, cancellationToken), cancellationToken);
    }

    private async Task<WifiReport> AnalyzeWifiCoreAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        progress?.Report(new ProgressUpdate("Reading the Wi-Fi adapter", 10));
        bool hasWireless = NetworkSystemInfo.HasWirelessAdapter();
        var interfacesRun = await LatencyProbes.RunAsync("netsh.exe", "wlan show interfaces", cancellationToken, 30).ConfigureAwait(false);
        string interfacesText = interfacesRun.Output + "\n" + interfacesRun.Error;
        if (NetshWlanParser.IsWlanServiceStopped(interfacesText) && !hasWireless)
        {
            return WifiReport.Empty(WifiReport.NoAdapterPrefix + " this PC has no Wi-Fi adapter (or it is switched off), so it is using a cable or another connection. The Wi-Fi checks are skipped.");
        }

        if (NetshWlanParser.IsWlanServiceStopped(interfacesText))
        {
            warnings.Add("The WLAN AutoConfig service (wlansvc) is not running, so Windows cannot manage Wi-Fi. Start it in Services (set it to Automatic) and run the test again.");
        }

        var interfaces = NetshWlanParser.ParseInterfaces(interfacesRun.Output);
        bool locationBlocked = NetshWlanParser.IsLocationPermissionError(interfacesText);
        if (interfaces.Count == 0)
        {
            if (!hasWireless)
            {
                return WifiReport.Empty(WifiReport.NoAdapterPrefix + " this PC has no Wi-Fi adapter (or it is switched off), so it is using a cable or another connection. The Wi-Fi checks are skipped.");
            }

            if (!locationBlocked && warnings.Count == 0)
            {
                warnings.Add("Windows has a Wi-Fi adapter, but netsh did not describe it" + (interfacesRun.ExitCode != 0 ? $" (exit code {interfacesRun.ExitCode})" : string.Empty) + ". Turn Wi-Fi on in the quick settings and try again.");
            }
        }

        var current = interfaces.FirstOrDefault(i => i.IsConnected) ?? interfaces.FirstOrDefault();

        progress?.Report(new ProgressUpdate("Looking at nearby networks", 40));
        IReadOnlyList<WifiNetworkInfo> nearby = [];
        var networksRun = await LatencyProbes.RunAsync("netsh.exe", "wlan show networks mode=bssid", cancellationToken, 30).ConfigureAwait(false);
        string networksText = networksRun.Output + "\n" + networksRun.Error;
        locationBlocked |= NetshWlanParser.IsLocationPermissionError(networksText);
        if (locationBlocked)
        {
            warnings.Add(WifiReport.LocationPermissionPrefix + " Windows 11 only lets apps list Wi-Fi networks when Location is allowed for desktop apps. Turn on Location (Settings → Privacy & security → Location → Let desktop apps access your location) and run the test again.");
        }
        else
        {
            nearby = NetshWlanParser.ParseNetworks(networksRun.Output, current?.Bssid);
        }

        if (nearby.Count == 0 && current is { IsConnected: true, Channel: { } channel, Bssid: { } bssid })
        {
            // Without a scan, at least chart your own network.
            nearby = [new WifiNetworkInfo(current.Ssid ?? string.Empty, bssid, current.SignalPercent ?? 0, channel, current.Band ?? WifiBands.FromChannel(channel), current.Radio ?? string.Empty, current.Authentication ?? string.Empty, true)];
        }

        progress?.Report(new ProgressUpdate("Checking Wi-Fi power saving", 75));
        bool? powerSaving = null;
        try
        {
            var (ac, dc) = await LatencyProbes.ReadPowerSettingAsync(NetworkTuningCatalog.WirelessSubgroup, NetworkTuningCatalog.WirelessPowerSavingSetting, cancellationToken).ConfigureAwait(false);
            if (ac is not null)
            {
                powerSaving = ac > 0 || (NetworkSystemInfo.HasBattery() && dc > 0);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the Wi-Fi power saving mode.");
        }

        progress?.Report(new ProgressUpdate("Evaluating", 95));
        var channels = ChannelCongestionCalculator.Calculate(nearby);
        var findings = WifiRules.Build(current, nearby, powerSaving);
        _logger.LogInformation("Wi-Fi analysis: {Adapter}, signal {Signal}%, {Nearby} nearby BSSIDs, {Findings} findings.", current?.Description ?? "none", current?.SignalPercent, nearby.Count, findings.Count);
        return new WifiReport(current, nearby, channels, findings, warnings, DateTimeOffset.Now);
    }

    public async Task<IReadOnlyList<PacketLossResult>> RunPacketLossTestAsync(int count, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 1000);
        var targets = PacketLossProbe.Targets(Options);
        int total = count * targets.Count;
        int done = 0;
        progress?.Report(PacketLossProbe.Progress(0, total, targets.Count));
        void OnReply()
        {
            int value = Interlocked.Increment(ref done);
            if (value % 5 == 0 || value == total)
            {
                progress?.Report(PacketLossProbe.Progress(value, total, targets.Count));
            }
        }

        var tasks = targets.Select(t => PacketLossProbe.RunAsync(t.Target, t.Label, count, OnReply, cancellationToken)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        _logger.LogInformation("Packet loss test: {Results}", string.Join("; ", results.Select(r => $"{r.Label} {r.LossPercent}% loss, avg {r.AvgMs} ms")));
        return results;
    }

    public async Task<SpeedTestResult> RunSpeedTestAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var result = await Task.Run(() => NetworkSpeedTester.RunAsync(Options, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Speed test via {Server}: {Down} / {Up} Mbps, idle {Idle} ms, loaded {Loaded} ms, grade {Grade}, {Data} used.",
            result.Server, result.DownloadMbps, result.UploadMbps, result.IdleLatencyMs, result.LoadedLatencyMs, result.Grade, SpeedMath.DataUsedText(result.BytesDownloaded, result.BytesUploaded));
        return result;
    }

    public Task<OperationResult> FlushDnsAsync(CancellationToken cancellationToken)
    {
        var options = new ChangeOptions(false, false, Module, "DNS cache", false, "Nothing to undo: Windows refills the cache as you browse.");
        return _guard.RunGuardedAsync("Flush the DNS cache", options, async ct =>
        {
            var run = await LatencyProbes.RunAsync("ipconfig.exe", "/flushdns", ct, 30).ConfigureAwait(false);
            return run.ExitCode == 0
                ? OperationResult.Ok("The DNS cache was cleared. Websites are looked up fresh the next time you open them.")
                : OperationResult.Fail("ipconfig /flushdns failed: " + Describe(run));
        }, null, cancellationToken);
    }

    public Task<OperationResult> RenewDhcpAsync(CancellationToken cancellationToken)
    {
        var options = new ChangeOptions(false, false, Module, "DHCP lease (all adapters)", false, "Nothing to undo: the router hands out the address again.");
        return _guard.RunGuardedAsync("Renew the IP address (DHCP)", options, async ct =>
        {
            var release = await LatencyProbes.RunAsync("ipconfig.exe", "/release", ct, 60).ConfigureAwait(false);
            var renew = await LatencyProbes.RunAsync("ipconfig.exe", "/renew", ct, 120).ConfigureAwait(false);
            if (renew.ExitCode == 0)
            {
                return OperationResult.Ok("The PC asked the router for a fresh IP address and got one." + (release.ExitCode != 0 ? " (Some adapters use a fixed address and were skipped.)" : string.Empty));
            }

            return OperationResult.Fail("ipconfig /renew did not finish cleanly: " + Describe(renew) + " If you have no connection now, unplug and reconnect the cable or Wi-Fi, or restart the router.");
        }, null, cancellationToken);
    }

    public Task<OperationResult> ResetNetworkStackAsync(CancellationToken cancellationToken)
    {
        var options = ChangeOptions.For(Module, "Winsock and TCP/IP", reversible: false,
            "Use the restore point created before the reset (if any). Re-enter any fixed IP address or DNS server you had set by hand.");
        return _guard.RunGuardedAsync("Reset the network stack (Winsock and TCP/IP)", options, async ct =>
        {
            var winsock = await LatencyProbes.RunAsync("netsh.exe", "winsock reset", ct, 120).ConfigureAwait(false);
            if (winsock.ExitCode != 0)
            {
                return OperationResult.Fail("netsh winsock reset failed: " + Describe(winsock));
            }

            var ip = await LatencyProbes.RunAsync("netsh.exe", "int ip reset", ct, 120).ConfigureAwait(false);
            // "int ip reset" reports "Access is denied" for a few protected keys even when it succeeds; only fail when it did not run.
            if (ip.ExitCode != 0 && string.IsNullOrWhiteSpace(ip.Output))
            {
                return OperationResult.Fail("netsh int ip reset failed: " + Describe(ip));
            }

            return OperationResult.Ok("Winsock and TCP/IP were reset to their defaults. Restart the PC to finish.", rebootRequired: true);
        }, null, cancellationToken);
    }

    public OperationResult OpenNetworkSettings() => ShellLauncher.Open("ms-settings:network-status");

    public OperationResult OpenWifiSettings() => ShellLauncher.Open("ms-settings:network-wifi");

    private static long? Safe(Func<long> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static string Describe((int ExitCode, string Output, string Error) run) =>
        !string.IsNullOrWhiteSpace(run.Error) ? run.Error.Trim()
        : !string.IsNullOrWhiteSpace(run.Output) ? run.Output.Trim()
        : $"exit code {run.ExitCode}";
}
