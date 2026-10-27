using System.Text.Json;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Latency;
using PcChecker.Core.Logging;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// Windows implementation of <see cref="INetworkTuningService"/>. Adapter properties use the NetAdapter PowerShell
/// cmdlets; system tweaks use powercfg, netsh, the DnsClient cmdlets and the registry; saved networks use
/// <c>netsh wlan</c>. Before any change the value it overwrites is captured into
/// <see cref="NetworkTuningCaptureStore"/> (and the action journal), so Revert restores it even after a restart.
/// Every write runs through <see cref="IChangeGuard"/>.
/// </summary>
public sealed partial class WindowsNetworkTuningService : INetworkTuningService
{
    private const string Module = "Network";

    private readonly IChangeGuard _guard;
    private readonly IActionJournal _journal;
    private readonly ILogger<WindowsNetworkTuningService> _logger;
    private readonly string? _capturePath;
    private readonly object _captureLock = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, string?>> _captured = new(StringComparer.OrdinalIgnoreCase);
    private bool _capturesLoaded;

    public WindowsNetworkTuningService(IChangeGuard guard, IActionJournal journal, ISettingsService settings, ILogger<WindowsNetworkTuningService> logger)
    {
        _guard = guard;
        _journal = journal;
        _logger = logger;
        try
        {
            _capturePath = NetworkTuningCaptureStore.DefaultPath(settings.SettingsDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Network tuning captures will not be persisted: the settings directory is unknown.");
        }
    }

    public IReadOnlyList<TuningAction> Actions => NetworkTuningCatalog.All;

    // ---------------------------------------------------------------- adapter properties

    public async Task<IReadOnlyList<AdapterSetting>> GetAdapterSettingsAsync(string? adapterName, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        var properties = await ReadAdvancedPropertiesAsync(adapterName, cancellationToken).ConfigureAwait(false);
        if (properties.Count == 0)
        {
            return [];
        }

        var report = await ReadWifiContextAsync(cancellationToken).ConfigureAwait(false);
        bool laptop = NetworkSystemInfo.HasBattery();
        var wifiAdapters = properties.Where(p => p.IsWifi).Select(p => p.Property.AdapterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wifiContext = AdapterSettingAdvisor.ContextFrom(report, isWifi: true, laptop);
        var wiredContext = wifiContext with { IsWifi = false };
        return properties
            .Select(p => AdapterSettingAdvisor.Advise(p.Property, wifiAdapters.Contains(p.Property.AdapterName) ? wifiContext : wiredContext))
            .ToList();
    }

    public async Task<OperationResult> SetAdapterSettingAsync(string adapterName, string key, string value, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Adapter settings can only be changed on Windows.");
        }

        var settings = await GetAdapterSettingsAsync(adapterName, cancellationToken).ConfigureAwait(false);
        var setting = settings.FirstOrDefault(s => s.AdapterName.Equals(adapterName, StringComparison.OrdinalIgnoreCase) && s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (setting is null)
        {
            return OperationResult.Fail($"The adapter \"{adapterName}\" has no setting \"{key}\".");
        }

        if (!AdapterSettingAdvisor.IsAllowed(setting, value))
        {
            return OperationResult.Fail($"\"{value}\" is not one of the values the driver allows for {setting.DisplayName}.");
        }

        string entry = NetworkTuningCaptureStore.AdapterEntryId(adapterName, setting.Key);
        var previous = Capture(entry, new Dictionary<string, string?> { ["value"] = setting.CurrentValue });
        return await WriteAdapterPropertyAsync(setting, value, previous.GetValueOrDefault("value"), $"Set {setting.DisplayName} to {setting.LabelFor(value)} on {adapterName}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> RevertAdapterSettingAsync(string adapterName, string key, CancellationToken cancellationToken)
    {
        string entry = NetworkTuningCaptureStore.AdapterEntryId(adapterName, key);
        var previous = Captured(entry);
        if (previous?.GetValueOrDefault("value") is not { } value)
        {
            return OperationResult.Fail("PC Checker has not changed this setting, so there is no previous value to put back.");
        }

        var settings = await GetAdapterSettingsAsync(adapterName, cancellationToken).ConfigureAwait(false);
        var setting = settings.FirstOrDefault(s => s.AdapterName.Equals(adapterName, StringComparison.OrdinalIgnoreCase) && s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (setting is null)
        {
            return OperationResult.Fail($"The adapter \"{adapterName}\" no longer reports \"{key}\". It may have been removed or its driver changed.");
        }

        var result = await WriteAdapterPropertyAsync(setting, value, setting.CurrentValue, $"Undo: {setting.DisplayName} back to {setting.LabelFor(value)} on {adapterName}", cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            Forget(entry);
        }

        return result;
    }

    public bool CanRevertAdapterSetting(string adapterName, string key) =>
        Captured(NetworkTuningCaptureStore.AdapterEntryId(adapterName, key))?.GetValueOrDefault("value") is not null;

    private async Task<OperationResult> WriteAdapterPropertyAsync(AdapterSetting setting, string value, string? beforeValue, string description, CancellationToken cancellationToken)
    {
        string before = setting.LabelFor(beforeValue);
        string undo = $"Use Revert on the Network page, or run: Set-NetAdapterAdvancedProperty -Name '{setting.AdapterName}' -RegistryKeyword '{setting.Key}' -RegistryValue '{beforeValue}'";
        var options = new ChangeOptions(false, null, Module, $"{setting.AdapterName}: {setting.Key}", true, undo);
        var result = await _guard.RunGuardedAsync(description, options, async ct =>
        {
            string command = $"Set-NetAdapterAdvancedProperty -Name {PowerShellRunner.Quote(setting.AdapterName)} -RegistryKeyword {PowerShellRunner.Quote(setting.Key)} -RegistryValue {PowerShellRunner.Quote(value)} -ErrorAction Stop";
            var run = await PowerShellRunner.RunAsync(command, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            return run.Success
                ? OperationResult.Ok($"{setting.DisplayName} is now {setting.LabelFor(value)}. The adapter restarted, so the connection may have dropped for a few seconds.")
                : OperationResult.Fail($"Windows refused to change {setting.DisplayName}: {FirstLine(run.StdErr, run.StdOut)}");
        }, null, cancellationToken).ConfigureAwait(false);
        await RecordValuesAsync(description, setting.AdapterName + ": " + setting.Key, before, setting.LabelFor(value), result).ConfigureAwait(false);
        return result;
    }

    private async Task<IReadOnlyList<(AdapterAdvancedProperty Property, bool IsWifi)>> ReadAdvancedPropertiesAsync(string? adapterName, CancellationToken cancellationToken)
    {
        string filter = string.IsNullOrWhiteSpace(adapterName) ? string.Empty : $" -Name {PowerShellRunner.Quote(adapterName)}";
        string command =
            $"Get-NetAdapter -Physical{filter} -ErrorAction SilentlyContinue | Where-Object {{ $_.Status -ne 'Not Present' }} | ForEach-Object {{ " +
            "$a = $_; Get-NetAdapterAdvancedProperty -Name $a.Name -ErrorAction SilentlyContinue | Where-Object { $_.RegistryKeyword } | " +
            "Select-Object @{n='AdapterName';e={$a.Name}}, @{n='MediaType';e={[string]$a.PhysicalMediaType}}, DisplayName, DisplayValue, RegistryKeyword, " +
            "@{n='RegistryValue';e={@($_.RegistryValue) -join ','}}, ValidDisplayValues, ValidRegistryValues }";
        try
        {
            var rows = await PowerShellRunner.RunJsonAsync<List<JsonElement>>(command, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            if (rows is null)
            {
                return [];
            }

            var list = new List<(AdapterAdvancedProperty, bool)>();
            foreach (var row in rows)
            {
                string keyword = Text(row, "RegistryKeyword") ?? string.Empty;
                string name = Text(row, "AdapterName") ?? string.Empty;
                if (keyword.Length == 0 || name.Length == 0)
                {
                    continue;
                }

                string media = Text(row, "MediaType") ?? string.Empty;
                bool wifi = media.Contains("802.11", StringComparison.OrdinalIgnoreCase) || media.Contains("Wireless", StringComparison.OrdinalIgnoreCase);
                list.Add((new AdapterAdvancedProperty(name, Text(row, "DisplayName") ?? keyword, Text(row, "DisplayValue"), keyword, Text(row, "RegistryValue"),
                    Strings(row, "ValidDisplayValues"), Strings(row, "ValidRegistryValues")), wifi));
            }

            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read adapter advanced properties.");
            return [];
        }
    }

    /// <summary>A light Wi-Fi scan for the advisor (no findings, no power settings).</summary>
    private async Task<WifiReport?> ReadWifiContextAsync(CancellationToken cancellationToken)
    {
        try
        {
            var interfaces = await LatencyProbes.RunAsync("netsh.exe", "wlan show interfaces", cancellationToken, 20).ConfigureAwait(false);
            var current = NetshWlanParser.ParseInterfaces(interfaces.Output).FirstOrDefault(i => i.IsConnected);
            var networks = await LatencyProbes.RunAsync("netsh.exe", "wlan show networks mode=bssid", cancellationToken, 20).ConfigureAwait(false);
            var nearby = NetshWlanParser.ParseNetworks(networks.Output, current?.Bssid);
            return new WifiReport(current, nearby, [], [], [], DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wi-Fi context for adapter recommendations is unavailable.");
            return null;
        }
    }

    // ---------------------------------------------------------------- captures and journal

    /// <summary>Stores <paramref name="values"/> for <paramref name="entry"/> unless an earlier capture exists; returns the capture in effect.</summary>
    private IReadOnlyDictionary<string, string?> Capture(string entry, IReadOnlyDictionary<string, string?> values)
    {
        lock (_captureLock)
        {
            EnsureCapturesLoaded();
            // Keep the first capture: that is what the PC looked like before PC Checker touched it.
            if (_captured.TryGetValue(entry, out var existing))
            {
                return existing;
            }

            _captured[entry] = values;
            SaveCaptures();
            return values;
        }
    }

    private IReadOnlyDictionary<string, string?>? Captured(string entry)
    {
        lock (_captureLock)
        {
            EnsureCapturesLoaded();
            return _captured.GetValueOrDefault(entry);
        }
    }

    private void Remember(string entry, IReadOnlyDictionary<string, string?> values)
    {
        lock (_captureLock)
        {
            EnsureCapturesLoaded();
            _captured[entry] = values;
            SaveCaptures();
        }
    }

    private void Forget(string entry)
    {
        lock (_captureLock)
        {
            EnsureCapturesLoaded();
            if (_captured.Remove(entry))
            {
                SaveCaptures();
            }
        }
    }

    private void EnsureCapturesLoaded()
    {
        if (_capturesLoaded)
        {
            return;
        }

        _capturesLoaded = true;
        if (_capturePath is null)
        {
            return;
        }

        foreach (var (id, values) in NetworkTuningCaptureStore.TryRead(_capturePath))
        {
            _captured.TryAdd(id, values);
        }
    }

    private void SaveCaptures()
    {
        if (_capturePath is null)
        {
            return;
        }

        try
        {
            NetworkTuningCaptureStore.Write(_capturePath, _captured);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the network tuning captures to {Path}; Revert will fall back to Windows defaults after a restart.", _capturePath);
        }
    }

    /// <summary>Adds a journal entry with the before/after values next to the change guard's own record.</summary>
    private async Task RecordValuesAsync(string description, string target, string? before, string? after, OperationResult result)
    {
        if (!result.Success)
        {
            return;
        }

        try
        {
            await _journal.RecordAsync(ActionRecord.Create(Module, description + " (values)", target, "Recorded", before, after, reversible: true,
                undoHint: "Use Revert on the Network page.")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record the before/after values.");
        }
    }

    private static string? Text(JsonElement row, string name)
    {
        foreach (var property in row.EnumerateObject())
        {
            if (property.NameEquals(name) || property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    JsonValueKind.Array => string.Join(",", property.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
                    _ => null,
                };
            }
        }

        return null;
    }

    private static IReadOnlyList<string> Strings(JsonElement row, string name)
    {
        foreach (var property in row.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.Array => property.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText()).ToList(),
                JsonValueKind.String => [property.Value.GetString() ?? string.Empty],
                _ => [],
            };
        }

        return [];
    }

    private static string FirstLine(params string?[] texts)
    {
        var text = texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "no details";
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? text;
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private static string Describe((int ExitCode, string Output, string Error) run) =>
        !string.IsNullOrWhiteSpace(run.Error) ? FirstLine(run.Error)
        : !string.IsNullOrWhiteSpace(run.Output) ? FirstLine(run.Output)
        : $"exit code {run.ExitCode}";
}
