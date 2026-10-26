using Microsoft.Win32;

namespace PcChecker.Core.Software;

/// <summary>
/// Enumerates the three <c>Uninstall</c> registry hives (64-bit machine, 32-bit machine, current user) into
/// <see cref="UninstallEntry"/> records. Every registry call is guarded; problems become warnings.
/// </summary>
public static class RegistryUninstallReader
{
    public const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Wow6432UninstallSubKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private const int UninstallerHeaderBytes = 64 * 1024;

    private static readonly string[] ValueNames =
    [
        "DisplayName", "DisplayVersion", "Publisher", "InstallDate", "InstallLocation", "InstallSource", "UninstallString",
        "QuietUninstallString", "EstimatedSize", "SystemComponent", "WindowsInstaller", "DisplayIcon", "HelpLink", "URLInfoAbout",
        "URLUpdateInfo", "ParentKeyName", "ParentDisplayName", "ReleaseType", "VersionMajor", "VersionMinor",
    ];

    /// <summary>The hives to read, in priority order.</summary>
    public static IReadOnlyList<(RegistryHive Hive, RegistryView View, string SubKey, SoftwareSource Source, string Label)> Hives { get; } =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, UninstallSubKey, SoftwareSource.Machine64, @"HKLM\" + UninstallSubKey),
        (RegistryHive.LocalMachine, RegistryView.Registry64, Wow6432UninstallSubKey, SoftwareSource.Machine32, @"HKLM\" + Wow6432UninstallSubKey),
        (RegistryHive.CurrentUser, RegistryView.Default, UninstallSubKey, SoftwareSource.User, @"HKCU\" + UninstallSubKey),
    ];

    /// <summary>Reads one hive. Returns the raw entries; <paramref name="warnings"/> receives any problems.</summary>
    public static IReadOnlyList<UninstallEntry> ReadHive(RegistryHive hive, RegistryView view, string subKey, SoftwareSource source, string label,
        bool readUninstallerHeaders, ICollection<string> warnings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        var entries = new List<UninstallEntry>();
        if (!OperatingSystem.IsWindows())
        {
            warnings.Add("The registry can only be read on Windows.");
            return entries;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(subKey, writable: false);
            if (root is null)
            {
                return entries;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var key = root.OpenSubKey(name, writable: false);
                    if (key is null)
                    {
                        continue;
                    }

                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var valueName in ValueNames)
                    {
                        var raw = key.GetValue(valueName, null, RegistryValueOptions.None);
                        if (raw is not null)
                        {
                            values[valueName] = raw;
                        }
                    }

                    if (values.Count == 0)
                    {
                        continue;
                    }

                    var header = readUninstallerHeaders ? ReadUninstallerHeader(values) : ReadOnlyMemory<byte>.Empty;
                    entries.Add(new UninstallEntry(name, label + "\\" + name, source, RegistryKeyTimestamp.TryGetLastWriteTime(key), values, header));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not read {label}\\{name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not open {label}: {ex.Message}");
        }

        return entries;
    }

    /// <summary>True when the registry key still exists (used after an uninstall to confirm the program is gone).</summary>
    public static bool KeyExists(string? registryKeyPath)
    {
        if (string.IsNullOrWhiteSpace(registryKeyPath) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var (hive, view, path) = SplitPath(registryKeyPath);
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(path, writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"HKLM\SOFTWARE\..." → (LocalMachine, Registry64, "SOFTWARE\...").</summary>
    public static (RegistryHive Hive, RegistryView View, string Path) SplitPath(string registryKeyPath)
    {
        var text = registryKeyPath.Trim();
        int cut = text.IndexOf('\\');
        var prefix = cut < 0 ? text : text[..cut];
        var rest = cut < 0 ? string.Empty : text[(cut + 1)..];
        var hive = prefix.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => RegistryHive.LocalMachine,
        };
        return (hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default, rest);
    }

    private static ReadOnlyMemory<byte> ReadUninstallerHeader(Dictionary<string, object?> values)
    {
        if (values.TryGetValue("WindowsInstaller", out var wi) && wi is int and 1)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var command = values.GetValueOrDefault("UninstallString") as string;
        var lower = command?.ToLowerInvariant() ?? string.Empty;
        if (!(lower.Contains("uninst.exe") || lower.Contains("uninstall.exe") || lower.Contains("uninstaller.exe")))
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var split = UninstallCommandBuilder.SplitCommand(command);
        if (split is null)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        try
        {
            var file = split.Value.FileName.Trim('"');
            if (!File.Exists(file))
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(stream.Length, UninstallerHeaderBytes)];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return new ReadOnlyMemory<byte>(buffer, 0, read);
        }
        catch
        {
            return ReadOnlyMemory<byte>.Empty;
        }
    }
}
