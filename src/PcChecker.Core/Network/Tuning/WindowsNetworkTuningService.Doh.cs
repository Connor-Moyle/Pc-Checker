using System.Text.Json;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Latency;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// DNS over HTTPS (Windows 11). Turning it on takes three parts: the server's DoH template with automatic upgrade
/// (DnsClient cmdlets), the per-adapter <c>DohFlags</c> switch for each DNS server the adapter uses, and the global
/// automatic-upgrade switch. All three are captured before the change and put back on Revert.
/// </summary>
public sealed partial class WindowsNetworkTuningService
{
    private async Task<ActionState> ReadDohStateAsync(string actionId, IReadOnlyList<string> servers, CancellationToken ct)
    {
        var entries = await ReadDohServersAsync(ct).ConfigureAwait(false);
        bool templates = servers.All(s => entries.TryGetValue(s, out var e) && e.AutoUpgrade);
        var targets = DohTargets();
        var missing = targets
            .Where(t => !DohInterfaceSettings.IsEncrypted(ReadQword(DohInterfaceSettings.KeyFor(t.InterfaceId, t.Server), DohInterfaceSettings.FlagsValue)))
            .Select(t => t.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool on = templates && targets.Count > 0 && missing.Count == 0;
        string text = on
            ? $"Encrypted (DNS over HTTPS on for {string.Join(", ", targets.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase))})"
            : templates && missing.Count > 0
                ? $"Not encrypted yet: DNS over HTTPS is off for {string.Join(", ", missing)}"
                : "Not encrypted";
        return new ActionState(actionId, on, text);
    }

    /// <summary>Captures the server entries, the per-adapter switches (null = value was absent) and the global switch.</summary>
    private async Task CaptureDohAsync(Dictionary<string, string?> map, CancellationToken ct)
    {
        var entries = await ReadDohServersAsync(ct).ConfigureAwait(false);
        foreach (var server in DohCapableServers())
        {
            bool exists = entries.TryGetValue(server, out var entry);
            map[$"{server}|exists"] = exists ? "true" : "false";
            if (exists)
            {
                map[$"{server}|template"] = entry.Template;
                map[$"{server}|autoUpgrade"] = entry.AutoUpgrade ? "true" : "false";
                map[$"{server}|fallback"] = entry.AllowFallback ? "true" : "false";
            }
        }

        foreach (var (interfaceId, _, server) in DohTargets())
        {
            map[DohInterfaceSettings.CaptureKey(interfaceId, server)] = ReadQword(DohInterfaceSettings.KeyFor(interfaceId, server), DohInterfaceSettings.FlagsValue)?.ToString();
        }

        map[DohInterfaceSettings.GlobalCaptureKey] = LatencyProbes.ReadDword(RegistryHive.LocalMachine, DohInterfaceSettings.DnscacheParametersKey, DohInterfaceSettings.EnableAutoDohValue)?.ToString();
    }

    private static async Task<OperationResult> EnableDohAsync(CancellationToken ct)
    {
        var servers = DohCapableServers();
        var targets = DohTargets();
        if (servers.Count == 0 || targets.Count == 0)
        {
            return OperationResult.Fail("Your DNS servers do not offer a known DNS-over-HTTPS service. Use \"Use a fast public DNS server\" first.");
        }

        // 1. Server templates with automatic upgrade.
        var commands = servers.Select(s =>
        {
            string template = PowerShellRunner.Quote(NetworkTuningCatalog.DohTemplateFor(s)!);
            string server = PowerShellRunner.Quote(s);
            return $"if (Get-DnsClientDohServerAddress -ServerAddress {server} -ErrorAction SilentlyContinue) {{ Set-DnsClientDohServerAddress -ServerAddress {server} -DohTemplate {template} -AllowFallbackToUdp $true -AutoUpgrade $true -ErrorAction Stop }} "
                + $"else {{ Add-DnsClientDohServerAddress -ServerAddress {server} -DohTemplate {template} -AllowFallbackToUdp $true -AutoUpgrade $true -ErrorAction Stop }}";
        });
        var run = await PowerShellRunner.RunAsync(string.Join("; ", commands), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!run.Success)
        {
            return OperationResult.Fail("Windows refused to turn on DNS over HTTPS: " + FirstLine(run.StdErr, run.StdOut));
        }

        // 2. The per-adapter switch ("DNS over HTTPS: On (automatic template)" in Settings) for every server each adapter uses.
        foreach (var (interfaceId, name, server) in targets)
        {
            var written = WriteQword(DohInterfaceSettings.KeyFor(interfaceId, server), DohInterfaceSettings.FlagsValue, DohInterfaceSettings.AutomaticTemplate);
            if (!written.Success)
            {
                return OperationResult.Fail($"Could not turn on DNS over HTTPS for {name} ({server}): {written.Message}. Use Revert to undo the part that was changed.");
            }
        }

        // 3. Global automatic upgrade.
        var global = LatencyProbes.WriteDword(RegistryHive.LocalMachine, DohInterfaceSettings.DnscacheParametersKey, DohInterfaceSettings.EnableAutoDohValue, DohInterfaceSettings.EnableAutoDohOn);
        if (!global.Success)
        {
            return OperationResult.Fail("Could not turn on automatic DNS-over-HTTPS upgrade: " + global.Message + ". Use Revert to undo the part that was changed.");
        }

        await FlushDnsCacheAsync(ct).ConfigureAwait(false);
        string adapters = string.Join(", ", targets.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase));
        return OperationResult.Ok($"DNS lookups from {adapters} to {string.Join(", ", servers)} are now encrypted. If Settings does not show it yet, turn the adapter off and on again or restart the PC.");
    }

