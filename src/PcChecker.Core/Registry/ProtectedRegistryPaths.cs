namespace PcChecker.Core.RegistryHealth;

/// <summary>
/// Registry locations PC Checker never changes, whatever a scan says: system hives, security and sign-in settings,
/// policies, boot and driver configuration, Windows Defender, and the container keys the checks enumerate. Pure.
/// </summary>
public static class ProtectedRegistryPaths
{
    /// <summary>HKLM top-level keys that are off limits (services are only ever reported, never fixed).</summary>
    private static readonly Dictionary<string, string> ProtectedMachineRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SYSTEM"] = "Windows system configuration (services, drivers, boot) is never changed by the registry fixer.",
        ["SAM"] = "The account database is never touched.",
        ["SECURITY"] = "Security settings are never touched.",
        ["HARDWARE"] = "Hardware information is rebuilt by Windows at every boot and is never touched.",
        ["BCD00000000"] = "Boot configuration is never touched.",
    };

    /// <summary>Key segment sequences that are protected wherever they appear.</summary>
    private static readonly (string[] Segments, string Reason)[] ProtectedSequences =
    [
        (["Policies"], "Group Policy and administrator settings are never changed."),
        (["Winlogon"], "Sign-in (Winlogon) settings are never changed."),
        (["Lsa"], "Local Security Authority settings are never changed."),
        (["CurrentControlSet", "Control"], "Core system control settings are never changed."),
        (["Session Manager"], "Session Manager (including BootExecute) is never changed."),
        (["SafeBoot"], "Safe mode configuration is never changed."),
        (["Drivers"], "Driver registrations are never changed."),
        (["Drivers32"], "Multimedia driver registrations are never changed."),
        (["drivers.desc"], "Driver descriptions are never changed."),
        (["Windows Defender"], "Windows Security (Defender) settings are never changed."),
        (["Windows Defender Security Center"], "Windows Security settings are never changed."),
        (["Microsoft Antimalware"], "Antimalware settings are never changed."),
        (["Windows NT", "CurrentVersion", "Image File Execution Options"], "Program launch (debugger) settings are never changed."),
        (["Windows NT", "CurrentVersion", "Windows"], "Core Windows loader settings (AppInit) are never changed."),
        (["Windows NT", "CurrentVersion", "SystemRestore"], "System Restore settings are never changed."),
        (["Explorer", "Shell Folders"], "The locations of your personal folders are never changed."),
        (["Explorer", "User Shell Folders"], "The locations of your personal folders are never changed."),
        (["Explorer", "ShellExecuteHooks"], "Shell hooks are never changed."),
        (["Component Based Servicing"], "Windows update servicing data is never changed."),
        (["Appx"], "Store app registrations are managed by Windows."),
    ];

    /// <summary>
    /// The keys the checks enumerate. Values inside them may be removed, and sub-keys below them may be removed, but
    /// the containers themselves never are. Paths are compared without <c>WOW6432Node</c>.
    /// </summary>
    public static IReadOnlyList<string> ContainerKeys { get; } =
    [
        @"SOFTWARE",
        @"SOFTWARE\Classes",
        @"SOFTWARE\Classes\CLSID",
        @"SOFTWARE\Classes\*",
        @"SOFTWARE\Classes\*\shellex",
        @"SOFTWARE\Classes\*\shellex\ContextMenuHandlers",
        @"SOFTWARE\Classes\Directory\shellex\ContextMenuHandlers",
        @"SOFTWARE\Classes\Directory\Background\shellex\ContextMenuHandlers",
        @"SOFTWARE\Classes\Folder\shellex\ContextMenuHandlers",
        @"SOFTWARE\Classes\Drive\shellex\ContextMenuHandlers",
        @"SOFTWARE\Classes\AllFilesystemObjects\shellex\ContextMenuHandlers",
        @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
        @"SOFTWARE\Microsoft",
        @"SOFTWARE\Microsoft\Windows",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FileExts",
        @"SOFTWARE\Microsoft\Windows\Help",
        @"SOFTWARE\Microsoft\Windows\HTML Help",
        @"SOFTWARE\Microsoft\Windows NT",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts",
    ];

    /// <summary>True when nothing at this key (the key itself or its values) may be changed.</summary>
    public static bool IsProtected(string hive, string keyPath) => Reason(hive, keyPath) is not null;

    /// <summary>The plain-English reason a key is protected, or null when it may be changed.</summary>
    public static string? Reason(string hive, string keyPath)
    {
        string root = RegistryPathTools.NormalizeHive(hive);
        if (root is not ("HKLM" or "HKCU" or "HKCR"))
        {
            return "Only HKEY_LOCAL_MACHINE, HKEY_CURRENT_USER and HKEY_CLASSES_ROOT entries are ever changed.";
        }

        var segments = Segments(keyPath);
        if (segments.Length == 0)
        {
            return "A registry hive itself is never changed.";
        }

        if (root == "HKLM" && ProtectedMachineRoots.TryGetValue(segments[0], out var machineReason))
        {
            return machineReason;
        }

        foreach (var (sequence, reason) in ProtectedSequences)
        {
            if (ContainsSequence(segments, sequence))
            {
                return reason;
            }
        }

        if (segments.Any(s => s.StartsWith("ControlSet0", StringComparison.OrdinalIgnoreCase)))
        {
            return "Core system control settings are never changed.";
        }

        return null;
    }

    /// <summary>True when a value inside this key may be deleted.</summary>
    public static bool CanDeleteValue(string hive, string keyPath, string? valueName) =>
        valueName is not null && !IsProtected(hive, keyPath);

    /// <summary>True when the whole key may be deleted: not protected, not a scanned container and at least three levels deep.</summary>
    public static bool CanDeleteKey(string hive, string keyPath)
    {
        if (IsProtected(hive, keyPath))
        {
            return false;
        }

        var segments = Segments(keyPath);
        if (segments.Length < 3 || IsContainer(keyPath))
        {
            return false;
        }

        // HKCU paths use "Software"; HKCR paths are relative to Classes. Compare both spellings.
        return !(RegistryPathTools.NormalizeHive(hive) == "HKCR" && IsContainer(@"SOFTWARE\Classes\" + string.Join('\\', segments)));
    }

    /// <summary>True when the key is one of <see cref="ContainerKeys"/> (ignoring WOW6432Node and case).</summary>
    public static bool IsContainer(string keyPath)
    {
        string normalized = string.Join('\\', Segments(keyPath).Where(s => !s.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)));
        return ContainerKeys.Any(c => c.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] Segments(string? keyPath) =>
        (keyPath ?? string.Empty).Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ContainsSequence(string[] segments, string[] sequence)
    {
        for (int i = 0; i + sequence.Length <= segments.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < sequence.Length; j++)
            {
                if (!segments[i + j].Equals(sequence[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
