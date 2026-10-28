using System.Net;
using System.Text.Json;
using PcChecker.Core.Common;
using PcChecker.Core.Network.Tuning;

namespace PcChecker.Core.Network.Resolvers;

/// <summary>
/// One adapter as it was before a change. <see cref="NameServer"/> and <see cref="NameServer6"/> are the Tcpip and
/// Tcpip6 registry values (empty = automatic; <see cref="NameServer6"/> null = IPv6 was not recorded and is left
/// alone). <see cref="DohFlags"/> maps each server whose DNS-over-HTTPS switch the change touched to its previous
/// value (null = no value).
/// </summary>
public sealed record DnsAdapterCapture(string InterfaceId, string Name, string NameServer, string? NameServer6, IReadOnlyDictionary<string, long?> DohFlags);

/// <summary>A DNS-over-HTTPS server entry as it was: missing, or its template and switches.</summary>
public sealed record DohServerCapture(string Server, bool Existed, string? Template, bool AutoUpgrade, bool AllowFallback);

/// <summary>Everything one switch or reset replaced, so Undo can put it back exactly.</summary>
public sealed record DnsPreviousSettings(DateTimeOffset Taken, string ChangeLabel, IReadOnlyList<DnsAdapterCapture> Adapters, IReadOnlyList<DohServerCapture> DohServers);

/// <summary>An adapter's previous DNS in plain parts, for messages ("Wi-Fi: automatic" / "Wi-Fi: 1.1.1.1, 1.0.0.1").</summary>
public sealed record DnsPreviousView(string Name, bool Automatic4, IReadOnlyList<string> Servers4, bool Automatic6, IReadOnlyList<string> Servers6);

/// <summary>Capturing, saving and restoring the settings a DNS change replaces. Pure apart from the file helpers.</summary>
public static class DnsSettingsCapture
{
    public const string FileName = "dns-switch.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>The capture of one adapter before a change (no DNS-over-HTTPS values yet).</summary>
    public static DnsAdapterCapture FromAdapter(DnsAdapterInfo adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return new DnsAdapterCapture(adapter.Id, adapter.Name, adapter.ManualServers4?.Trim() ?? string.Empty, adapter.ManualServers6?.Trim(),
            new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The previous DNS of an adapter split into parts for a message.</summary>
    public static DnsPreviousView Describe(DnsAdapterCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var v4 = DnsCommands.Split(capture.NameServer);
        var v6 = DnsCommands.Split(capture.NameServer6);
        return new DnsPreviousView(capture.Name, v4.Count == 0, v4, v6.Count == 0, v6);
    }

    /// <summary>
    /// The PowerShell that puts each captured adapter's servers back, followed by clearing the DNS cache. Adapters that
    /// are gone (no interface index) are skipped and listed in <c>Missing</c>. Empty script when nothing can be restored.
    /// </summary>
    public static (string Script, IReadOnlyList<string> Missing) RestoreScript(DnsPreviousSettings previous, Func<string, int?> indexOf)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(indexOf);
        var commands = new List<string>();
        var missing = new List<string>();
        foreach (var adapter in previous.Adapters)
        {
            if (indexOf(adapter.InterfaceId) is not { } index || index < 0)
            {
                missing.Add(adapter.Name);
                continue;
            }

            commands.Add(DnsCommands.Restore(index, adapter.NameServer, adapter.NameServer6));
        }

        return commands.Count == 0 ? (string.Empty, missing) : (string.Join("; ", commands) + "; Clear-DnsClientCache", missing);
    }

    /// <summary>The registry writes that put each adapter's DNS-over-HTTPS switches back (null value = delete it).</summary>
    public static IReadOnlyList<(string Key, long? Value)> DohFlagRestores(DnsPreviousSettings previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return previous.Adapters
            .SelectMany(a => a.DohFlags.Select(f => (Key: DohInterfaceSettings.KeyFor(a.InterfaceId, f.Key), f.Value)))
            .Where(r => r.Key is not null)
            .Select(r => (r.Key!, r.Value))
            .ToList();
    }

    /// <summary>The commands that put DNS-over-HTTPS server entries back: removed when PC Checker added them, otherwise reset.</summary>
    public static IReadOnlyList<string> DohServerRestoreCommands(DnsPreviousSettings previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var commands = new List<string>();
        foreach (var server in previous.DohServers)
        {
            if (!IPAddress.TryParse(server.Server, out _))
            {
                continue;
            }

            string quoted = PowerShellRunner.Quote(server.Server);
            if (!server.Existed)
            {
                commands.Add($"Remove-DnsClientDohServerAddress -ServerAddress {quoted} -ErrorAction SilentlyContinue");
                continue;
            }

            string template = PowerShellRunner.Quote(server.Template ?? DnsResolverCatalog.DohTemplateFor(server.Server) ?? string.Empty);
            commands.Add($"Set-DnsClientDohServerAddress -ServerAddress {quoted} -DohTemplate {template} -AutoUpgrade {Bool(server.AutoUpgrade)} -AllowFallbackToUdp {Bool(server.AllowFallback)} -ErrorAction Stop");
        }

        return commands;
    }

    /// <summary>The command that adds or updates a DNS-over-HTTPS template, falling back to plain DNS if it fails.</summary>
    public static string DohServerCommand(string server, string template)
    {
        string s = PowerShellRunner.Quote(server);
        string t = PowerShellRunner.Quote(template);
        return $"if (Get-DnsClientDohServerAddress -ServerAddress {s} -ErrorAction SilentlyContinue) {{ Set-DnsClientDohServerAddress -ServerAddress {s} -DohTemplate {t} -AllowFallbackToUdp $true -AutoUpgrade $true -ErrorAction Stop }} "
            + $"else {{ Add-DnsClientDohServerAddress -ServerAddress {s} -DohTemplate {t} -AllowFallbackToUdp $true -AutoUpgrade $true -ErrorAction Stop }}";
    }

    public static string Serialize(DnsPreviousSettings previous) => JsonSerializer.Serialize(previous, JsonOptions);

    /// <summary>Reads a saved capture; null for empty, malformed or unusable text (never throws).</summary>
    public static DnsPreviousSettings? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var value = JsonSerializer.Deserialize<DnsPreviousSettings>(json, JsonOptions);
            if (value?.Adapters is null || value.Adapters.Count == 0 || value.Adapters.Any(a => string.IsNullOrWhiteSpace(a?.InterfaceId)))
            {
                return null;
            }

            return value with
            {
                ChangeLabel = value.ChangeLabel ?? string.Empty,
                DohServers = value.DohServers ?? [],
                Adapters = value.Adapters.Select(a => a with
                {
                    Name = a.Name ?? a.InterfaceId,
                    NameServer = a.NameServer ?? string.Empty,
                    DohFlags = a.DohFlags ?? new Dictionary<string, long?>(),
                }).ToList(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static DnsPreviousSettings? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the capture (null deletes the file).</summary>
    public static void Write(string path, DnsPreviousSettings? previous)
    {
        if (previous is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(previous));
        File.Move(temp, path, overwrite: true);
    }

    private static string Bool(bool value) => value ? "$true" : "$false";
}
