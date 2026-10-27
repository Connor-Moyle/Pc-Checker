namespace PcChecker.Core.Leftovers;

/// <summary>
/// Folders and registry keys that are never reported, whatever the matching says: Windows and Microsoft folders,
/// shared component folders (Common Files, Package Cache, Store packages), hardware vendors whose drivers install
/// shared bits without an entry in Installed apps (NVIDIA, Intel, AMD, Realtek…), game libraries, developer caches,
/// GUID-named installer folders, hidden dot-folders and PC Checker's own folders. Pure.
/// </summary>
public static class LeftoverAllowList
{
    /// <summary>Never reported in any root.</summary>
    private static readonly HashSet<string> Everywhere = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows and Microsoft.
        "Microsoft", "Windows", "Common Files", "Internet Explorer", "Reference Assemblies", "MSBuild", "dotnet", "IIS",
        "IIS Express", "ModifiableWindowsApps", "WindowsApps", "WindowsPowerShell", "Uninstall Information", "PowerShell",
        "Package Cache", "Packages", "PackageManagement", "regid.1991-06.com.microsoft", "ssh", "USOPrivate", "USOShared",
        "SoftwareDistribution", "Comms", "ConnectedDevicesPlatform", "PeerDistRepub", "Application Data", "Desktop",
        "Documents", "Start Menu", "Templates", "Favorites", "Temp", "Programs", "CrashDumps", "D3DSCache", "VirtualStore",
        "History", "IsolatedStorage", "Publishers", "PlaceholderTileLogoFolder", "ElevatedDiagnostics", "GroupPolicy",
        "Diagnostics", "Identities", "Media Center Programs", "Sun", "Oracle", "Java", "Macromedia",
        "InstallShield Installation Information", "InstallShield", "Installer", "CEF", "Downloaded Program Files",
        // Hardware vendors: drivers and control panels install shared parts with no entry of their own.
        "NVIDIA", "NVIDIA Corporation", "Intel", "Intel Corporation", "AMD", "ATI", "ATI Technologies", "Realtek",
        "Dolby", "DTS", "Waves", "Synaptics", "ELAN", "Conexant", "Broadcom", "Qualcomm", "MediaTek", "Killer Networking",
        "Thunderbolt", "IntelGraphicsProfiles", "NVIDIA GPU Computing Toolkit",
        // Game libraries and stores: games live here whether or not they have an uninstall entry.
        "Steam", "SteamLibrary", "Epic Games", "EpicGamesLauncher", "GOG Galaxy", "GOG Games", "Origin Games", "EA Games",
        "Ubisoft", "Rockstar Games", "XboxGames", "Unity", "Unreal Engine", "UnrealEngine",
        // Developer tools and package caches that keep data without an uninstall entry.
        "npm", "npm-cache", "pip", "pypa", "pypoetry", "NuGet", "Yarn", "pnpm", "go-build", "node-gyp", "fontconfig",
        "chocolatey", "scoop", "SquirrelTemp", "Squirrel",
        // PC Checker itself.
        "PcChecker", "PC Checker",
    };

    /// <summary>Additional names that are left alone only in the given root.</summary>
    private static readonly Dictionary<LeftoverRoot, HashSet<string>> PerRoot = new()
    {
        [LeftoverRoot.ProgramData] = new(StringComparer.OrdinalIgnoreCase) { "Windows Defender", "WindowsHolographicDevices", "dbg", "Setup" },
        [LeftoverRoot.LocalAppData] = new(StringComparer.OrdinalIgnoreCase) { "Downloaded Installations", "Low", "assembly" },
        [LeftoverRoot.RoamingAppData] = new(StringComparer.OrdinalIgnoreCase) { "Adobe", "Mozilla" },
        [LeftoverRoot.LocalLowAppData] = new(StringComparer.OrdinalIgnoreCase) { "Mozilla", "Adobe" },
    };

    /// <summary>
    /// Names of sub-folders and sub-keys that are shared within a vendor's folder ("Common", "Updater", "Logs"): they
    /// are never reported one level down, because another product of the same vendor may use them.
    /// </summary>
    private static readonly HashSet<string> SharedInsideVendor = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common", "Common Files", "Shared", "SharedFiles", "Update", "Updater", "Updates", "Installer", "Installers",
        "Setup", "Logs", "Log", "Cache", "Temp", "Tmp", "Data", "Downloads", "Crashpad", "CrashReports", "Crash Reports",
        "Licenses", "License", "Settings", "Config", "Capabilities", "Plugins", "Tools", "Bin", "Runtime", "Uninstall",
        "Uninstaller", "Service", "Services", "Drivers", "Driver",
    };

    /// <summary>Registry vendor keys under SOFTWARE that are never reported.</summary>
    private static readonly HashSet<string> RegistryVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Classes", "Clients", "DefaultUserEnvironment", "Description", "Policies", "RegisteredApplications", "WOW6432Node",
        "ODBC", "Microsoft", "Windows", "Partner", "Setup", "System", "Khronos", "OpenSSH", "AppDataLow",
        "MozillaPlugins", "JavaSoft", "Macromedia", "Oracle", "Sun", "Intel", "NVIDIA Corporation", "NVIDIA", "AMD",
        "ATI", "ATI Technologies", "Realtek", "Dolby", "DTS", "Waves", "Synaptics", "Elantech", "Conexant", "Broadcom",
        "Qualcomm", "MediaTek", "Chromium", "PcChecker", "Volatile Environment", "Unity", "Unity Technologies",
        "dotnet", "CVSM", "OEM", "SyncIntegrationClients", "SyncEngines", "IM Providers", "Sysinternals", "InstallShield",
        "Installer", "Netscape",
    };

    /// <summary>
    /// True when a folder directly in <paramref name="root"/> must never be reported: allow-listed by name, starting with
    /// "Microsoft" or "Windows", a hidden dot-folder, or a GUID-like installer folder ("{…}").
    /// </summary>
    public static bool IsAllowedFolder(LeftoverRoot root, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var n = name.Trim();
        if (Everywhere.Contains(n) || (PerRoot.TryGetValue(root, out var extra) && extra.Contains(n)))
        {
            return true;
        }

        return StartsWithSystemPrefix(n) || n.StartsWith('.') || n.StartsWith('{') || n.StartsWith('$') || IsGuid(n);
    }

    /// <summary>True when a sub-folder one level inside a vendor folder must never be reported on its own.</summary>
    public static bool IsSharedInsideVendor(string? name) =>
        string.IsNullOrWhiteSpace(name) || SharedInsideVendor.Contains(name.Trim()) || name.StartsWith('.') || name.StartsWith('{') || IsGuid(name.Trim()) ||
        LeftoverNameMatcher.IsGenericName(name);

    /// <summary>True when a vendor key under SOFTWARE (or Software in HKCU) must never be reported.</summary>
    public static bool IsAllowedRegistryVendor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var n = name.Trim();
        return RegistryVendors.Contains(n) || StartsWithSystemPrefix(n) || n.StartsWith('.') || n.StartsWith('{') || IsGuid(n);
    }

    private static bool StartsWithSystemPrefix(string name) =>
        name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase);

    private static bool IsGuid(string name) => Guid.TryParse(name.Trim('{', '}'), out _);
}