    private static async Task<OperationResult> RestoreDohAsync(IReadOnlyDictionary<string, string?>? previous, CancellationToken ct)
    {
        if (previous is null || previous.Count == 0)
        {
            return OperationResult.Fail("Nothing was recorded before DNS over HTTPS was turned on, so there is nothing to restore. Change it in Settings → Network & internet → your adapter → DNS server assignment.");
        }

        // Per-adapter switches and the global switch first, so no adapter keeps asking for a template that is about to go.
        foreach (var (key, value) in previous)
        {
            if (!DohInterfaceSettings.TryParseCaptureKey(key, out var interfaceId, out var server) || DohInterfaceSettings.KeyFor(interfaceId, server) is not { } registryKey)
            {
                continue;
            }

            var restored = long.TryParse(value, out long old)
                ? WriteQword(registryKey, DohInterfaceSettings.FlagsValue, old)
                : LatencyProbes.DeleteValue(RegistryHive.LocalMachine, registryKey, DohInterfaceSettings.FlagsValue);
            if (!restored.Success)
            {
                return OperationResult.Fail("Could not restore the adapter's DNS-over-HTTPS setting: " + restored.Message);
            }
        }

        if (previous.TryGetValue(DohInterfaceSettings.GlobalCaptureKey, out var globalValue))
        {
            var restored = int.TryParse(globalValue, out int old)
                ? LatencyProbes.WriteDword(RegistryHive.LocalMachine, DohInterfaceSettings.DnscacheParametersKey, DohInterfaceSettings.EnableAutoDohValue, old)
                : LatencyProbes.DeleteValue(RegistryHive.LocalMachine, DohInterfaceSettings.DnscacheParametersKey, DohInterfaceSettings.EnableAutoDohValue);
            if (!restored.Success)
            {
                return OperationResult.Fail("Could not restore the automatic DNS-over-HTTPS upgrade setting: " + restored.Message);
            }
        }

        var commands = new List<string>();
        foreach (var server in previous.Keys.Where(k => k.EndsWith("|exists", StringComparison.OrdinalIgnoreCase)).Select(k => k[..k.IndexOf('|')]))
        {
            string quoted = PowerShellRunner.Quote(server);
            if (previous[$"{server}|exists"] != "true")
            {
                commands.Add($"Remove-DnsClientDohServerAddress -ServerAddress {quoted} -ErrorAction SilentlyContinue");
                continue;
            }

            string template = PowerShellRunner.Quote(previous.GetValueOrDefault($"{server}|template") ?? NetworkTuningCatalog.DohTemplateFor(server) ?? string.Empty);
            string auto = previous.GetValueOrDefault($"{server}|autoUpgrade") == "true" ? "$true" : "$false";
            string fallback = previous.GetValueOrDefault($"{server}|fallback") == "false" ? "$false" : "$true";
            commands.Add($"Set-DnsClientDohServerAddress -ServerAddress {quoted} -DohTemplate {template} -AutoUpgrade {auto} -AllowFallbackToUdp {fallback} -ErrorAction Stop");
        }

        commands.Add("Clear-DnsClientCache");
        var run = await PowerShellRunner.RunAsync(string.Join("; ", commands), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return run.Success ? OperationResult.Ok("DNS over HTTPS is back to how it was.") : OperationResult.Fail("Windows refused to restore the DNS-over-HTTPS settings: " + FirstLine(run.StdErr, run.StdOut));
    }

    private async Task<Dictionary<string, (string Template, bool AutoUpgrade, bool AllowFallback)>> ReadDohServersAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, (string, bool, bool)>(StringComparer.OrdinalIgnoreCase);
        var rows = await PowerShellRunner.RunJsonAsync<List<JsonElement>>(
            "Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue | Select-Object ServerAddress, DohTemplate, AllowFallbackToUdp, AutoUpgrade", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        foreach (var row in rows ?? [])
        {
            var server = Text(row, "ServerAddress");
            if (server is not null)
            {
                map[server] = (Text(row, "DohTemplate") ?? string.Empty, Text(row, "AutoUpgrade") == "True", Text(row, "AllowFallbackToUdp") != "False");
            }
        }

        return map;
    }

    /// <summary>DNS servers in use that belong to a known DoH provider.</summary>
    private static IReadOnlyList<string> DohCapableServers() =>
        ActiveInterfaces().SelectMany(a => a.Dns).Distinct().Where(s => NetworkTuningCatalog.DohTemplateFor(s) is not null).ToList();

    /// <summary>Each connected adapter paired with every DoH-capable DNS server it uses.</summary>
    private static IReadOnlyList<(string InterfaceId, string Name, string Server)> DohTargets() =>
        ActiveInterfaces()
            .SelectMany(a => a.Dns.Distinct()
                .Where(s => NetworkTuningCatalog.DohTemplateFor(s) is not null && DohInterfaceSettings.KeyFor(a.Id, s) is not null)
                .Select(s => (a.Id, a.Name, s)))
            .ToList();

    private static async Task FlushDnsCacheAsync(CancellationToken ct)
    {
        try
        {
            await LatencyProbes.RunAsync("ipconfig.exe", "/flushdns", ct, 30).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A stale cache only delays the change until entries expire.
        }
    }

    private static long? ReadQword(string? subKey, string valueName)
    {
        if (!PlatformGuard.IsWindows || subKey is null)
        {
            return null;
        }

        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(subKey, false);
            return key?.GetValue(valueName) switch
            {
                long l => l,
                int i => i,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static OperationResult WriteQword(string? subKey, string valueName, long value)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Registry changes are only possible on Windows.");
        }

        if (subKey is null)
        {
            return OperationResult.Fail("The adapter or DNS server could not be identified.");
        }

        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).CreateSubKey(subKey, true);
            key.SetValue(valueName, value, RegistryValueKind.QWord);
            return OperationResult.Ok($"{valueName} = {value}");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not write {subKey}\\{valueName}: {ex.Message}", ex);
        }
    }
}
