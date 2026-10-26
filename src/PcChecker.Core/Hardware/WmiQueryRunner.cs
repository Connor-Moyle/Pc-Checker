using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;

namespace PcChecker.Core.Hardware;

/// <summary>A WMI row as a plain property bag (property name → raw value).</summary>
public sealed class WmiRow
{
    public WmiRow(Dictionary<string, object?> bag) => Bag = bag;

    public Dictionary<string, object?> Bag { get; }

    public string? Str(string key) => WmiPropertyMapper.GetString(Bag, key);
    public long? Long(string key) => WmiPropertyMapper.GetInt64(Bag, key);
    public int? Int(string key) => WmiPropertyMapper.GetInt32(Bag, key);
    public bool? Bool(string key) => WmiPropertyMapper.GetBool(Bag, key);
    public DateTime? Date(string key) => WmiPropertyMapper.GetDate(Bag, key);
    public IReadOnlyList<string> List(string key) => WmiPropertyMapper.GetStringList(Bag, key);
    public object? Raw(string key) => Bag.TryGetValue(key, out var v) ? v : null;

    /// <summary>Embedded objects render as "Key=Value; Key=Value" inside a parent property bag.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        foreach (var (k, v) in Bag)
        {
            var text = WmiPropertyMapper.FormatValue(k, v);
            if (text is not null)
            {
                parts.Add($"{k}={text}");
            }
        }

        return string.Join("; ", parts);
    }

    /// <summary>The display dictionary for <c>HardwareItem.Properties</c>, optionally merged with extra prefixed bags.</summary>
    public IReadOnlyDictionary<string, string> ToProperties(params (string Prefix, WmiRow? Row)[] extras)
    {
        var merged = new Dictionary<string, object?>(Bag, StringComparer.OrdinalIgnoreCase);
        foreach (var (prefix, row) in extras)
        {
            if (row is null)
            {
                continue;
            }

            foreach (var (k, v) in row.Bag)
            {
                merged[$"{prefix}.{k}"] = v;
            }
        }

        return WmiPropertyMapper.ToStringDictionary(merged);
    }
}

/// <summary>
/// Runs WQL queries and registry reads with every failure caught and logged, so a missing WMI class, a broken
/// repository or a denied namespace only produces a warning instead of crashing a scan.
/// </summary>
public sealed class WmiQueryRunner
{
    public const string Cimv2 = @"root\cimv2";
    public const string WmiRoot = @"root\WMI";
    public const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    public const string TpmNamespace = @"root\cimv2\Security\MicrosoftTpm";

    private readonly ILogger _logger;
    private readonly List<string> _warnings;

    public WmiQueryRunner(ILogger logger, List<string> warnings)
    {
        _logger = logger;
        _warnings = warnings;
    }

    /// <summary>Runs a query and returns every row; an empty list (plus a warning) when the query fails.</summary>
    public List<WmiRow> Query(string wql, string scope = Cimv2, string? friendlyName = null, CancellationToken cancellationToken = default)
    {
        var rows = new List<WmiRow>();
        if (!PlatformGuard.IsWindows)
        {
            _warnings.Add($"{friendlyName ?? wql}: WMI is only available on Windows.");
            return rows;
        }

        try
        {
            var managementScope = new ManagementScope(scope, new ConnectionOptions { Timeout = TimeSpan.FromSeconds(20) });
            managementScope.Connect();
            using var searcher = new ManagementObjectSearcher(managementScope, new ObjectQuery(wql), new System.Management.EnumerationOptions { ReturnImmediately = true, Timeout = TimeSpan.FromSeconds(60) });
            using var collection = searcher.Get();
            foreach (ManagementBaseObject obj in collection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (obj)
                {
                    rows.Add(new WmiRow(ToBag(obj)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var name = friendlyName ?? wql;
            _logger.LogWarning(ex, "WMI query failed: {Query} in {Scope}", wql, scope);
            _warnings.Add($"{name} could not be read ({ex.GetType().Name}: {Shorten(ex.Message)}).");
        }

        return rows;
    }

    /// <summary>Copies every property of a WMI object into a dictionary. Embedded objects become nested bags.</summary>
    public static Dictionary<string, object?> ToBag(ManagementBaseObject obj)
    {
        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyData property in obj.Properties)
        {
            try
            {
                bag[property.Name] = Unwrap(property.Value);
            }
            catch (Exception)
            {
                // A single unreadable property must not lose the whole row.
            }
        }

        return bag;
    }

    private static object? Unwrap(object? value) => value switch
    {
        ManagementBaseObject embedded => new WmiRow(ToBag(embedded)),
        ManagementBaseObject[] embeddedArray => embeddedArray.Select(e => new WmiRow(ToBag(e))).ToArray(),
        _ => value,
    };

    /// <summary>Reads a registry value from HKLM; null on any failure (missing key, access denied, not Windows).</summary>
    public object? ReadLocalMachine(string subKey, string valueName)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Registry read failed: HKLM\\{Key}\\{Value}", subKey, valueName);
            return null;
        }
    }

    /// <summary>Lists the sub-key names under an HKLM key; empty on failure.</summary>
    public IReadOnlyList<string> SubKeys(string subKey)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Registry enumeration failed: HKLM\\{Key}", subKey);
            return [];
        }
    }

    /// <summary>Interprets a registry value as a 64-bit number (QWORD, DWORD, string or little-endian binary).</summary>
    public static long? AsInt64(object? value) => value switch
    {
        null => null,
        long l => l,
        int i => (uint)i,
        uint ui => ui,
        ulong ul => ul > long.MaxValue ? long.MaxValue : (long)ul,
        string s when long.TryParse(s, out var parsed) => parsed,
        byte[] { Length: >= 8 } bytes => BitConverter.ToInt64(bytes, 0),
        byte[] { Length: >= 4 } bytes => BitConverter.ToUInt32(bytes, 0),
        _ => null,
    };

    private static string Shorten(string text) => text.Length > 160 ? text[..157] + "…" : text.Replace('\r', ' ').Replace('\n', ' ');
}
