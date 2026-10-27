using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// Demo tuning for the sample desktop: the Intel AX211 Wi-Fi and I226-V Ethernet driver properties, the tuning
/// actions with a few not yet applied, and four saved Wi-Fi networks. Changes are kept in memory so Apply, Revert and
/// Quick improve behave like the real thing. Pure .NET.
/// </summary>
public sealed class DemoNetworkTuningService : INetworkTuningService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _originals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase) { TuningActionIds.TcpAutoTuning, TuningActionIds.Rss, TuningActionIds.TcpEcnDefault };
    private readonly List<WifiProfileInfo> _profiles =
    [
        new(DemoNetworkService.HomeSsid, DemoNetworkService.WifiAdapterName, true, false, "WPA2-Personal", 1),
        new("Pixel 8 Hotspot", DemoNetworkService.WifiAdapterName, false, true, "WPA3-Personal", 2),
        new("Office-Corp", DemoNetworkService.WifiAdapterName, true, false, "WPA2-Enterprise", 3),
        new("CoffeeHouse Free WiFi", DemoNetworkService.WifiAdapterName, true, false, "Open", 4),
    ];

    private string? _dnsProvider;

    public DemoNetworkTuningService()
    {
        foreach (var p in Properties())
        {
            _values[Key(p.AdapterName, p.RegistryKeyword)] = p.RegistryValue ?? string.Empty;
        }
    }

    public IReadOnlyList<TuningAction> Actions => NetworkTuningCatalog.All;

    /// <summary>DNS servers the demo adapters show (changes when the DNS action is applied).</summary>
    public string[]? DemoDnsServers
    {
        get
        {
            lock (_gate)
            {
                var provider = _dnsProvider is null ? null : NetworkTuningCatalog.DnsProviders.FirstOrDefault(p => p.Id == _dnsProvider);
                return provider is null ? null : [provider.Primary, provider.Secondary];
            }
        }
    }

    public bool IsActionApplied(string actionId)
    {
        lock (_gate)
        {
            return _applied.Contains(actionId);
        }
    }

    /// <summary>True when the demo adapter property currently holds PC Checker's recommendation.</summary>
    public bool IsAdapterSettingRecommended(string adapterName, string keyword)
    {
        var setting = Build().FirstOrDefault(s => s.AdapterName == adapterName && s.Key.Equals(keyword, StringComparison.OrdinalIgnoreCase));
        return setting is { RecommendedValue: not null } && !setting.DiffersFromRecommendation;
    }

    public async Task<IReadOnlyList<AdapterSetting>> GetAdapterSettingsAsync(string? adapterName, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        return Build().Where(s => adapterName is null || s.AdapterName.Equals(adapterName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<OperationResult> SetAdapterSettingAsync(string adapterName, string key, string value, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 600).ConfigureAwait(false);
        var setting = Build().FirstOrDefault(s => s.AdapterName.Equals(adapterName, StringComparison.OrdinalIgnoreCase) && s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (setting is null || !AdapterSettingAdvisor.IsAllowed(setting, value))
        {
            return OperationResult.Fail($"\"{value}\" is not an allowed value for {key}.");
        }

        lock (_gate)
        {
            _originals.TryAdd(Key(adapterName, key), setting.CurrentValue ?? string.Empty);
            _values[Key(adapterName, key)] = value;
        }

        return OperationResult.Ok($"Demo mode: {setting.DisplayName} is now {setting.LabelFor(value)} (the adapter would restart briefly).");
    }

    public async Task<OperationResult> RevertAdapterSettingAsync(string adapterName, string key, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_originals.Remove(Key(adapterName, key), out var original))
            {
                return OperationResult.Fail("PC Checker has not changed this setting, so there is no previous value to put back.");
            }

            _values[Key(adapterName, key)] = original;
        }

        return OperationResult.Ok("Demo mode: the previous value is back.");
    }

    public bool CanRevertAdapterSetting(string adapterName, string key)
    {
        lock (_gate)
        {
            return _originals.ContainsKey(Key(adapterName, key));
        }
    }

    public async Task<ActionState> GetStateAsync(string actionId, CancellationToken cancellationToken)
    {
        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        var action = NetworkTuningCatalog.Find(actionId);
        if (action is null)
        {
            return new ActionState(actionId, null, "Unknown action");
        }

        lock (_gate)
        {
            bool applied = _applied.Contains(action.Id);
            return action.Id switch
            {
                TuningActionIds.WifiPowerSave => new(actionId, applied, applied ? "Plugged in: Maximum Performance; on battery: Maximum Performance" : "Plugged in: Maximum Performance; on battery: Medium Power Saving"),
                TuningActionIds.WifiAllowTurnOff => new(actionId, applied, applied ? "Windows may not turn the adapter off" : "Windows may turn the adapter off to save power"),
                TuningActionIds.TcpAutoTuning => new(actionId, applied, applied ? "Normal" : "Disabled"),
                TuningActionIds.TcpEcnDefault => new(actionId, applied, "Disabled (default: Disabled)"),
                TuningActionIds.Rss => new(actionId, applied, applied ? "Enabled" : "Disabled"),
                TuningActionIds.DnsFast => new(actionId, _dnsProvider is not null,
                    _dnsProvider is null ? "Wi-Fi: 192.168.1.1 (automatic)" : NetworkTuningCatalog.DnsProviders.First(p => p.Id == _dnsProvider).Name),
                TuningActionIds.DnsOverHttps => _dnsProvider is null
                    ? new(actionId, null, "Choose a public DNS provider first")
                    : new(actionId, applied, applied ? "Encrypted (automatic upgrade on)" : "Not encrypted"),
                TuningActionIds.PreferIpv4 => new(actionId, applied, applied ? "IPv4 preferred" : "IPv6 preferred (Windows default)"),
                _ => new(actionId, null, "Unknown action"),
            };
        }
    }

    public async Task<OperationResult> ApplyAsync(string actionId, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 250, 500).ConfigureAwait(false);
        var action = NetworkTuningCatalog.Find(actionId);
        if (action is null)
        {
            return OperationResult.Fail($"Unknown action '{actionId}'.");
        }

        lock (_gate)
        {
            if (action.Id == TuningActionIds.DnsFast)
            {
                _dnsProvider = (NetworkTuningCatalog.DnsProviderFromActionId(actionId) ?? NetworkTuningCatalog.DefaultDnsProvider).Id;
            }
            else if (action.Id == TuningActionIds.DnsOverHttps && _dnsProvider is null)
            {
                return OperationResult.Fail("Your DNS servers do not offer a known DNS-over-HTTPS service. Use \"Use a fast public DNS server\" first.");
            }
            else
            {
                _applied.Add(action.Id);
            }
        }

        return OperationResult.Ok($"Demo mode: {action.Title}: done." + (action.RequiresRestart ? " Restart the PC for it to take effect." : string.Empty), action.RequiresRestart);
    }

    public async Task<OperationResult> RevertAsync(string actionId, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 250, 500).ConfigureAwait(false);
        var action = NetworkTuningCatalog.Find(actionId);
        if (action is null)
        {
            return OperationResult.Fail($"Unknown action '{actionId}'.");
        }

        lock (_gate)
        {
            if (action.Id == TuningActionIds.DnsFast)
            {
                _dnsProvider = null;
                _applied.Remove(TuningActionIds.DnsOverHttps);
            }
            else
            {
                _applied.Remove(action.Id);
            }
        }

        return OperationResult.Ok($"Demo mode: {action.Title}: reverted.", action.RequiresRestart);
    }

    public async Task<IReadOnlyList<WifiProfileInfo>> GetSavedNetworksAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 150, 300).ConfigureAwait(false);
        lock (_gate)
        {
            return _profiles.OrderBy(p => p.Priority).ToList();
        }
    }

    public async Task<OperationResult> SetNetworkPriorityAsync(string profileName, int priority, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 150, 300).ConfigureAwait(false);
        lock (_gate)
        {
            var profile = _profiles.FirstOrDefault(p => p.Name == profileName);
            if (profile is null)
            {
                return OperationResult.Fail($"No saved Wi-Fi network called \"{profileName}\" was found.");
            }

            var ordered = _profiles.OrderBy(p => p.Priority).Where(p => p.Name != profileName).ToList();
            ordered.Insert(Math.Clamp(priority - 1, 0, ordered.Count), profile);
            _profiles.Clear();
            _profiles.AddRange(ordered.Select((p, i) => p with { Priority = i + 1 }));
        }

        return OperationResult.Ok($"Demo mode: \"{profileName}\" is now number {priority} in the list Windows tries.");
    }

    public async Task<OperationResult> ForgetNetworkAsync(string profileName, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        lock (_gate)
        {
            if (_profiles.RemoveAll(p => p.Name == profileName) == 0)
            {
                return OperationResult.Fail($"No saved Wi-Fi network called \"{profileName}\" was found.");
            }

            var ordered = _profiles.OrderBy(p => p.Priority).Select((p, i) => p with { Priority = i + 1 }).ToList();
            _profiles.Clear();
            _profiles.AddRange(ordered);
        }

        return OperationResult.Ok($"Demo mode: \"{profileName}\" was forgotten (a backup would be saved first).");
    }

    public Task<OperationResult> SetAutoConnectAsync(string profileName, bool enabled, CancellationToken cancellationToken) =>
        UpdateProfileAsync(profileName, p => p with { AutoConnect = enabled }, enabled ? "connects automatically" : "connects only when you choose it", cancellationToken);

    public Task<OperationResult> SetMeteredAsync(string profileName, bool metered, CancellationToken cancellationToken) =>
        UpdateProfileAsync(profileName, p => p with { Metered = metered }, metered ? "is metered" : "is no longer metered", cancellationToken);

    private async Task<OperationResult> UpdateProfileAsync(string profileName, Func<WifiProfileInfo, WifiProfileInfo> change, string text, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 150, 300).ConfigureAwait(false);
        lock (_gate)
        {
            int index = _profiles.FindIndex(p => p.Name == profileName);
            if (index < 0)
            {
                return OperationResult.Fail($"No saved Wi-Fi network called \"{profileName}\" was found.");
            }

            _profiles[index] = change(_profiles[index]);
        }

        return OperationResult.Ok($"Demo mode: \"{profileName}\" {text}.");
    }

    private IReadOnlyList<AdapterSetting> Build()
    {
        var wifi = new AdvisorContext(IsWifi: true, IsLaptop: false, SameSsidOn5GhzVisible: true, SameSsidOn6GhzVisible: false, Band24Congested: true, AccessPointsForSsid: 1);
        var wired = wifi with { IsWifi = false };
        lock (_gate)
        {
            return Properties()
                .Select(p => p with { RegistryValue = _values.GetValueOrDefault(Key(p.AdapterName, p.RegistryKeyword), p.RegistryValue ?? string.Empty) })
                .Select(p => AdapterSettingAdvisor.Advise(p, p.AdapterName == DemoNetworkService.WifiAdapterName ? wifi : wired))
                .ToList();
        }
    }

    private static string Key(string adapter, string keyword) => adapter + "|" + keyword;

    private static IEnumerable<AdapterAdvancedProperty> Properties()
    {
        const string w = DemoNetworkService.WifiAdapterName;
        const string e = DemoNetworkService.EthernetAdapterName;
        yield return P(w, "Preferred Band", "RoamingPreferredBandType", "0", ["1. No Preference", "2. Prefer 2.4GHz band", "3. Prefer 5GHz band", "4. Prefer 6GHz band"], ["0", "1", "2", "3"]);
        yield return P(w, "Roaming Aggressiveness", "RoamAggressiveness", "3", ["1. Lowest", "2. Medium-low", "3. Medium", "4. Medium-High", "5. Highest"], ["1", "2", "3", "4", "5"]);
        yield return P(w, "MIMO Power Save Mode", "MIMOPowerSaveMode", "0", ["Auto SMPS", "No SMPS"], ["0", "3"]);
        yield return P(w, "Transmit Power", "IbssTxPower", "100", ["1. Lowest", "2. Medium-low", "3. Medium", "4. Medium-High", "5. Highest"], ["0", "25", "50", "75", "100"]);
        yield return P(w, "802.11n/ac/ax Wireless Mode", "IEEE11nMode", "4", ["802.11a/b/g", "802.11n", "802.11ac", "802.11ax"], ["0", "1", "2", "4"]);
        yield return P(w, "Channel Width for 2.4GHz", "ChannelWidth24", "1", ["20 MHz Only", "Auto"], ["0", "1"]);
        yield return P(w, "Channel Width for 5GHz", "ChannelWidth52", "1", ["20 MHz Only", "Auto"], ["0", "1"]);
        yield return P(w, "Packet Coalescing", "*PacketCoalescing", "1", ["Disabled", "Enabled"], ["0", "1"]);
        yield return P(w, "ARP offload for WoWLAN", "*PMARPOffload", "1", ["Disabled", "Enabled"], ["0", "1"]);
        yield return P(w, "Throughput Booster", "ThroughputBoosterEnabled", "0", ["Disabled", "Enabled"], ["0", "1"]);
        yield return P(e, "Energy Efficient Ethernet", "*EEE", "1", ["Off", "On"], ["0", "1"]);
        yield return P(e, "Interrupt Moderation", "*InterruptModeration", "1", ["Disabled", "Enabled"], ["0", "1"]);
        yield return P(e, "Flow Control", "*FlowControl", "3", ["Disabled", "Tx Enabled", "Rx Enabled", "Rx & Tx Enabled"], ["0", "1", "2", "3"]);
        yield return P(e, "Speed & Duplex", "*SpeedDuplex", "0", ["Auto Negotiation", "10 Mbps Full Duplex", "100 Mbps Full Duplex", "1.0 Gbps Full Duplex", "2.5 Gbps Full Duplex"], ["0", "2", "4", "6", "7"]);
        yield return P(e, "Wait for Link", "WaitAutoNegComplete", "2", ["Off", "On", "Auto Detect"], ["0", "1", "2"]);
    }

    private static AdapterAdvancedProperty P(string adapter, string display, string keyword, string value, string[] labels, string[] values) =>
        new(adapter, display, labels[Array.IndexOf(values, value)], keyword, value, labels, values);
}
