using System.Collections.Concurrent;
using System.Security;
using Microsoft.Win32;

namespace PcChecker.Core.RegistryHealth;

/// <summary>
/// Thin, exception-safe wrapper over <see cref="Microsoft.Win32.Registry"/> for the Windows registry checks. Always uses
/// the 64-bit view; 32-bit entries are addressed explicitly through their <c>WOW6432Node</c> paths so the same path
/// works for reading, <c>reg export</c> and deleting.
/// </summary>
internal sealed class RegistryAccess
{
    private readonly ConcurrentDictionary<string, byte> _keysSeen = new(StringComparer.OrdinalIgnoreCase);
    private int _accessDenied;

    /// <summary>Keys that could not be read because Windows refused access.</summary>
    public int AccessDenied => _accessDenied;

    /// <summary>
    /// Distinct keys opened so far (the "keys checked" figure). A key the checks open several times (once per value it
    /// holds, again right before a fix) is counted once.
    /// </summary>
    public int KeysOpened => _keysSeen.Count;

    public RegistryKey? Open(string hive, string path, bool writable = false)
    {
        try
        {
            using var root = OpenBase(hive);
            var key = root?.OpenSubKey(path, writable);
            if (key is not null)
            {
                Count(key);
            }

            return key;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _accessDenied);
            return null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or ObjectDisposedException)
        {
            return null;
        }
    }

    public RegistryKey? OpenChild(RegistryKey parent, string name)
    {
        try
        {
            var key = parent.OpenSubKey(name, writable: false);
            if (key is not null)
            {
                Count(key);
            }

            return key;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _accessDenied);
            return null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or ObjectDisposedException)
        {
            return null;
        }
    }

    public bool KeyExists(string hive, string path)
    {
        using var key = Open(hive, path);
        return key is not null;
    }

    public static string[] SubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch
        {
            return [];
        }
    }

    public static string[] ValueNames(RegistryKey key)
    {
        try
        {
            return key.GetValueNames();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The raw value (environment variables not expanded) or null.</summary>
    public static object? Raw(RegistryKey key, string? name)
    {
        try
        {
            return key.GetValue(name ?? string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A string value (REG_SZ / REG_EXPAND_SZ unexpanded; first entry of a REG_MULTI_SZ), trimmed, or null.</summary>
    public static string? Text(RegistryKey key, string? name) => Raw(key, name) switch
    {
        string s when !string.IsNullOrWhiteSpace(s) => s.Trim().TrimEnd('\0'),
        string[] { Length: > 0 } a => a[0],
        _ => null,
    };

    public static int? DWord(RegistryKey key, string name) => Raw(key, name) switch
    {
        int i => i,
        long l => unchecked((int)l),
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => null,
    };

    public static RegValueType Kind(RegistryKey key, string? name)
    {
        try
        {
            return key.GetValueKind(name ?? string.Empty) switch
            {
                RegistryValueKind.String => RegValueType.String,
                RegistryValueKind.ExpandString => RegValueType.ExpandString,
                RegistryValueKind.MultiString => RegValueType.MultiString,
                RegistryValueKind.DWord => RegValueType.DWord,
                RegistryValueKind.QWord => RegValueType.QWord,
                RegistryValueKind.Binary => RegValueType.Binary,
                _ => RegValueType.None,
            };
        }
        catch
        {
            return RegValueType.None;
        }
    }

    /// <summary>Text form of a value for "has it changed?" comparisons.</summary>
    public static string? Fingerprint(RegistryKey key, string? name) => Raw(key, name) switch
    {
        null => null,
        string s => "s:" + s,
        string[] a => "m:" + string.Join('\u001f', a),
        byte[] b => "b:" + Convert.ToHexString(b),
        var other => "v:" + other,
    };

    /// <summary>Reads a key and all its sub-keys into .reg sections (used when <c>reg export</c> is unavailable).</summary>
    public List<RegExportKey> ReadTree(string hive, string path, int maxKeys = 5000)
    {
        var sections = new List<RegExportKey>();
        using var key = Open(hive, path);
        if (key is not null)
        {
            AddTree(key, RegistryPathTools.FullKeyName(hive, path), sections, maxKeys);
        }

        return sections;
    }

    private void AddTree(RegistryKey key, string fullName, List<RegExportKey> sections, int maxKeys)
    {
        if (sections.Count >= maxKeys)
        {
            throw new InvalidOperationException("The key is too large to back up value by value.");
        }

        var values = ValueNames(key).Select(n => new RegExportValue(n, Kind(key, n), Raw(key, n))).ToList();
        sections.Add(new RegExportKey(fullName, values));
        foreach (var child in SubKeyNames(key))
        {
            using var sub = OpenChild(key, child);
            if (sub is not null)
            {
                AddTree(sub, fullName + "\\" + child, sections, maxKeys);
            }
        }
    }

    private void Count(RegistryKey key)
    {
        try
        {
            _keysSeen.TryAdd(key.Name, 0);
        }
        catch (ObjectDisposedException)
        {
            // Not countable; harmless.
        }
    }

    private static RegistryKey? OpenBase(string hive) => RegistryPathTools.NormalizeHive(hive) switch
    {
        "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        "HKCR" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
        _ => null,
    };
}
