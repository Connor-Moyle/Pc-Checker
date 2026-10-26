using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Performance;

/// <summary>
/// Small, guarded probes the performance analyzer uses to collect facts: WMI classes, registry flags, command-line
/// tools. Every member returns null/empty on failure instead of throwing, and records a warning when asked to.
/// </summary>
public static class WindowsSystemProbe
{
    public const string ExplorerVisualEffects = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
    public const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string GameBar = @"Software\Microsoft\GameBar";
    public const string GameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    public const string BackgroundApps = @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";
    public const string StorageSensePolicy = @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy";
    public const string SessionManagerPower = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    public const string ControlPower = @"SYSTEM\CurrentControlSet\Control\Power";
    public const string WindowsUpdateDetect = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Detect";

    /// <summary>Runs a WMI query and maps each row. Returns an empty list (and a warning) when WMI is unavailable.</summary>
    public static IReadOnlyList<T> Query<T>(string scope, string wql, Func<ManagementBaseObject, T?> map, List<string>? warnings = null, string? what = null)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        var results = new List<T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            using var rows = searcher.Get();
            foreach (ManagementBaseObject row in rows)
            {
                try
                {
                    if (map(row) is { } mapped)
                    {
                        results.Add(mapped);
                    }
                }
                finally
                {
                    row.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            warnings?.Add($"{what ?? wql} could not be read: {ex.Message}");
        }

        return results;
    }

