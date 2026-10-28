using System.Globalization;
using System.Management;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Security;

/// <summary>
/// Small guarded WMI and registry readers for the Security and Privacy modules. Every call returns empty or null instead
/// of throwing, so one missing class (Home editions, Server, a third-party antivirus) never hides the rest.
/// </summary>
internal static class SecurityWmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string Defender = @"root\Microsoft\Windows\Defender";
    public const string SecurityCenter = @"root\SecurityCenter2";
    public const string StandardCimv2 = @"root\StandardCimv2";
    public const string Tpm = @"root\cimv2\Security\MicrosoftTpm";
    public const string Encryption = @"root\cimv2\Security\MicrosoftVolumeEncryption";
    public const string DeviceGuard = @"root\Microsoft\Windows\DeviceGuard";

    /// <summary>Runs a WQL query; null when the namespace or class is missing or access is denied.</summary>
    public static List<Dictionary<string, object?>>? Query(string scope, string wql, ManagementNamedValueCollection? context = null)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            var options = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(30) };
            if (context is not null)
            {
                options.Context = context;
            }

            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
            using var results = searcher.Get();
            var rows = new List<Dictionary<string, object?>>();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in item.Properties)
                    {
                        row[property.Name] = property.Value;
                    }

                    rows.Add(row);
                }
            }

            return rows;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    public static string? Str(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null)
        {
            return null;
        }

        var s = Convert.ToString(v, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public static long? Long(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null)
        {
            return null;
        }

        try
        {
            return v is bool b ? (b ? 1 : 0) : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static int? Int(IReadOnlyDictionary<string, object?> row, string key) =>
        Long(row, key) is { } l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;

    public static bool? Bool(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is bool b ? b : Long(row, key) is { } l ? l != 0 : null;

    public static DateTimeOffset? Date(IReadOnlyDictionary<string, object?> row, string key)
    {
        var text = Str(row, key);
        return DmtfDateTime.TryParse(text, out var parsed) && parsed.Year > 1970 ? parsed : null;
    }

    public static IReadOnlyList<uint>? UIntArray(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is not Array array)
        {
            return null;
        }

        var list = new List<uint>();
        foreach (var item in array)
        {
            try
            {
                list.Add(Convert.ToUInt32(item, CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
            }
        }

        return list;
    }

    /// <summary>Reads a DWORD (or a numeric string) from HKLM or HKCU; null when missing or unreadable.</summary>
    public static int? RegInt(RegistryHive hive, string path, string name)
    {
        var value = RegValue(hive, path, name);
        return value switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    public static string? RegString(RegistryHive hive, string path, string name) => RegValue(hive, path, name) as string;

    public static object? RegValue(RegistryHive hive, string path, string? name)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static bool RegKeyExists(RegistryHive hive, string path)
    {
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
