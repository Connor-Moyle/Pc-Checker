using System.Globalization;

namespace PcChecker.Core.Software;

/// <summary>
/// Raw values of one <c>Uninstall\{key}</c> registry entry, decoupled from the registry API so the mapping is testable.
/// Values are the registry value names (DisplayName, DisplayVersion, …) mapped to their raw objects.
/// </summary>
public sealed record UninstallEntry(
    string KeyName,
    string KeyPath,
    SoftwareSource Source,
    DateTime? KeyLastWrite,
    IReadOnlyDictionary<string, object?> Values,
    ReadOnlyMemory<byte> UninstallerHeader = default);

/// <summary>Pure mapping from an <see cref="UninstallEntry"/> to an <see cref="InstalledProgram"/>.</summary>
public static class UninstallEntryMapper
{
    private static readonly string[] UpdateNamePrefixes = ["KB", "Update for ", "Security Update for ", "Hotfix for ", "Cumulative Update", "Servicing Stack Update"];

    /// <summary>
    /// Maps the entry, or returns null when it should be hidden: no display name (unless <paramref name="includeNameless"/>),
    /// or a Windows update / hotfix record.
    /// </summary>
    public static InstalledProgram? Map(UninstallEntry entry, bool includeNameless = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var v = entry.Values;
        var displayName = Str(v, "DisplayName");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            if (!includeNameless)
            {
                return null;
            }

            displayName = entry.KeyName;
        }

        var parentKey = Str(v, "ParentKeyName");
        var releaseType = Str(v, "ReleaseType");
        if (IsWindowsUpdate(displayName, parentKey, releaseType))
        {
            return null;
        }

        var uninstall = Str(v, "UninstallString");
        var quiet = Str(v, "QuietUninstallString");
        var displayIcon = Str(v, "DisplayIcon");
        bool systemComponent = Int(v, "SystemComponent") == 1;
        bool windowsInstaller = Int(v, "WindowsInstaller") == 1;
        var installer = InstallerKindDetector.Detect(uninstall, quiet, windowsInstaller, entry.UninstallerHeader.Span);
        var location = InstallLocationInference.Infer(Str(v, "InstallLocation"), displayIcon, uninstall);
        var installDate = InstallDateParser.Parse(Str(v, "InstallDate")) ?? entry.KeyLastWrite;
        long? size = Int64(v, "EstimatedSize") is { } kb and > 0 ? kb * 1024 : null;
        var source = systemComponent && entry.Source != SoftwareSource.User ? SoftwareSource.SystemComponent : entry.Source;
        var bundledWith = parentKey;
        var iconPath = InstallLocationInference.StripIconIndex(displayIcon);

        return new InstalledProgram(
            Id: BuildId(entry),
            Name: displayName.Trim(),
            Version: NullIfBlank(Str(v, "DisplayVersion")) ?? ComposeVersion(v),
            Publisher: NullIfBlank(Str(v, "Publisher")),
            InstallDate: installDate,
            InstallLocation: location,
            UninstallString: NullIfBlank(uninstall),
            QuietUninstallString: NullIfBlank(quiet),
            SizeBytes: size,
            Source: source,
            Installer: installer,
            IsSystemComponent: systemComponent,
            IsProtected: false,
            ProtectionReason: null,
            RegistryKey: entry.KeyPath,
            PackageFullName: null,
            IconPath: iconPath,
            HelpLink: NullIfBlank(Str(v, "HelpLink")),
            AboutUrl: NullIfBlank(Str(v, "URLInfoAbout")) ?? NullIfBlank(Str(v, "URLUpdateInfo")),
            BundledWithId: NullIfBlank(bundledWith),
            BundledChildIds: []);
    }

    /// <summary>Windows updates and hotfixes are listed under Uninstall too; they belong to Windows Update, not here.</summary>
    public static bool IsWindowsUpdate(string? displayName, string? parentKeyName, string? releaseType)
    {
        if (!string.IsNullOrWhiteSpace(parentKeyName)
            && (string.Equals(releaseType, "Update", StringComparison.OrdinalIgnoreCase)
                || string.Equals(releaseType, "Hotfix", StringComparison.OrdinalIgnoreCase)
                || string.Equals(releaseType, "Security Update", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        var name = displayName.Trim();
        foreach (var prefix in UpdateNamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                // "KB" must be followed by digits to count as a knowledge-base article.
                if (prefix == "KB")
                {
                    return name.Length > 2 && char.IsDigit(name[2]);
                }

                return true;
            }
        }

        return name.Contains("(KB", StringComparison.OrdinalIgnoreCase) && name.EndsWith(')');
    }

    /// <summary>Stable id: hive prefix + key name, so the same product in two hives stays distinct.</summary>
    public static string BuildId(UninstallEntry entry) => entry.Source switch
    {
        SoftwareSource.Machine64 => "m64:" + entry.KeyName,
        SoftwareSource.Machine32 => "m32:" + entry.KeyName,
        SoftwareSource.User => "usr:" + entry.KeyName,
        _ => entry.Source.ToString().ToLowerInvariant() + ":" + entry.KeyName,
    };

    private static string? ComposeVersion(IReadOnlyDictionary<string, object?> v)
    {
        var major = Int(v, "VersionMajor");
        var minor = Int(v, "VersionMinor");
        if (major is null)
        {
            return null;
        }

        return minor is null ? major.Value.ToString(CultureInfo.InvariantCulture) : $"{major}.{minor}";
    }

    private static string? Str(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string s => s,
            string[] parts => string.Join(' ', parts),
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
        };
    }

    private static int? Int(IReadOnlyDictionary<string, object?> values, string key) => Int64(values, key) is { } l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;

    private static long? Int64(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            int i => i,
            long l => l,
            uint u => u,
            ulong ul => (long)Math.Min(ul, long.MaxValue),
            short s => s,
            byte b => b,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
