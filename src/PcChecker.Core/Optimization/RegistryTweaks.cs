using Microsoft.Win32;
using PcChecker.Core.Common;

namespace PcChecker.Core.Optimization;

/// <summary>
/// Guarded registry access used by the optimization and performance modules. Every call swallows platform and
/// permission errors: readers return null, writers return a failed <see cref="OperationResult"/>.
/// Always uses the 64-bit view so HKLM\SOFTWARE is not redirected.
/// </summary>
public static class RegistryTweaks
{
    public static int? GetDword(RegistryHive hive, string subKey, string name)
    {
        var value = GetValue(hive, subKey, name);
        return value switch
        {
            int i => i,
            long l => unchecked((int)l),
            uint u => unchecked((int)u),
            string s when int.TryParse(s, out int parsed) => parsed,
            _ => null,
        };
    }

    public static string? GetString(RegistryHive hive, string subKey, string name) =>
        GetValue(hive, subKey, name) switch
        {
            string s => s,
            string[] array => string.Join(Environment.NewLine, array),
            null => null,
            var other => other.ToString(),
        };

    public static byte[]? GetBinary(RegistryHive hive, string subKey, string name) => GetValue(hive, subKey, name) as byte[];

    public static object? GetValue(RegistryHive hive, string subKey, string name)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch
        {
            return null;
        }
    }

    public static bool KeyExists(RegistryHive hive, string subKey)
    {
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey, writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool ValueExists(RegistryHive hive, string subKey, string name) => GetValue(hive, subKey, name) is not null;

    /// <summary>Lists the value names under a key (empty when missing).</summary>
    public static IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey, writable: false);
            return key?.GetValueNames() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static OperationResult SetDword(RegistryHive hive, string subKey, string name, int value) =>
        SetValue(hive, subKey, name, value, RegistryValueKind.DWord);

    public static OperationResult SetString(RegistryHive hive, string subKey, string name, string value) =>
        SetValue(hive, subKey, name, value, RegistryValueKind.String);

    public static OperationResult SetBinary(RegistryHive hive, string subKey, string name, byte[] value) =>
        SetValue(hive, subKey, name, value, RegistryValueKind.Binary);

    public static OperationResult SetValue(RegistryHive hive, string subKey, string name, object value, RegistryValueKind kind)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Registry changes are only possible on Windows.");
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.CreateSubKey(subKey, writable: true);
            if (key is null)
            {
                return OperationResult.Fail($"Could not open {Describe(hive, subKey)}.");
            }

            key.SetValue(name, value, kind);
            return OperationResult.Ok($"{Describe(hive, subKey)}\\{name} set.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not write {Describe(hive, subKey)}\\{name}: {ex.Message}", ex);
        }
    }

    public static OperationResult DeleteValue(RegistryHive hive, string subKey, string name)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Registry changes are only possible on Windows.");
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
            return OperationResult.Ok($"{Describe(hive, subKey)}\\{name} removed.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not remove {Describe(hive, subKey)}\\{name}: {ex.Message}", ex);
        }
    }

    public static string Describe(RegistryHive hive, string subKey) => hive switch
    {
        RegistryHive.LocalMachine => "HKLM\\" + subKey,
        RegistryHive.CurrentUser => "HKCU\\" + subKey,
        RegistryHive.ClassesRoot => "HKCR\\" + subKey,
        RegistryHive.Users => "HKU\\" + subKey,
        _ => hive + "\\" + subKey,
    };
}
