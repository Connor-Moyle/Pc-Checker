using Microsoft.Extensions.Logging;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Latency;
using PcChecker.Core.Optimization;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Network.Tuning;

public sealed partial class WindowsNetworkTuningService
{
    private const string Tcpip6ParametersKey = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";
    private const string TcpipInterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    public async Task<ActionState> GetStateAsync(string actionId, CancellationToken cancellationToken)
    {
        var action = NetworkTuningCatalog.Find(actionId);
        if (action is null)
        {
            return new ActionState(actionId, null, "Unknown action");
        }

        if (!PlatformGuard.IsWindows)
        {
            return new ActionState(actionId, null, "Windows only");
        }

        try
        {
            switch (action.Id)
            {
                case TuningActionIds.WifiPowerSave:
                {
                    var (ac, dc) = await LatencyProbes.ReadPowerSettingAsync(NetworkTuningCatalog.WirelessSubgroup, NetworkTuningCatalog.WirelessPowerSavingSetting, cancellationToken).ConfigureAwait(false);
                    return new ActionState(actionId, ac is null ? null : ac == 0 && dc == 0,
                        ac is null ? "Unknown" : $"Plugged in: {NetworkTuningCatalog.DescribeWifiPowerIndex(ac)}; on battery: {NetworkTuningCatalog.DescribeWifiPowerIndex(dc)}");
                }

                case TuningActionIds.WifiAllowTurnOff:
                {
                    var keys = WifiClassKeys();
                    if (keys.Count == 0)
                    {
                        return new ActionState(actionId, null, "No Wi-Fi adapter found");
                    }

                    bool all = keys.All(k => LatencyProbes.ReadDword(RegistryHive.LocalMachine, k, "PnPCapabilities") is { } v && (v & NetworkTuningCatalog.PnpCapabilitiesNoPowerOff) == NetworkTuningCatalog.PnpCapabilitiesNoPowerOff);
                    return new ActionState(actionId, all, all ? "Windows may not turn the adapter off" : "Windows may turn the adapter off to save power");
                }

                case TuningActionIds.TcpAutoTuning:
                {
                    var level = await PowerShellValueAsync("(Get-NetTCPSetting -SettingName Internet -ErrorAction SilentlyContinue).AutoTuningLevelLocal", cancellationToken).ConfigureAwait(false);
                    return new ActionState(actionId, level is null ? null : level.Equals("Normal", StringComparison.OrdinalIgnoreCase), level ?? "Unknown");
                }

                case TuningActionIds.TcpEcnDefault:
                {
                    var ecn = await PowerShellValueAsync("(Get-NetTCPSetting -SettingName Internet -ErrorAction SilentlyContinue).EcnCapability", cancellationToken).ConfigureAwait(false);
                    var known = Captured(NetworkTuningCaptureStore.EcnDefaultEntryId)?.GetValueOrDefault("value");
                    bool? applied = ecn is null || known is null ? null : ecn.Equals(known, StringComparison.OrdinalIgnoreCase);
                    return new ActionState(actionId, applied, ecn is null ? "Unknown" : known is null ? $"{ecn} (Windows default not known yet)" : $"{ecn} (default: {known})");
                }

                case TuningActionIds.Rss:
                {
                    var rss = await PowerShellValueAsync("(Get-NetOffloadGlobalSetting -ErrorAction SilentlyContinue).ReceiveSideScaling", cancellationToken).ConfigureAwait(false);
                    return new ActionState(actionId, rss is null ? null : rss.Equals("Enabled", StringComparison.OrdinalIgnoreCase), rss ?? "Unknown");
                }

                case TuningActionIds.DnsFast:
                {
                    var adapters = ActiveInterfaces();
                    if (adapters.Count == 0)
                    {
                        return new ActionState(actionId, null, "No connected adapter");
                    }

                    var providers = adapters.Select(a => NetworkTuningCatalog.MatchDnsProvider(a.Dns)).ToList();
                    bool all = providers.All(p => p is not null);
                    string text = all
                        ? string.Join(", ", providers.Select(p => p!.Name).Distinct())
                        : string.Join("; ", adapters.Select(a => $"{a.Name}: {(a.Dns.Count == 0 ? "none" : string.Join(", ", a.Dns))}{(IsAutomaticDns(a.Id) ? " (automatic)" : string.Empty)}"));
                    return new ActionState(actionId, all, text);
                }

                case TuningActionIds.DnsOverHttps:
                {
                    if (!NetworkSystemInfo.IsWindows11OrNewer())
                    {
                        return new ActionState(actionId, null, "Needs Windows 11");
                    }

                    var servers = DohCapableServers();
                    if (servers.Count == 0)
                    {
                        return new ActionState(actionId, null, "Choose a public DNS provider first");
                    }

                    return await ReadDohStateAsync(actionId, servers, cancellationToken).ConfigureAwait(false);
                }

                case TuningActionIds.PreferIpv4:
                {
                    int? value = LatencyProbes.ReadDword(RegistryHive.LocalMachine, Tcpip6ParametersKey, "DisabledComponents");
                    bool prefers = NetworkTuningCatalog.PrefersIpv4(value);
                    return new ActionState(actionId, prefers, prefers ? "IPv4 preferred" : "IPv6 preferred (Windows default)");
                }

                default:
                    return new ActionState(actionId, null, "Unknown action");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the state of {Action}.", actionId);
            return new ActionState(actionId, null, "Could not read: " + ex.Message);
        }
    }

    public Task<OperationResult> ApplyAsync(string actionId, CancellationToken cancellationToken) => RunActionAsync(actionId, apply: true, cancellationToken);

    public Task<OperationResult> RevertAsync(string actionId, CancellationToken cancellationToken) => RunActionAsync(actionId, apply: false, cancellationToken);

    private async Task<OperationResult> RunActionAsync(string actionId, bool apply, CancellationToken cancellationToken)
    {
        var action = NetworkTuningCatalog.Find(actionId);
        if (action is null)
        {
            return OperationResult.Fail($"The Network module does not know the action '{actionId}'.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("This change is only possible on Windows.");
        }

        if (action.Id == TuningActionIds.DnsOverHttps && !NetworkSystemInfo.IsWindows11OrNewer())
        {
            return OperationResult.Fail("DNS over HTTPS settings need Windows 11.");
        }

        var provider = NetworkTuningCatalog.DnsProviderFromActionId(actionId) ?? NetworkTuningCatalog.DefaultDnsProvider;
        IReadOnlyDictionary<string, string?>? previous;
        string? before;
        bool firstCapture = false;
        if (apply)
        {
            // Like the adapter-setting path: a failed first apply that changed nothing must not leave a capture
            // behind, or a later Apply would keep these stale values and Revert would restore them over a hand-made
            // change. One that changed part of the settings keeps it, so Revert can undo that part.
            firstCapture = Captured(action.Id) is null;
            previous = Capture(action.Id, await ReadCurrentAsync(action.Id, cancellationToken).ConfigureAwait(false));
            before = previous.Count == 0 ? null : NetworkTuningCaptureStore.Describe(previous);
        }
        else
        {
            previous = Captured(action.Id);
            before = null;
        }

        string title = action.Id == TuningActionIds.DnsFast && apply ? $"{action.Title} ({provider.Name})" : action.Title;
        string description = (apply ? string.Empty : "Undo: ") + title;
        string undo = "Use Revert on the Network page." + (before is null ? string.Empty : $" Previous value(s): {before}");
        // Safe tweaks are captured and reverted by PC Checker itself; Moderate and Advanced ones also get a restore point when Settings ask for one.
        var options = new ChangeOptions(action.Risk == RiskLevel.Safe ? false : null, null, Module, action.Id, true, undo);
        var result = await _guard.RunGuardedAsync(description, options, ct => ExecuteAsync(action, apply, provider, previous, ct), null, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            if (apply)
            {
                await RecordValuesAsync(description, action.Id, before, apply ? "Applied" : "Reverted", result).ConfigureAwait(false);
            }
            else
            {
                Forget(action.Id);
            }
        }
        else if (apply && firstCapture && previous is not null && await NothingChangedAsync(action.Id, previous).ConfigureAwait(false))
        {
            // Only when the failed Apply changed nothing: if part of it went through, the capture is what Revert
            // needs to put that part back.
            Forget(action.Id);
        }

        return result;
    }

    /// <summary>True when the values an action changes still read exactly as captured; false when unsure.</summary>
    private async Task<bool> NothingChangedAsync(string actionId, IReadOnlyDictionary<string, string?> captured)
    {
        try
        {
            var now = await ReadCurrentAsync(actionId, CancellationToken.None).ConfigureAwait(false);
            return NetworkTuningCaptureStore.Unchanged(captured, now);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not re-read {Action} after a failed change; keeping its capture.", actionId);
            return false;
        }
    }

    private async Task<OperationResult> ExecuteAsync(TuningAction action, bool apply, DnsProvider provider, IReadOnlyDictionary<string, string?>? previous, CancellationToken ct)
    {
        var result = action.Id switch
        {
            TuningActionIds.WifiPowerSave => apply
                ? await LatencyProbes.SetPowerSettingAsync(NetworkTuningCatalog.WirelessSubgroup, NetworkTuningCatalog.WirelessPowerSavingSetting, 0, ct).ConfigureAwait(false)
                : await LatencyProbes.SetPowerSettingAsync(NetworkTuningCatalog.WirelessSubgroup, NetworkTuningCatalog.WirelessPowerSavingSetting,
                    ParseInt(previous, "AC") ?? 0, ParseInt(previous, "DC") ?? 2, ct).ConfigureAwait(false),
            TuningActionIds.WifiAllowTurnOff => SetWifiPowerOff(apply, previous),
            TuningActionIds.TcpAutoTuning => await NetshAsync($"int tcp set global autotuninglevel={(apply ? "normal" : NetworkTuningCatalog.AutoTuningToNetsh(Value(previous, "level")) ?? "normal")}", ct).ConfigureAwait(false),
            TuningActionIds.TcpEcnDefault => apply ? await ApplyEcnDefaultAsync(ct).ConfigureAwait(false) : await RevertToggleAsync("ecncapability", Value(previous, "ecn"), ct).ConfigureAwait(false),
            TuningActionIds.Rss => apply ? await NetshAsync("int tcp set global rss=enabled", ct).ConfigureAwait(false) : await RevertToggleAsync("rss", Value(previous, "rss"), ct).ConfigureAwait(false),
            TuningActionIds.DnsFast => apply ? await SetDnsAsync(provider, ct).ConfigureAwait(false) : await RestoreDnsAsync(previous, ct).ConfigureAwait(false),
            TuningActionIds.DnsOverHttps => apply ? await EnableDohAsync(ct).ConfigureAwait(false) : await RestoreDohAsync(previous, ct).ConfigureAwait(false),
            TuningActionIds.PreferIpv4 => SetPreferIpv4(apply, previous),
            _ => OperationResult.Fail("Unknown action."),
        };

        if (!result.Success)
        {
            return result;
        }

        string message = apply ? $"{action.Title}: done." : $"{action.Title}: reverted.";
        if (action.RequiresRestart)
        {
            message += " Restart the PC for it to take effect.";
        }
        else if (action.RequiresReconnect)
        {
            message += " The connection may have paused for a moment.";
        }

        return OperationResult.Ok(message, action.RequiresRestart);
    }

    /// <summary>Reads the values an action is about to change.</summary>
    private async Task<IReadOnlyDictionary<string, string?>> ReadCurrentAsync(string actionId, CancellationToken ct)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        switch (actionId)
        {
            case TuningActionIds.WifiPowerSave:
            {
                var (ac, dc) = await LatencyProbes.ReadPowerSettingAsync(NetworkTuningCatalog.WirelessSubgroup, NetworkTuningCatalog.WirelessPowerSavingSetting, ct).ConfigureAwait(false);
                map["AC"] = ac?.ToString();
                map["DC"] = dc?.ToString();
                break;
            }

            case TuningActionIds.WifiAllowTurnOff:
                foreach (var key in WifiClassKeys())
                {
                    map[$"{key}|PnPCapabilities"] = LatencyProbes.ReadDword(RegistryHive.LocalMachine, key, "PnPCapabilities")?.ToString();
                }

                break;

            case TuningActionIds.TcpAutoTuning:
                map["level"] = await PowerShellValueAsync("(Get-NetTCPSetting -SettingName Internet -ErrorAction SilentlyContinue).AutoTuningLevelLocal", ct).ConfigureAwait(false);
                break;

            case TuningActionIds.TcpEcnDefault:
                map["ecn"] = await PowerShellValueAsync("(Get-NetTCPSetting -SettingName Internet -ErrorAction SilentlyContinue).EcnCapability", ct).ConfigureAwait(false);
                break;

            case TuningActionIds.Rss:
                map["rss"] = await PowerShellValueAsync("(Get-NetOffloadGlobalSetting -ErrorAction SilentlyContinue).ReceiveSideScaling", ct).ConfigureAwait(false);
                break;

            case TuningActionIds.DnsFast:
                foreach (var adapter in ActiveInterfaces())
                {
                    // An empty NameServer means "automatic" (from DHCP); a value is a list the user typed in.
                    map[$"{adapter.Id}|NameServer"] = LatencyProbes.ReadString(RegistryHive.LocalMachine, $@"{TcpipInterfacesKey}\{adapter.Id}", "NameServer") ?? string.Empty;
                }

                break;

            case TuningActionIds.DnsOverHttps:
                await CaptureDohAsync(map, ct).ConfigureAwait(false);
                break;

            case TuningActionIds.PreferIpv4:
                map["DisabledComponents"] = LatencyProbes.ReadDword(RegistryHive.LocalMachine, Tcpip6ParametersKey, "DisabledComponents")?.ToString();
                break;
        }

        return map;
    }

    private static OperationResult SetWifiPowerOff(bool apply, IReadOnlyDictionary<string, string?>? previous)
    {
        var keys = WifiClassKeys();
        if (keys.Count == 0)
        {
            return OperationResult.Fail("No Wi-Fi adapter was found in the registry.");
        }

        // Remember what each key held before this run so a failure part-way through can put the
        // keys already written back, instead of leaving the adapters in a mixed state.
        var written = new List<(string Key, int? Old)>();
        foreach (var key in keys)
        {
            int? original = LatencyProbes.ReadDword(RegistryHive.LocalMachine, key, "PnPCapabilities");
            OperationResult result;
            if (apply)
            {
                int current = LatencyProbes.ReadDword(RegistryHive.LocalMachine, key, "PnPCapabilities") ?? 0;
                result = LatencyProbes.WriteDword(RegistryHive.LocalMachine, key, "PnPCapabilities", current | NetworkTuningCatalog.PnpCapabilitiesNoPowerOff);
            }
            else
            {
                string capture = $"{key}|PnPCapabilities";
                // Nothing captured or "was absent": the Windows default is no value at all.
                result = previous is not null && previous.TryGetValue(capture, out var old) && int.TryParse(old, out int value)
                    ? LatencyProbes.WriteDword(RegistryHive.LocalMachine, key, "PnPCapabilities", value)
                    : LatencyProbes.DeleteValue(RegistryHive.LocalMachine, key, "PnPCapabilities");
            }

            if (!result.Success)
            {
                bool rolledBack = true;
                foreach (var (doneKey, doneOld) in written)
                {
                    var undo = doneOld is { } v
                        ? LatencyProbes.WriteDword(RegistryHive.LocalMachine, doneKey, "PnPCapabilities", v)
                        : LatencyProbes.DeleteValue(RegistryHive.LocalMachine, doneKey, "PnPCapabilities");
                    rolledBack &= undo.Success;
                }

                return written.Count == 0
                    ? result
                    : OperationResult.Fail(result.Message + (rolledBack
                        ? " The adapters already changed were put back as they were."
                        : " Some adapters were already changed and could not all be put back; use Revert on the Network page."));
            }

            written.Add((key, original));
        }

        return OperationResult.Ok($"Updated {keys.Count} Wi-Fi adapter(s).");
    }

    private static OperationResult SetPreferIpv4(bool apply, IReadOnlyDictionary<string, string?>? previous)
    {
        int? current = LatencyProbes.ReadDword(RegistryHive.LocalMachine, Tcpip6ParametersKey, "DisabledComponents");
        if (apply)
        {
            return LatencyProbes.WriteDword(RegistryHive.LocalMachine, Tcpip6ParametersKey, "DisabledComponents", NetworkTuningCatalog.WithPreferIpv4(current));
        }

        if (previous is not null && previous.ContainsKey("DisabledComponents"))
        {
            return int.TryParse(previous["DisabledComponents"], out int old)
                ? LatencyProbes.WriteDword(RegistryHive.LocalMachine, Tcpip6ParametersKey, "DisabledComponents", old)
                : LatencyProbes.DeleteValue(RegistryHive.LocalMachine, Tcpip6ParametersKey, "DisabledComponents");
        }

        // Nothing captured: clear only the prefer-IPv4 bit and keep anything else that was set.
        int cleared = (current ?? 0) & ~0x20;
        return cleared == 0
            ? LatencyProbes.DeleteValue(RegistryHive.LocalMachine, Tcpip6ParametersKey, "DisabledComponents")
            : LatencyProbes.WriteDword(RegistryHive.LocalMachine, Tcpip6ParametersKey, "DisabledComponents", cleared);
    }

    private async Task<OperationResult> ApplyEcnDefaultAsync(CancellationToken ct)
    {
        var result = await NetshAsync("int tcp set global ecncapability=default", ct).ConfigureAwait(false);
        if (result.Success)
        {
            // Remember what "default" means on this Windows build so the state can be shown later.
            var now = await PowerShellValueAsync("(Get-NetTCPSetting -SettingName Internet -ErrorAction SilentlyContinue).EcnCapability", ct).ConfigureAwait(false);
            if (now is not null)
            {
                Remember(NetworkTuningCaptureStore.EcnDefaultEntryId, new Dictionary<string, string?> { ["value"] = now });
            }
        }

        return result;
    }

    private static async Task<OperationResult> RevertToggleAsync(string parameter, string? capturedValue, CancellationToken ct)
    {
        var value = NetworkTuningCatalog.EnabledToNetsh(capturedValue);
        return value is null
            ? OperationResult.Ok("Nothing was recorded before the change, so the Windows default stays in place.")
            : await NetshAsync($"int tcp set global {parameter}={value}", ct).ConfigureAwait(false);
    }

    private static async Task<OperationResult> SetDnsAsync(DnsProvider provider, CancellationToken ct)
    {
        var adapters = ActiveInterfaces();
        if (adapters.Count == 0)
        {
            return OperationResult.Fail("No connected network adapter was found.");
        }

        var commands = adapters.Select(a => $"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ServerAddresses @({PowerShellRunner.Quote(provider.Primary)},{PowerShellRunner.Quote(provider.Secondary)}) -ErrorAction Stop");
        var run = await PowerShellRunner.RunAsync(string.Join("; ", commands) + "; Clear-DnsClientCache", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return run.Success
            ? OperationResult.Ok($"{string.Join(", ", adapters.Select(a => a.Name))} now use{(adapters.Count == 1 ? "s" : string.Empty)} {provider.Name} ({provider.Primary}, {provider.Secondary}).")
            : OperationResult.Fail("Windows refused to change the DNS servers: " + FirstLine(run.StdErr, run.StdOut));
    }

    private static async Task<OperationResult> RestoreDnsAsync(IReadOnlyDictionary<string, string?>? previous, CancellationToken ct)
    {
        var commands = new List<string>();
        var active = ActiveInterfaces(includeAll: true);
        var targets = previous is { Count: > 0 }
            ? previous.Where(p => p.Key.EndsWith("|NameServer", StringComparison.OrdinalIgnoreCase)).Select(p => (Id: p.Key[..p.Key.IndexOf('|')], Servers: p.Value ?? string.Empty)).ToList()
            : active.Where(a => NetworkTuningCatalog.MatchDnsProvider(a.Dns) is not null).Select(a => (a.Id, Servers: string.Empty)).ToList();
        foreach (var (id, servers) in targets)
        {
            var adapter = active.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (adapter.Id is null)
            {
                continue; // The adapter is gone; nothing to restore.
            }

            var list = servers.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            commands.Add(list.Length == 0
                ? $"Set-DnsClientServerAddress -InterfaceIndex {adapter.Index} -ResetServerAddresses -ErrorAction Stop"
                : $"Set-DnsClientServerAddress -InterfaceIndex {adapter.Index} -ServerAddresses @({string.Join(",", list.Select(PowerShellRunner.Quote))}) -ErrorAction Stop");
        }

        if (commands.Count == 0)
        {
            return OperationResult.Ok("There was nothing to restore: the adapters PC Checker changed are no longer present.");
        }

        var run = await PowerShellRunner.RunAsync(string.Join("; ", commands) + "; Clear-DnsClientCache", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return run.Success ? OperationResult.Ok("The previous DNS servers are back.") : OperationResult.Fail("Windows refused to restore the DNS servers: " + FirstLine(run.StdErr, run.StdOut));
    }

    /// <summary>Connected adapters with an IPv4 gateway (the ones that reach the internet).</summary>
    private static IReadOnlyList<(string Id, int Index, string Name, IReadOnlyList<string> Dns)> ActiveInterfaces(bool includeAll = false)
    {
        var list = new List<(string, int, string, IReadOnlyList<string>)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                if (!includeAll && (nic.OperationalStatus != OperationalStatus.Up || !hasGateway))
                {
                    continue;
                }

                int index;
                try
                {
                    index = props.GetIPv4Properties()?.Index ?? -1;
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                if (index < 0)
                {
                    continue;
                }

                var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                list.Add((nic.Id, index, nic.Name, dns));
            }
        }
        catch
        {
            // No network information available.
        }

        return list;
    }

    private static bool IsAutomaticDns(string interfaceId) =>
        string.IsNullOrWhiteSpace(LatencyProbes.ReadString(RegistryHive.LocalMachine, $@"{TcpipInterfacesKey}\{interfaceId}", "NameServer"));

    /// <summary>Driver class keys of the Wi-Fi adapters (matched by interface GUID).</summary>
    private static IReadOnlyList<string> WifiClassKeys()
    {
        var ids = NetworkSystemInfo.WirelessInterfaceIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return [];
        }

        var keys = new List<string>();
        foreach (var index in LatencyProbes.SubKeyNames(RegistryHive.LocalMachine, LatencyProbes.NetworkClassKey))
        {
            if (index.Length != 4 || !index.All(char.IsDigit))
            {
                continue;
            }

            var key = $@"{LatencyProbes.NetworkClassKey}\{index}";
            var instance = LatencyProbes.ReadString(RegistryHive.LocalMachine, key, "NetCfgInstanceId");
            if (instance is not null && ids.Contains(instance))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static async Task<OperationResult> NetshAsync(string arguments, CancellationToken ct)
    {
        var run = await LatencyProbes.RunAsync("netsh.exe", arguments, ct, 60).ConfigureAwait(false);
        return run.ExitCode == 0 ? OperationResult.Ok($"netsh {arguments}") : OperationResult.Fail($"netsh {arguments} failed: {Describe(run)}");
    }

    private static async Task<string?> PowerShellValueAsync(string expression, CancellationToken ct)
    {
        var run = await PowerShellRunner.RunAsync(expression, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var text = run.StdOut.Trim();
        return run.Success && text.Length > 0 ? text.Split('\n')[0].Trim() : null;
    }

    private static string? Value(IReadOnlyDictionary<string, string?>? previous, string key) =>
        previous is not null && previous.TryGetValue(key, out var value) ? value : null;

    private static int? ParseInt(IReadOnlyDictionary<string, string?>? previous, string key) =>
        int.TryParse(Value(previous, key), out int v) ? v : null;
}
