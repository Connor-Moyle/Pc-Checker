using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Converters;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Network;
using PcChecker.Core.Network.Tuning;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// View model for the Network page: connection summary, Wi-Fi analysis with findings, nearby networks and channel
/// congestion, speed and packet-loss tests, adapter counters, repair tools, and the Wi-Fi tuning section.
/// </summary>
public sealed partial class NetworkViewModel : ViewModelBase
{
    public const string LocationSettingsUri = "ms-settings:privacy-location";

    private readonly INetworkService _network;
    private readonly INetworkTuningService _tuning;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<NetworkViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWifiReport), nameof(HasWifiConnection), nameof(ShowNoWifi), nameof(NeedsLocationPermission), nameof(HasFindings), nameof(HasNearby))]
    private WifiReport? _wifiReport;

    [ObservableProperty] private string _adapterText = "—";
    [ObservableProperty] private string? _adapterSubtitle;
    [ObservableProperty] private string _ipText = "—";
    [ObservableProperty] private string? _ipSubtitle;
    [ObservableProperty] private string _gatewayText = "—";
    [ObservableProperty] private string? _gatewaySubtitle;
    [ObservableProperty] private string _linkSpeedText = "—";
    [ObservableProperty] private string? _linkSpeedUnit;
    [ObservableProperty] private string _signalText = "—";
    [ObservableProperty] private string? _signalSubtitle;

    [ObservableProperty] private string? _wifiSsid;
    [ObservableProperty] private string? _wifiBandChannel;
    [ObservableProperty] private double _wifiSignal;
    [ObservableProperty] private string? _wifiDbmText;
    [ObservableProperty] private string? _wifiQualityText;
    [ObservableProperty] private Brush _wifiQualityBrush = HealthStatusToBrushConverter.Unknown;
    [ObservableProperty] private string? _wifiRadioText;
    [ObservableProperty] private string? _wifiRatesText;
    [ObservableProperty] private string? _wifiSecurityText;
    [ObservableProperty] private string? _wifiAdapterText;
    [ObservableProperty] private string? _wifiCapturedText;
    [ObservableProperty] private string? _noWifiMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWifiWarnings))]
    private string? _wifiWarningsText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAdapters))]
    private int _adapterCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAdapterErrors))]
    private string? _adapterErrorsText;

    [ObservableProperty]
    private bool _isRebootRequired;

    public NetworkViewModel(ISettingsService settings, INetworkService network, INetworkTuningService tuning, IDialogService dialogs,
        INotificationService notifications, INavigationService navigation, ILogger<NetworkViewModel> logger)
        : base(settings)
    {
        _network = network;
        _tuning = tuning;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
        _selectedPacketCount = Math.Clamp(settings.Current.Network?.PacketLossCount ?? 100, 1, 1000);
        if (!PacketCountOptions.Contains(_selectedPacketCount))
        {
            _selectedPacketCount = 100;
        }

        PropertyChanged += OnOwnPropertyChanged;
    }

    public ObservableCollection<NetworkFindingItem> Findings { get; } = [];
    public ObservableCollection<NearbyNetworkItem> Nearby { get; } = [];
    public ObservableCollection<ChannelBandGroup> ChannelGroups { get; } = [];
    public ObservableCollection<AdapterRow> Adapters { get; } = [];
    public ObservableCollection<WifiAdviceItem> Advice { get; } = [];

    public bool HasWifiReport => WifiReport is not null;
    public bool HasWifiConnection => WifiReport?.Current?.IsConnected == true;
    public bool ShowNoWifi => WifiReport is not null && WifiReport.Current?.IsConnected != true && !WifiReport.NeedsLocationPermission;
    public bool NeedsLocationPermission => WifiReport?.NeedsLocationPermission == true;
    public bool HasFindings => Findings.Count > 0;
    public bool HasNearby => Nearby.Count > 0;
    public bool HasChannels => ChannelGroups.Count > 0;
    public bool HasWifiWarnings => !string.IsNullOrWhiteSpace(WifiWarningsText);
    public bool HasAdapters => AdapterCount > 0;
    public bool HasAdapterErrors => !string.IsNullOrWhiteSpace(AdapterErrorsText);

    /// <summary>True while any test, refresh or change runs; header buttons are disabled.</summary>
    public bool IsIdle => !IsBusy && !IsSpeedTesting && !IsPacketTesting;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Adapters, Wi-Fi and tuning state; the speed test only runs when the user asks (it uses data).
        await RefreshCoreAsync();
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RefreshCoreAsync();

    [RelayCommand]
    private Task RefreshAllAsync() => RefreshCoreAsync();

    private Task RefreshCoreAsync() => RunBusyAsync(async ct =>
    {
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Percent is { } pct ? $"{p.Stage} ({pct:0}%)" : p.Stage);
        BusyText = "Reading network adapters…";
        var adapters = await _network.GetAdaptersAsync(ct);
        ApplyAdapters(adapters);

        var report = await _network.AnalyzeWifiAsync(progress, ct);
        ApplyWifi(report);

        BusyText = "Reading tuning settings…";
        await LoadTuningCoreAsync(ct);
    }, "Checking your connection…");

    [RelayCommand]
    private Task AnalyzeWifiAsync() => RunBusyAsync(async ct =>
    {
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage);
        ApplyWifi(await _network.AnalyzeWifiAsync(progress, ct));
    }, "Analyzing Wi-Fi…");

    private void ApplyAdapters(IReadOnlyList<NetworkAdapterStatus> adapters)
    {
        Adapters.Clear();
        foreach (var adapter in adapters)
        {
            Adapters.Add(new AdapterRow(adapter));
        }

        AdapterCount = Adapters.Count;
        var withErrors = adapters.Where(a => (a.ErrorsIn ?? 0) + (a.ErrorsOut ?? 0) > 0).ToList();
        AdapterErrorsText = withErrors.Count == 0
            ? null
            : $"{string.Join(", ", withErrors.Select(a => a.Name))} reported packet errors since Windows started. A few are harmless; thousands point at a bad cable, port or driver.";

        var main = adapters.FirstOrDefault(a => a.IsUp && a.Gateway is not null) ?? adapters.FirstOrDefault(a => a.IsUp);
        if (main is null)
        {
            AdapterText = "Offline";
            AdapterSubtitle = "No adapter is connected";
            IpText = GatewayText = LinkSpeedText = "—";
            IpSubtitle = GatewaySubtitle = LinkSpeedUnit = null;
            return;
        }

        AdapterText = main.Kind == NetworkAdapterStatus.OtherKind ? main.Name : main.Kind;
        AdapterSubtitle = main.Description;
        IpText = main.Ipv4 ?? "—";
        IpSubtitle = "This PC's address on your network";
        GatewayText = main.Gateway ?? "—";
        GatewaySubtitle = main.Dns.Count == 0 ? "Your router" : $"DNS: {string.Join(", ", main.Dns.Take(2))}";
        if (main.LinkSpeedBps is { } bps)
        {
            bool giga = bps >= 1_000_000_000;
            LinkSpeedText = giga ? $"{bps / 1_000_000_000.0:0.#}" : $"{bps / 1_000_000.0:0}";
            LinkSpeedUnit = giga ? "Gbps" : "Mbps";
        }
        else
        {
            LinkSpeedText = "—";
            LinkSpeedUnit = null;
        }
    }

    private void ApplyWifi(WifiReport report)
    {
        WifiReport = report;
        var current = report.Current;
        WifiCapturedText = $"Checked {report.Captured:t}";
        // The location and no-adapter notes have their own InfoBars; list only the other warnings here.
        var warnings = report.Warnings
            .Where(w => !w.StartsWith(WifiReport.LocationPermissionPrefix, StringComparison.Ordinal) && !w.StartsWith(WifiReport.NoAdapterPrefix, StringComparison.Ordinal))
            .ToList();
        WifiWarningsText = warnings.Count == 0 ? null : string.Join("\n", warnings);

        if (current?.IsConnected == true)
        {
            WifiSsid = current.Ssid ?? "(hidden network)";
            WifiBandChannel = current.Channel is { } ch ? $"{current.Band} · channel {ch}" : current.Band;
            WifiSignal = current.SignalPercent ?? 0;
            int dbm = current.RssiDbm ?? WifiBands.SignalToDbm(current.SignalPercent ?? 0);
            WifiDbmText = $"{dbm} dBm";
            WifiQualityText = WifiBands.DescribeSignal(current.SignalPercent);
            WifiQualityBrush = NetworkUi.Signal(current.SignalPercent);
            WifiRadioText = WifiBands.DescribeRadio(current.Radio);
            WifiRatesText = current.ReceiveRateMbps is null && current.TransmitRateMbps is null
                ? "—"
                : $"↓ {NetworkUi.Mbps(current.ReceiveRateMbps)} / ↑ {NetworkUi.Mbps(current.TransmitRateMbps)} Mbps";
            WifiSecurityText = string.IsNullOrWhiteSpace(current.Cipher) ? current.Authentication ?? "—" : $"{current.Authentication} ({current.Cipher})";
            WifiAdapterText = string.IsNullOrWhiteSpace(current.Description) ? current.Name : current.Description;
            SignalText = $"{current.SignalPercent}";
            SignalSubtitle = $"{WifiQualityText} · {WifiDbmText}";
            NoWifiMessage = null;
        }
        else
        {
            WifiSignal = 0;
            SignalText = "—";
            SignalSubtitle = report.HasNoWifiAdapter ? "No Wi-Fi adapter" : "Not on Wi-Fi";
            NoWifiMessage = report.HasNoWifiAdapter
                ? "This PC has no Wi-Fi adapter (or it is switched off), so it uses a cable or another connection. The speed and packet loss tests below still work."
                : current is not null
                    ? $"The Wi-Fi adapter ({current.Description}) is not connected to a network. Connect to Wi-Fi and run the analysis again."
                    : "Windows did not describe a Wi-Fi connection. Turn Wi-Fi on in the quick settings, then analyze again.";
        }

        var actions = _tuning.Actions;
        Findings.Clear();
        foreach (var finding in report.Findings)
        {
            var action = finding.ActionId is null ? null : actions.FirstOrDefault(a => a.Id == finding.ActionId);
            Findings.Add(new NetworkFindingItem(finding, action is { AdvancedOnly: true } && !IsAdvancedMode ? null : action));
        }

        Nearby.Clear();
        foreach (var network in report.Nearby.OrderByDescending(n => n.IsConnected).ThenByDescending(n => n.SignalPercent))
        {
            Nearby.Add(new NearbyNetworkItem(network));
        }

        BuildChannelGroups(report);
        Advice.Clear();
        foreach (var item in WifiAdvice.Build(report))
        {
            Advice.Add(item);
        }

        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasNearby));
        OnPropertyChanged(nameof(HasChannels));
    }

    private void BuildChannelGroups(WifiReport report)
    {
        ChannelGroups.Clear();
        var current = report.Current;
        int best = ChannelCongestionCalculator.BestChannel24(report.Nearby, current?.Bssid);
        foreach (var band in report.Channels.GroupBy(c => c.Band))
        {
            int max = Math.Max(1, band.Max(c => c.NetworkCount));
            var bars = band.Select(c => new ChannelBarItem(c.Channel, c.NetworkCount, max,
                IsYours: current?.Band == band.Key && current.Channel == c.Channel,
                IsRecommended: band.Key == WifiBands.Band24 && c.Channel == best && !(current?.Band == band.Key && current.Channel == best),
                c.StrongestSignalPercent)).ToList();
            string caption = band.Key == WifiBands.Band24
                ? "Networks overlap when they are less than 5 channels apart. Only 1, 6 and 11 do not overlap."
                : "Each channel is separate; only networks on the same channel compete.";
            ChannelGroups.Add(new ChannelBandGroup(band.Key, caption, bars));
        }
    }

    // ---------------------------------------------------------------- fix tools

    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        bool ok = await _dialogs.ConfirmAsync("Flush the DNS cache?",
            "Windows forgets the web addresses it has looked up recently and asks for them again. This fixes sites that moved or fail to load after a network change. Nothing else changes and there is nothing to undo.",
            "Flush DNS");
        if (ok)
        {
            await RunChangeAsync(ct => _network.FlushDnsAsync(ct), "Flushing the DNS cache…", "DNS cache cleared");
        }
    }

    [RelayCommand]
    private async Task RenewIpAsync()
    {
        bool ok = await _dialogs.ConfirmAsync("Renew the IP address?",
            "The PC hands its address back to the router and asks for a new one. Your connection drops for a few seconds (downloads and calls are interrupted). Adapters with a fixed address are skipped.",
            "Renew IP");
        if (ok)
        {
            await RunChangeAsync(ct => _network.RenewDhcpAsync(ct), "Renewing the IP address (the connection drops briefly)…", "IP address renewed");
            await RefreshAdaptersQuietlyAsync();
        }
    }

    [RelayCommand]
    private async Task ResetNetworkStackAsync()
    {
        bool ok = await _dialogs.ConfirmAsync("Reset the network stack?",
            "This resets Winsock and TCP/IP to the Windows defaults: the last resort when nothing connects even though other devices work.\n\n"
            + "• Any IP address or DNS server you typed in by hand is cleared and must be entered again.\n"
            + "• VPN and some security software may need to be reinstalled or repaired.\n"
            + "• A restart is required to finish.\n\nA restore point is created first when that is enabled in Settings.",
            "Reset network stack", isDanger: true);
        if (ok)
        {
            await RunChangeAsync(ct => _network.ResetNetworkStackAsync(ct), "Resetting the network stack…", "Network stack reset");
        }
    }

    [RelayCommand]
    private void OpenWifiSettings() => Report(_network.OpenWifiSettings());

    [RelayCommand]
    private void OpenNetworkSettings() => Report(_network.OpenNetworkSettings());

    [RelayCommand]
    private void OpenLocationSettings() => Report(ShellLauncher.Open(LocationSettingsUri));

    [RelayCommand]
    private void GoToLatency() => _navigation.Navigate(typeof(LatencyPage));

    [RelayCommand]
    private void GoToDrivers() => _navigation.Navigate(typeof(DriversPage));

    [RelayCommand]
    private void DismissRestart() => IsRebootRequired = false;

    private void Report(OperationResult result)
    {
        if (!result.Success)
        {
            _notifications.Warning("Could not open Settings", result.Message);
        }
        else if (result.Message.StartsWith("Demo mode", StringComparison.Ordinal))
        {
            _notifications.Info("Demo mode", result.Message);
        }
    }

    /// <summary>Runs a confirmed system change with the busy overlay (not cancellable part-way) and reports the result.</summary>
    private async Task<OperationResult?> RunChangeAsync(Func<CancellationToken, Task<OperationResult>> change, string busyText, string successTitle)
    {
        OperationResult? result = null;
        await RunBusyAsync(async ct =>
        {
            result = await change(ct);
        }, busyText, cancellable: false);

        if (result is null)
        {
            return null;
        }

        if (result.Success)
        {
            _notifications.Success(successTitle, result.Message);
            if (result.RebootRequired)
            {
                IsRebootRequired = true;
            }
        }
        else
        {
            _notifications.Error("That did not work", result.Message);
        }

        return result;
    }

    private async Task RefreshAdaptersQuietlyAsync()
    {
        try
        {
            ApplyAdapters(await _network.GetAdaptersAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh the adapter list.");
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Network page operation failed.");

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsBusy):
            case nameof(IsSpeedTesting):
            case nameof(IsPacketTesting):
                OnPropertyChanged(nameof(IsIdle));
                break;
            case nameof(IsAdvancedMode):
                if (WifiReport is { } report)
                {
                    ApplyWifi(report);
                }

                RebuildTuningRows();
                break;
        }
    }
}