    public static string? Str(ManagementBaseObject row, string name)
    {
        try
        {
            return row[name]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    public static long? Long(ManagementBaseObject row, string name)
    {
        try
        {
            return row[name] is { } v ? Convert.ToInt64(v) : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool? Bool(ManagementBaseObject row, string name)
    {
        try
        {
            return row[name] is { } v ? Convert.ToBoolean(v) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>All Windows services with friendly start modes and the Microsoft/third-party classification.</summary>
    public static IReadOnlyList<ServiceInfo> Services(List<string>? warnings)
    {
        string windir = SafeWindowsDirectory();
        var services = Query(@"root\cimv2",
            "SELECT Name, DisplayName, StartMode, State, PathName, Description, DelayedAutoStart FROM Win32_Service",
            row =>
            {
                var name = Str(row, "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    return null;
                }

                var path = Str(row, "PathName");
                return new ServiceInfo(name, Str(row, "DisplayName") ?? name,
                    ServiceClassifier.NormalizeStartMode(Str(row, "StartMode"), Bool(row, "DelayedAutoStart") ?? false),
                    Str(row, "State") ?? "Unknown", path, ServiceClassifier.IsMicrosoft(path, windir), Str(row, "Description"));
            }, warnings, "The list of services");
        return services.OrderBy(s => s.IsMicrosoft).ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Memory totals from Win32_OperatingSystem (KB values converted to bytes) plus the last boot time.</summary>
    public static (long TotalBytes, long FreeBytes, long CommitLimitBytes, long CommitFreeBytes, DateTime? LastBoot)? OperatingSystem(List<string>? warnings)
    {
        var rows = Query(@"root\cimv2",
            "SELECT TotalVisibleMemorySize, FreePhysicalMemory, TotalVirtualMemorySize, FreeVirtualMemory, LastBootUpTime FROM Win32_OperatingSystem",
            row =>
            {
                DateTime? boot = null;
                try
                {
                    if (Str(row, "LastBootUpTime") is { } dmtf)
                    {
                        boot = ManagementDateTimeConverter.ToDateTime(dmtf);
                    }
                }
                catch
                {
                    // Keep going without the boot time.
                }

                return ((Long(row, "TotalVisibleMemorySize") ?? 0) * 1024, (Long(row, "FreePhysicalMemory") ?? 0) * 1024,
                    (Long(row, "TotalVirtualMemorySize") ?? 0) * 1024, (Long(row, "FreeVirtualMemory") ?? 0) * 1024, boot);
            }, warnings, "Memory information");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Fixed drives with free/total bytes and media type (HDD/SSD) joined from the Storage namespace.</summary>
    public static IReadOnlyList<(string Drive, long Free, long Total, string MediaType)> Drives(List<string>? warnings)
    {
        var mediaByLetter = new Dictionary<char, string>();
        if (PlatformGuard.IsWindows)
        {
            var disks = Query(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk",
                row => (Id: Str(row, "DeviceId") ?? string.Empty, Media: PerformanceTextParsers.MediaTypeName((int?)Long(row, "MediaType"))), warnings, "Disk media types")
                .ToDictionary(d => d.Id, d => d.Media);
            var partitions = Query(@"root\Microsoft\Windows\Storage", "SELECT DiskNumber, DriveLetter FROM MSFT_Partition",
                row => (Disk: Long(row, "DiskNumber")?.ToString() ?? string.Empty, Letter: Str(row, "DriveLetter")), null);
            foreach (var (disk, letter) in partitions)
            {
                if (!string.IsNullOrEmpty(letter) && letter[0] != '\0' && disks.TryGetValue(disk, out var media))
                {
                    mediaByLetter[char.ToUpperInvariant(letter[0])] = media;
                }
            }
        }

        var drives = new List<(string, long, long, string)>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady)
                    {
                        continue;
                    }

                    var letter = d.Name.TrimEnd('\\');
                    var media = letter.Length > 0 && mediaByLetter.TryGetValue(char.ToUpperInvariant(letter[0]), out var m) ? m : "Unknown";
                    drives.Add((letter, d.AvailableFreeSpace, d.TotalSize, media));
                }
                catch
                {
                    // Skip drives that cannot be queried (BitLocker locked, removable).
                }
            }
        }
        catch (Exception ex)
        {
            warnings?.Add("Drive list could not be read: " + ex.Message);
        }

        return drives;
    }

    /// <summary>(HasBattery, OnBattery) from Win32_Battery.</summary>
    public static (bool HasBattery, bool? OnBattery) Battery()
    {
        var rows = Query(@"root\cimv2", "SELECT BatteryStatus FROM Win32_Battery", row => Long(row, "BatteryStatus") ?? 0, null);
        if (rows.Count == 0)
        {
            return (false, null);
        }

        // 1 = discharging; 2 = on AC; others (charging, critical) vary.
        return (true, rows[0] == 1);
    }

    /// <summary>Number of third-party antivirus products registered with Security Center (excludes Microsoft Defender).</summary>
    public static int? ThirdPartyAntivirusCount()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        var names = Query(@"root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct", row => Str(row, "displayName") ?? string.Empty, null);
        return names.Count(n => !n.Contains("Defender", StringComparison.OrdinalIgnoreCase) && !n.Contains("Windows Security", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Last boot duration in seconds from the Diagnostics-Performance event 100, or null.</summary>
    public static async Task<double?> LastBootSecondsAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        const string command = "$e = Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Diagnostics-Performance/Operational'; Id=100} -MaxEvents 1 -ErrorAction Stop; "
                               + "$x = [xml]$e.ToXml(); ($x.Event.EventData.Data | Where-Object { $_.Name -eq 'BootTime' }).'#text'";
        try
        {
            var result = await PowerShellRunner.RunAsync(command, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return result.Success && PerformanceTextParsers.TryParseBootTimeMs(result.StdOut, out long ms)
                ? PerformanceTextParsers.BootTimeToSeconds(ms)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool?> TrimEnabledAsync(CancellationToken cancellationToken)
    {
        var result = await ExternalCommand.RunAsync("fsutil.exe", "behavior query DisableDeleteNotify", cancellationToken, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return result.Success ? PerformanceTextParsers.ParseTrimEnabled(result.Output) : null;
    }

    public static async Task<PowerScheme?> ActivePowerSchemeAsync(CancellationToken cancellationToken)
    {
        var result = await ExternalCommand.RunAsync("powercfg.exe", "/getactivescheme", cancellationToken, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return result.Success ? PowerPlans.ParseScheme(result.Output) : null;
    }

    public static bool? FastStartupEnabled() => RegistryTweaks.GetDword(RegistryHive.LocalMachine, SessionManagerPower, "HiberbootEnabled") is { } v ? v != 0 : null;

    public static bool? HibernationEnabled() => RegistryTweaks.GetDword(RegistryHive.LocalMachine, ControlPower, "HibernateEnabled") is { } v ? v != 0 : null;

    /// <summary>VisualFXSetting: 0 let Windows choose, 1 best appearance, 2 best performance, 3 custom.</summary>
    public static int? VisualEffectsSetting() => RegistryTweaks.GetDword(RegistryHive.CurrentUser, ExplorerVisualEffects, "VisualFXSetting");

    public static bool? TransparencyEnabled() => RegistryTweaks.GetDword(RegistryHive.CurrentUser, Personalize, "EnableTransparency") is { } v ? v != 0 : null;

    /// <summary>Game Mode is on by default on Windows 11, so a missing value counts as enabled.</summary>
    public static bool? GameModeEnabled()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        var value = RegistryTweaks.GetDword(RegistryHive.CurrentUser, GameBar, "AutoGameModeEnabled")
                    ?? RegistryTweaks.GetDword(RegistryHive.CurrentUser, GameBar, "AllowAutoGameMode");
        return value is null || value != 0;
    }

    public static bool? GameBarEnabled()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        var value = RegistryTweaks.GetDword(RegistryHive.CurrentUser, GameDvr, "AppCaptureEnabled");
        return value is null || value != 0;
    }

    public static bool? BackgroundAppsEnabled()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        var value = RegistryTweaks.GetDword(RegistryHive.CurrentUser, BackgroundApps, "GlobalUserDisabled");
        return value is null || value == 0;
    }

    public static bool? StorageSenseEnabled()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        return RegistryTweaks.GetDword(RegistryHive.CurrentUser, StorageSensePolicy, "01") is { } v && v != 0;
    }

    public static bool? PendingReboot()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        return RegistryTweaks.KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
               || RegistryTweaks.KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
               || RegistryTweaks.ValueExists(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations");
    }

    public static int? DaysSinceUpdateCheck(DateTimeOffset now) =>
        PerformanceTextParsers.DaysSinceUpdateCheck(RegistryTweaks.GetString(RegistryHive.LocalMachine, WindowsUpdateDetect, "LastSuccessTime"), now);

    /// <summary>Samples every process over <paramref name="window"/> and returns CPU %, working set, path and publisher.</summary>
    public static async Task<IReadOnlyList<ProcessInfo>> SampleProcessesAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var first = new Dictionary<int, (string Name, TimeSpan Cpu)>();
        var stopwatch = Stopwatch.StartNew();
        foreach (var p in SafeProcesses())
        {
            try
            {
                first[p.Id] = (p.ProcessName, p.TotalProcessorTime);
            }
            catch
            {
                // Access denied for protected processes; sample only what we can read.
            }
            finally
            {
                p.Dispose();
            }
        }

        await Task.Delay(window, cancellationToken).ConfigureAwait(false);
        var wall = stopwatch.Elapsed;
        int cpus = Environment.ProcessorCount;
        var list = new List<ProcessInfo>();
        foreach (var p in SafeProcesses())
        {
            try
            {
                if (!first.TryGetValue(p.Id, out var start) || p.Id == 0)
                {
                    continue;
                }

                double cpu = ProcessRules.CpuPercent(p.TotalProcessorTime - start.Cpu, wall, cpus);
                string? path = null, publisher = null;
                try
                {
                    path = p.MainModule?.FileName;
                    if (path is not null)
                    {
                        publisher = FileVersionInfo.GetVersionInfo(path).CompanyName;
                    }
                }
                catch
                {
                    // 32/64-bit or protected process: no path available.
                }

                list.Add(new ProcessInfo(p.Id, p.ProcessName, cpu, p.WorkingSet64, path, string.IsNullOrWhiteSpace(publisher) ? null : publisher));
            }
            catch
            {
                // Process exited during sampling.
            }
            finally
            {
                p.Dispose();
            }
        }

        return list;
    }

    private static Process[] SafeProcesses()
    {
        try
        {
            return Process.GetProcesses();
        }
        catch
        {
            return [];
        }
    }

    private static string SafeWindowsDirectory()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return string.IsNullOrWhiteSpace(dir) ? @"C:\Windows" : dir;
        }
        catch
        {
            return @"C:\Windows";
        }
    }
}
