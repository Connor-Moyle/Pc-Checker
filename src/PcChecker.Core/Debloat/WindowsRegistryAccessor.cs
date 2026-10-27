using Microsoft.Win32;
using PcChecker.Core.Common;

namespace PcChecker.Core.Debloat;

/// <summary>
/// <see cref="IRegistryAccessor"/> on the real registry, always through the 64-bit view so HKLM\SOFTWARE is not
/// redirected for this 64-bit app. Reads never throw (they return null); writes throw so the engine can report the
/// exact failure (for example "access denied" on settings Windows protects).
/// </summary>
public sealed class WindowsRegistryAccessor : IRegistryAccessor
{
    public RegistryData? Read(DebloatHive hive, string path, string name)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            using var root = Open(hive);
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null)
            {
                return null;
            }

            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
            {
                return null;
            }

            return key.GetValueKind(name) switch
            {
                RegistryValueKind.DWord => new RegistryData(DebloatValueKind.DWord, unchecked((uint)(int)value).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                RegistryValueKind.QWord => new RegistryData(DebloatValueKind.DWord, ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                RegistryValueKind.Binary => new RegistryData(DebloatValueKind.Binary, RegistryValueCodec.ToHex((byte[])value)),
                RegistryValueKind.MultiString => new RegistryData(DebloatValueKind.String, string.Join("\n", (string[])value)),
                _ => new RegistryData(DebloatValueKind.String, value.ToString() ?? string.Empty),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool KeyExists(DebloatHive hive, string path)
    {
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        try
        {
            using var root = Open(hive);
            using var key = root.OpenSubKey(path, writable: false);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(DebloatHive hive, string path)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        try
        {
            using var root = Open(hive);
            using var key = root.OpenSubKey(path, writable: false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public void Write(DebloatHive hive, string path, string name, RegistryData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        PlatformGuard.ThrowIfNotWindows("Registry changes");
        using var root = Open(hive);
        using var key = root.CreateSubKey(path, writable: true) ?? throw new IOException($"Could not open {RegistryValueCodec.HiveName(hive)}\\{path}.");
        switch (data.Kind)
        {
            case DebloatValueKind.DWord:
                if (!RegistryValueCodec.TryParseDword(data.Value, out uint dword))
                {
                    throw new FormatException($"\"{data.Value}\" is not a valid DWORD.");
                }

                key.SetValue(name, unchecked((int)dword), RegistryValueKind.DWord);
                break;
            case DebloatValueKind.Binary:
                key.SetValue(name, RegistryValueCodec.ParseHex(data.Value) ?? throw new FormatException($"\"{data.Value}\" is not valid hex."), RegistryValueKind.Binary);
                break;
            default:
                key.SetValue(name, data.Value, RegistryValueKind.String);
                break;
        }
    }

    public void DeleteValue(DebloatHive hive, string path, string name)
    {
        PlatformGuard.ThrowIfNotWindows("Registry changes");
        using var root = Open(hive);
        using var key = root.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteKeyTree(DebloatHive hive, string path)
    {
        PlatformGuard.ThrowIfNotWindows("Registry changes");
        using var root = Open(hive);
        root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    private static RegistryKey Open(DebloatHive hive) =>
        RegistryKey.OpenBaseKey(hive == DebloatHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
}
