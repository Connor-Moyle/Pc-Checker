using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Latency;
using PcChecker.Core.Network.Tuning;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Network.Resolvers;

/// <summary>
/// Windows implementation of <see cref="IDnsService"/>. Servers come from the adapters' IP properties and the Tcpip/Tcpip6
/// registry values (typed in by hand or handed out by the router); switching uses the DnsClient cmdlets the Network page
/// already uses (<see cref="DnsCommands"/>), DNS over HTTPS the same template and per-adapter switch Windows 11's
/// Settings app writes. Before every change the replaced values are saved to <c>dns-switch.json</c> so Undo works
/// after a restart, and the change runs through <see cref="IChangeGuard"/>.
/// </summary>
public sealed class WindowsDnsService : IDnsService
{
    private const string Module = "Network";
    private const string TcpipInterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string Tcpip6InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsDnsService> _logger;
    private readonly string? _capturePath;
    private readonly object _gate = new();
    private DnsPreviousSettings? _last;
    private bool _loaded;

    public WindowsDnsService(IChangeGuard guard, ISettingsService settings, ILogger<WindowsDnsService> logger)
    {
        _guard = guard;
        _logger = logger;
        try
        {
            _capturePath = Path.Combine(settings.SettingsDirectory, DnsSettingsCapture.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DNS captures will not be saved: the settings directory is unknown.");
        }
    }

    public DnsPreviousSettings? LastChange
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _last = _capturePath is null ? null : DnsSettingsCapture.TryRead(_capturePath);
                }

