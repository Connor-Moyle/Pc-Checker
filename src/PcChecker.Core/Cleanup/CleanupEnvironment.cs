using System.Diagnostics;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Facts about the running machine the scanner and cleaner both need: environment variables plus the values PC Checker
/// works out itself (Steam, Ubisoft Connect, crash dump folders, fixed drives), which other user profiles may be
/// included (elevated only), the folders PC Checker itself writes to, the preconditions (pending restart, installs)
/// and a guarded directory lister.
/// </summary>
internal sealed class CleanupEnvironment
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly Dictionary<string, string?> _computed;
    private readonly Dictionary<string, IReadOnlyList<string>> _multi;

    private CleanupEnvironment(bool isElevated, IReadOnlyList<string> otherUserProfiles, IReadOnlyList<string> protectedDirectories,
        Dictionary<string, string?> computed, Dictionary<string, IReadOnlyList<string>> multi)
    {
        IsElevated = isElevated;
        OtherUserProfiles = otherUserProfiles;
        ProtectedDirectories = protectedDirectories;
        _computed = computed;
        _multi = multi;
    }

    public bool IsElevated { get; }

    /// <summary>Profile folders of other accounts (empty unless elevated).</summary>
    public IReadOnlyList<string> OtherUserProfiles { get; }

    /// <summary>Folders never cleaned: the app's own log and settings directories.</summary>
    public IReadOnlyList<string> ProtectedDirectories { get; }

    public static CleanupEnvironment Capture(IEnumerable<string?> protectedDirectories)
    {
        bool elevated = CleanupNativeMethods.IsElevated();
        var protectedList = protectedDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!).ToList();
        var steam = Guard(() => CleanupRootRules.NormalizeSteamPath(RegistryString(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath")));
        var computed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["STEAM"] = steam,
            ["UBISOFT"] = Guard(FindUbisoft),
            ["DUMPFILEDIR"] = Guard(() => CleanupRootRules.DumpFolder(RegistryString(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\CrashControl", "DumpFile"), true, Environment.ExpandEnvironmentVariables)),
            ["MINIDUMPDIR"] = Guard(() => CleanupRootRules.DumpFolder(RegistryString(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\CrashControl", "MinidumpDir"), false, Environment.ExpandEnvironmentVariables)),
        };
        var multi = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["STEAMLIBRARIES"] = Guard(() => SteamLibraries(steam)) ?? [],
            ["FIXEDDRIVES"] = Guard(FixedDrives) ?? [],
        };
        return new CleanupEnvironment(elevated, elevated ? FindOtherUserProfiles() : [], protectedList, computed, multi);
    }

    /// <summary>Environment variables first, then the computed values. "PROGRAMFILES(X86)" comes from the environment.</summary>
    public string? GetVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        return _computed.TryGetValue(name, out var computed) ? computed : null;
    }

    public IReadOnlyList<string> GetMultiValues(string name) => _multi.TryGetValue(name, out var values) ? values : [];

    /// <summary>Child folder names of <paramref name="parent"/>; empty when it does not exist or is inaccessible.</summary>
    public static IEnumerable<string> ListDirectoryNames(string parent)
    {
        try
        {
            if (!Directory.Exists(parent))
            {
                return [];
            }

            return Directory.EnumerateDirectories(parent, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Resolves a category's folders and adds the exclusions only known at run time (see <see cref="DynamicExclusions"/>).</summary>
    public ResolvedRoots Resolve(CleanupCategory category)
    {
        var resolved = CleanupPathResolver.Resolve(category, GetVariable, OtherUserProfiles, ListDirectoryNames, GetMultiValues, IsElevated);
        var dynamic = DynamicExclusions(category, resolved);
        if (dynamic.Count == 0)
        {
            return resolved;
        }

        var excluded = resolved.ExcludedPaths.Concat(dynamic).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var entries = resolved.Entries.Where(e => !excluded.Any(x => CleanupRules.IsUnderDirectory(e.Path, x))).ToList();
        return new ResolvedRoots(entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), resolved.UsersScanned)
        {
            Entries = entries,
            ExcludedPaths = excluded,
        };
    }

    public bool IsProtected(string path) => ProtectedDirectories.Any(d => CleanupRules.IsUnderDirectory(path, d));

    /// <summary>
    /// Folders only known at run time that a category must leave alone: under C:\AMD every folder an installed
    /// program's uninstall entry points to (newer chipset drivers keep their uninstaller there), and in Steam's
    /// downloading folders every download Steam still has queued or paused.
    /// </summary>
    private static List<string> DynamicExclusions(CleanupCategory category, ResolvedRoots resolved)
    {
        var result = new List<string>();
        if (category.Id.Equals("driver.leftovers.amd", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var root in resolved.Roots)
            {
                var values = UninstallValues();
                if (values is null)
                {
                    // Could not read the uninstall entries: keep the newest chipset package at least.
                    var newest = CleanupRootRules.NewestChipsetFolder(ListDirectoryNames(root).Select(n => Path.Combine(root, n)).Select(p => (p, SafeLastWrite(p))));
                    if (newest is not null)
                    {
                        result.Add(newest);
                    }

                    continue;
                }

                result.AddRange(CleanupRootRules.FoldersReferencedUnder(root, values));
            }
        }
        else if (category.Id.Equals("game.steam.downloading", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var root in resolved.Roots)
            {
                var steamapps = Path.GetDirectoryName(root.TrimEnd('\\'));
                if (steamapps is null)
                {
                    continue;
                }

                var manifests = Guard(() => Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList()) ?? [];
                result.AddRange(CleanupRootRules.ActiveSteamDownloads(root, ListDirectoryNames(root), manifests));
            }
        }

        return result;
    }

    /// <summary>
    /// Reads what decides whether a category may run now: elevation, a pending restart (servicing or Windows Update),
    /// file renames waiting for a restart, a running MSI install and TrustedInstaller at work.
    /// </summary>
    public CleanupConditions ReadConditions()
    {
        if (!PlatformGuard.IsWindows)
        {
            return new CleanupConditions(false, false, false, false, false);
        }

        bool pendingReboot =
            KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
            || KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        bool renames = Guard(() =>
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager", writable: false);
            return key?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 } list && list.Any(s => !string.IsNullOrWhiteSpace(s));
        });
        var running = RunningProcesses();
        bool installer = KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress")
            || running.Any(p => p.Name.Equals("msiexec", StringComparison.OrdinalIgnoreCase));
        bool updating = IsRunning("TiWorker");
        return new CleanupConditions(IsElevated, pendingReboot, installer, renames, updating)
        {
            LastFeatureUpdate = Guard(LastFeatureUpdate),
        };
    }

    /// <summary>Running processes any app rule cares about, with their image paths when readable.</summary>
    public static IReadOnlyList<RunningProcess> RunningProcesses()
    {
        var result = new List<RunningProcess>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return result;
        }

        foreach (var p in processes)
        {
            try
            {
                if (CleanupRules.IsWatchedProcess(p.ProcessName))
                {
                    string? path = null;
                    try
                    {
                        path = ProcessNative.ReadIdentity(p.Id).Path;
                    }
                    catch
                    {
                        // An unreadable path still counts as the app running.
                    }

                    result.Add(new RunningProcess(p.ProcessName, path));
                }
            }
            catch
            {
                // The process exited while we looked.
            }
            finally
            {
                p.Dispose();
            }
        }

        return result;
    }

    private static bool IsRunning(string name)
    {
        try
        {
            var found = Process.GetProcessesByName(name);
            bool any = found.Length > 0;
            foreach (var p in found)
            {
                p.Dispose();
            }

            return any;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> SteamLibraries(string? steam)
    {
        if (steam is null)
        {
            return [];
        }

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var libraries = File.Exists(vdf) ? CleanupRootRules.ParseSteamLibraries(File.ReadAllText(vdf)).ToList() : [];
        if (!libraries.Contains(steam, StringComparer.OrdinalIgnoreCase))
        {
            libraries.Insert(0, steam);
        }

        return libraries;
    }

    private static string? FindUbisoft()
    {
        var installDir = CleanupRootRules.NormalizeWindowsPath(RegistryString(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir"));
        if (CleanupPathResolver.IsAcceptableRoot(installDir) && Directory.Exists(installDir))
        {
            return installDir;
        }

        var x86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        var fallback = x86 is null ? null : Path.Combine(x86, "Ubisoft", "Ubisoft Game Launcher");
        return fallback is not null && Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>Roots of fixed (internal) drives only: never network, removable or optical drives.</summary>
    private static IReadOnlyList<string> FixedDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .Where(r => r.Length >= 2 && char.IsAsciiLetter(r[0]) && r[1] == ':')
            .ToList();

    private static DateTime? LastFeatureUpdate()
    {
        // InstallDate (seconds since 1970, UTC) is reset by every feature update.
        var value = RegistryTweaksValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallDate");
        return value is int seconds && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((uint)seconds).LocalDateTime : null;
    }

    private static List<string?>? UninstallValues()
    {
        try
        {
            var values = new List<string?>();
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var path in new[] { UninstallKey, UninstallKey32 })
            {
                using var key = hklm.OpenSubKey(path, writable: false);
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetSubKeyNames())
                {
                    using var entry = key.OpenSubKey(name, writable: false);
                    if (entry is null)
                    {
                        continue;
                    }

                    foreach (var valueName in new[] { "UninstallString", "QuietUninstallString", "InstallSource", "InstallLocation", "ModifyPath" })
                    {
                        values.Add(entry.GetValue(valueName) as string);
                    }
                }
            }

            return values;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try
        {
            return Directory.GetLastWriteTime(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string? RegistryString(RegistryHive hive, string subKey, string name)
    {
        var value = RegistryTweaksValue(hive, subKey, name);
        return value as string;
    }

    private static object? RegistryTweaksValue(RegistryHive hive, string subKey, string name)
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

    private static bool KeyExists(RegistryHive hive, string subKey)
    {
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

    private static T? Guard<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return default;
        }
    }

    private static IReadOnlyList<string> FindOtherUserProfiles()
    {
        try
        {
            var current = Environment.GetEnvironmentVariable("USERPROFILE");
            var usersRoot = current is null ? null : Path.GetDirectoryName(current.TrimEnd('\\'));
            if (string.IsNullOrEmpty(usersRoot) || !Directory.Exists(usersRoot))
            {
                return [];
            }

            return Directory.EnumerateDirectories(usersRoot, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(d => !CleanupRules.IsUnderDirectory(d, current) && CleanupPathResolver.IsRealUserProfile(Path.GetFileName(d)))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