                return _last;
            }
        }
    }

    public Task<DnsStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        if (!PlatformGuard.IsWindows)
        {
            return DnsStatus.Empty;
        }

        bool win11 = NetworkSystemInfo.IsWindows11OrNewer();
        var doh = win11 ? await ReadDohServersAsync(cancellationToken).ConfigureAwait(false) : [];
        bool globalAuto = win11 && LatencyProbes.ReadDword(RegistryHive.LocalMachine, DohInterfaceSettings.DnscacheParametersKey, DohInterfaceSettings.EnableAutoDohValue) == DohInterfaceSettings.EnableAutoDohOn;
        var adapters = new List<DnsAdapterInfo>();
        foreach (var nic in ReadInterfaces(activeOnly: true))
        {
            string manual4 = LatencyProbes.ReadString(RegistryHive.LocalMachine, $@"{TcpipInterfacesKey}\{nic.Id}", "NameServer") ?? string.Empty;
            string? manual6 = LatencyProbes.ReadString(RegistryHive.LocalMachine, $@"{Tcpip6InterfacesKey}\{nic.Id}", "NameServer") ?? string.Empty;
            var router = DnsCommands.Split(LatencyProbes.ReadString(RegistryHive.LocalMachine, $@"{TcpipInterfacesKey}\{nic.Id}", "DhcpNameServer"));
            var encryption = DnsEncryptionState.NotSupported;
            if (win11)
            {
                var states = nic.Servers4.Concat(nic.Servers6)
                    .Select(s => DnsSwitchPlan.IsServerEncrypted(
                        WindowsNetworkTuningService.ReadQword(DohInterfaceSettings.KeyFor(nic.Id, s), DohInterfaceSettings.FlagsValue),
                        globalAuto,
                        doh.TryGetValue(s, out var entry) && entry.AutoUpgrade))
                    .ToList();
                encryption = DnsSwitchPlan.Combine(states);
            }

            adapters.Add(new DnsAdapterInfo(nic.Id, nic.Index, nic.Name, nic.Description, nic.IsWifi, nic.Servers4, nic.Servers6, manual4, manual6, router, nic.Gateway, encryption));
        }

        return new DnsStatus(adapters, win11, DateTimeOffset.Now);
    }, cancellationToken);

    public Task<DnsBenchmarkReport> BenchmarkAsync(IReadOnlyList<DnsBenchmarkTarget> targets, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => new DnsBenchmarkRunner(new UdpDnsTransport()).RunAsync(targets, progress, cancellationToken), cancellationToken);

    public async Task<OperationResult> SwitchAsync(DnsChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("DNS servers can only be changed on Windows.");
        }

        if (DnsSwitchPlan.Validate(request) is not DnsSwitchProblem.None and var problem)
        {
            return OperationResult.Fail($"The DNS change is not valid ({problem}).");
        }

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var adapters = status.Adapters.Where(a => request.AdapterIds.Contains(a.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (adapters.Count == 0)
        {
            return OperationResult.Fail("The chosen network adapter is no longer connected. Refresh the page and try again.");
        }

        var dohServers = status.EncryptionSupported ? DnsSwitchPlan.DohServers(request) : [];
        var capture = await CaptureAsync(adapters, request.Label, dohServers.Select(d => d.Server).ToList(), cancellationToken).ConfigureAwait(false);
        var servers = DnsSwitchPlan.Servers(request);
        string names = string.Join(", ", adapters.Select(a => a.Name));
        var options = Options(names, capture);

        return await RunWithCaptureAsync(capture, () => _guard.RunGuardedAsync($"Switch DNS to {request.Label} ({string.Join(", ", servers)})", options, async ct =>
        {
            foreach (var adapter in adapters)
            {
                foreach (var (server, _) in dohServers)
                {
                    var written = WindowsNetworkTuningService.WriteQword(DohInterfaceSettings.KeyFor(adapter.Id, server), DohInterfaceSettings.FlagsValue, DohInterfaceSettings.AutomaticTemplate);
                    if (!written.Success)
                    {
                        return OperationResult.Fail($"Could not turn on DNS over HTTPS for {adapter.Name}: {written.Message}");
                    }
                }
            }

            var commands = dohServers.Select(d => DnsSettingsCapture.DohServerCommand(d.Server, d.Template))
                .Concat(adapters.Select(a => DnsCommands.Set(a.Index, servers)))
                .Append("Clear-DnsClientCache");
            var run = await PowerShellRunner.RunAsync(string.Join("; ", commands), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!run.Success)
            {
                return OperationResult.Fail("Windows refused to change the DNS servers: " + FirstLine(run.StdErr, run.StdOut));
            }

            string encrypted = dohServers.Count > 0 ? " Lookups are encrypted (DNS over HTTPS)." : string.Empty;
            return OperationResult.Ok($"{names} now use {request.Label} ({string.Join(", ", servers)}).{encrypted} The DNS cache was cleared.");
        }, null, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<OperationResult> ResetToAutomaticAsync(IReadOnlyList<string> adapterIds, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("DNS servers can only be changed on Windows.");
        }

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var adapters = status.Adapters.Where(a => adapterIds.Contains(a.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (adapters.Count == 0)
        {
            return OperationResult.Fail("The chosen network adapter is no longer connected. Refresh the page and try again.");
        }

        var capture = await CaptureAsync(adapters, "Automatic", [], cancellationToken).ConfigureAwait(false);
        string names = string.Join(", ", adapters.Select(a => a.Name));
        return await RunWithCaptureAsync(capture, () => _guard.RunGuardedAsync("Set DNS back to automatic (from the router)", Options(names, capture), async ct =>
        {
            var script = string.Join("; ", adapters.Select(a => DnsCommands.Reset(a.Index))) + "; Clear-DnsClientCache";
            var run = await PowerShellRunner.RunAsync(script, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            return run.Success
                ? OperationResult.Ok($"{names} get their DNS servers from the router again. The DNS cache was cleared.")
                : OperationResult.Fail("Windows refused to reset the DNS servers: " + FirstLine(run.StdErr, run.StdOut));
        }, null, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<OperationResult> UndoLastChangeAsync(CancellationToken cancellationToken)
    {
        var previous = LastChange;
        if (previous is null)
        {
            return OperationResult.Fail("There is no DNS change to undo.");
        }

        var indexes = ReadInterfaces(activeOnly: false).ToDictionary(i => i.Id, i => i.Index, StringComparer.OrdinalIgnoreCase);
        var (script, missing) = DnsSettingsCapture.RestoreScript(previous, id => indexes.TryGetValue(id, out var index) ? index : null);
        var options = ChangeOptions.For(Module, string.Join(", ", previous.Adapters.Select(a => a.Name)), reversible: false,
            "Switch or reset the DNS servers again on the DNS page.");
        var result = await _guard.RunGuardedAsync($"Undo the DNS change ({previous.ChangeLabel})", options, async ct =>
        {
            foreach (var (key, value) in DnsSettingsCapture.DohFlagRestores(previous))
            {
                var restored = value is { } old
                    ? WindowsNetworkTuningService.WriteQword(key, DohInterfaceSettings.FlagsValue, old)
                    : LatencyProbes.DeleteValue(RegistryHive.LocalMachine, key, DohInterfaceSettings.FlagsValue);
                if (!restored.Success)
                {
                    return OperationResult.Fail("Could not restore the DNS-over-HTTPS setting: " + restored.Message);
                }
            }

            var commands = DnsSettingsCapture.DohServerRestoreCommands(previous).ToList();
            if (script.Length > 0)
            {
                commands.Add(script);
            }

            if (commands.Count == 0)
            {
                return OperationResult.Ok("There was nothing to restore: the adapters that were changed are no longer present.");
            }

            var run = await PowerShellRunner.RunAsync(string.Join("; ", commands), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!run.Success)
            {
                return OperationResult.Fail("Windows refused to put the previous DNS servers back: " + FirstLine(run.StdErr, run.StdOut));
            }

            string gone = missing.Count == 0 ? string.Empty : $" Not restored (adapter no longer present): {string.Join(", ", missing)}.";
            return OperationResult.Ok("The previous DNS servers are back." + gone);
        }, null, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            Save(null);
        }

        return result;
    }

    private ChangeOptions Options(string adapterNames, DnsPreviousSettings capture)
    {
        string before = string.Join("; ", capture.Adapters.Select(a =>
        {
            var view = DnsSettingsCapture.Describe(a);
            return $"{view.Name}: {(view.Automatic4 ? "automatic" : string.Join(", ", view.Servers4))}";
        }));
        // The page offers the quick Undo through UndoOffers (it calls UndoLastChangeAsync); the journal keeps the hint.
        return ChangeOptions.For(Module, adapterNames, reversible: true, $"Use Undo on the DNS page, or set the DNS servers back to: {before}.");
    }

    /// <summary>Saves the capture before the change and puts the previous one back when the change did not happen.</summary>
    private async Task<OperationResult> RunWithCaptureAsync(DnsPreviousSettings capture, Func<Task<OperationResult>> change)
    {
        var before = LastChange;
        Save(capture);
        OperationResult result;
        try
        {
            result = await change().ConfigureAwait(false);
        }
        catch
        {
            Save(before);
            throw;
        }

        if (!result.Success)
        {
            Save(before);
        }

        return result;
    }

    private void Save(DnsPreviousSettings? capture)
    {
        lock (_gate)
        {
            _loaded = true;
            _last = capture;
            if (_capturePath is null)
            {
                return;
            }

            try
            {
                DnsSettingsCapture.Write(_capturePath, capture);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save the DNS capture to {Path}; Undo works until PC Checker closes.", _capturePath);
            }
        }
    }

    private async Task<DnsPreviousSettings> CaptureAsync(IReadOnlyList<DnsAdapterInfo> adapters, string label, IReadOnlyList<string> dohServers, CancellationToken ct)
    {
        var entries = dohServers.Count > 0 ? await ReadDohServersAsync(ct).ConfigureAwait(false) : [];
        var captured = adapters.Select(a =>
        {
            var capture = DnsSettingsCapture.FromAdapter(a);
            var flags = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
            foreach (var server in dohServers)
            {
                flags[server] = WindowsNetworkTuningService.ReadQword(DohInterfaceSettings.KeyFor(a.Id, server), DohInterfaceSettings.FlagsValue);
            }

            return capture with { DohFlags = flags };
        }).ToList();
        var servers = dohServers
            .Select(s => entries.TryGetValue(s, out var e)
                ? new DohServerCapture(s, true, e.Template, e.AutoUpgrade, e.AllowFallback)
                : new DohServerCapture(s, false, null, false, true))
            .ToList();
        return new DnsPreviousSettings(DateTimeOffset.Now, label, captured, servers);
    }

    private async Task<Dictionary<string, (string Template, bool AutoUpgrade, bool AllowFallback)>> ReadDohServersAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, (string, bool, bool)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = await PowerShellRunner.RunJsonAsync<List<JsonElement>>(
                "@(Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue | Select-Object ServerAddress, DohTemplate, AllowFallbackToUdp, AutoUpgrade)",
                TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            foreach (var row in rows ?? [])
            {
                if (Text(row, "ServerAddress") is { } server)
                {
                    map[DnsResolverCatalog.Normalize(server) is { Length: > 0 } n ? n : server] =
                        (Text(row, "DohTemplate") ?? string.Empty, Text(row, "AutoUpgrade") == "True", Text(row, "AllowFallbackToUdp") != "False");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the DNS-over-HTTPS server list.");
        }

        return map;
    }

    private sealed record Interface(string Id, int Index, string Name, string Description, bool IsWifi, IReadOnlyList<string> Servers4, IReadOnlyList<string> Servers6, string? Gateway);

    /// <summary>Network adapters with an IPv4 index; with <paramref name="activeOnly"/> only connected ones that have a gateway.</summary>
    private IReadOnlyList<Interface> ReadInterfaces(bool activeOnly)
    {
        var list = new List<Interface>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                var gateway = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (activeOnly && (nic.OperationalStatus != OperationalStatus.Up || gateway is null))
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

                var v4 = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                var v6 = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal).Select(a => a.ToString()).ToList();
                list.Add(new Interface(nic.Id, index, nic.Name, nic.Description, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211, v4, v6, gateway?.ToString()));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the network adapters.");
        }

        return list;
    }

    private static string? Text(JsonElement row, string name)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in row.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null,
                };
            }
        }

        return null;
    }

    private static string FirstLine(params string?[] texts)
    {
        var text = texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "no details";
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? text;
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}
