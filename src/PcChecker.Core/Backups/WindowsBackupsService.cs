using System.Globalization;
using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hosts;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;
using PcChecker.Core.SetupBackup;
using PcChecker.Core.Startup;

namespace PcChecker.Core.Backups;

/// <summary>
/// Windows implementation of <see cref="IBackupsService"/>. Restore points come from the <c>SystemRestore</c> WMI class
/// (PowerShell as a fallback), System Protection per drive from the SPP client list in the registry, restore point space
/// from <c>Win32_ShadowStorage</c> (vssadmin's text as a fallback, read by shape so any language works). All of it runs
/// off the UI thread with time limits, and every read degrades to "unknown" instead of throwing.
/// </summary>
public sealed partial class WindowsBackupsService : IBackupsService
{
    private const string Module = "Backups";
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(2);

    private readonly IRestorePointService _restorePoints;
    private readonly IChangeGuard _guard;
    private readonly ICleanupCommandService _cleanupCommands;
    private readonly ISetupBackupService _setupBackups;
    private readonly ISettingsService _settings;
    private readonly IDriverCleanupService _driverCleanup;
    private readonly IHostsFileService _hosts;
    private readonly ILogger<WindowsBackupsService> _logger;

    public WindowsBackupsService(IRestorePointService restorePoints, IChangeGuard guard, ICleanupCommandService cleanupCommands,
        ISetupBackupService setupBackups, ISettingsService settings, IDriverCleanupService driverCleanup, IHostsFileService hosts,
        ILogger<WindowsBackupsService> logger)
    {
        _restorePoints = restorePoints;
        _guard = guard;
        _cleanupCommands = cleanupCommands;
        _setupBackups = setupBackups;
        _settings = settings;
        _driverCleanup = driverCleanup;
        _hosts = hosts;
        _logger = logger;
    }

    private static string SystemDriveLetter() =>
        SystemProtectionRules.NormalizeDrive(Path.GetPathRoot(Environment.SystemDirectory)) ?? "C:";

    // ---- Restore points and System Protection ---------------------------------------------------------------------

    public async Task<RestorePointOverview> GetRestorePointsAsync(CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (!PlatformGuard.IsWindows)
        {
            return new RestorePointOverview([], false, [], false, ["System Restore is only available on Windows."], DateTimeOffset.Now);
        }

        var points = await QueryPointsAsync(cancellationToken).ConfigureAwait(false);
        if (points is null)
        {
            warnings.Add("Windows did not list its restore points. The System Restore service may be turned off.");
        }

        var volumes = await RunWithTimeoutAsync(QueryVolumes, "volume list", cancellationToken).ConfigureAwait(false) ?? [];
        var storage = await QueryShadowStorageAsync(volumes, cancellationToken).ConfigureAwait(false);
        if (storage is null)
        {
            warnings.Add("Windows did not report how much space restore points use.");
        }

        var protectedVolumes = ReadProtectedVolumes();
        bool policyOff = ReadPolicyDisabled();
        if (policyOff)
        {
            warnings.Add("System Restore is turned off by a policy on this PC (often set by an organisation or a tweak tool).");
        }

        var drives = new List<DriveProtection>();
        string systemDrive = SystemDriveLetter();
        foreach (var drive in FixedNtfsDrives())
        {
            volumes.TryGetValue(drive.Letter, out var volume);
            ShadowStorageInfo? info = null;
            storage?.TryGetValue(drive.Letter, out info);
            bool? isProtected = protectedVolumes is null ? null : SystemProtectionRules.IsProtected(protectedVolumes, drive.Letter, volume?.DeviceId);
            drives.Add(new DriveProtection(drive.Letter, drive.Label, drive.Letter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase),
                isProtected, drive.Capacity ?? (long?)volume?.Capacity, info));
        }

        return new RestorePointOverview(
            (points ?? []).OrderByDescending(p => p.SequenceNumber).ToList(),
            points is not null,
            drives.OrderByDescending(d => d.IsSystemDrive).ThenBy(d => d.Drive, StringComparer.OrdinalIgnoreCase).ToList(),
            policyOff,
            warnings,
            DateTimeOffset.Now);
    }

    public Task<bool?> IsSystemDriveProtectedAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult<bool?>(null);
        }

        return Task.Run<bool?>(() =>
        {
            if (ReadPolicyDisabled())
            {
                return false;
            }

            var list = ReadProtectedVolumes();
            return list is null ? null : SystemProtectionRules.IsProtected(list, SystemDriveLetter());
        }, cancellationToken);
    }

    private async Task<List<SystemRestorePoint>?> QueryPointsAsync(CancellationToken cancellationToken)
    {
        var fromWmi = await RunWithTimeoutAsync(QueryPointsViaWmi, "restore point list", cancellationToken).ConfigureAwait(false);
        if (fromWmi is not null)
        {
            return fromWmi;
        }

        try
        {
            var result = await PowerShellRunner.RunAsync(
                "ConvertTo-Json -Depth 3 -Compress -InputObject @(Get-ComputerRestorePoint | Select-Object SequenceNumber, Description, CreationTime, RestorePointType, EventType)",
                ReadTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(result.StdOut))
            {
                return [];
            }

            var rows = PowerShellRunner.ParseJson<List<PointRow>>(result.StdOut.Trim());
            return rows?.Select(r => new SystemRestorePoint(r.SequenceNumber, r.Description ?? string.Empty,
                RestorePointRules.ParseWmiTime(r.CreationTime) ?? DateTimeOffset.MinValue, r.RestorePointType, r.EventType)).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Get-ComputerRestorePoint failed.");
            return null;
        }
    }

    private static List<SystemRestorePoint> QueryPointsViaWmi()
    {
        var list = new List<SystemRestorePoint>();
        var scope = new ManagementScope(@"\\.\root\default");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT SequenceNumber, Description, CreationTime, RestorePointType, EventType FROM SystemRestore"));
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                list.Add(new SystemRestorePoint(
                    Convert.ToInt64(item["SequenceNumber"] ?? 0, CultureInfo.InvariantCulture),
                    item["Description"]?.ToString() ?? string.Empty,
                    RestorePointRules.ParseWmiTime(item["CreationTime"]?.ToString()) ?? DateTimeOffset.MinValue,
                    Convert.ToInt32(item["RestorePointType"] ?? -1, CultureInfo.InvariantCulture),
                    Convert.ToInt32(item["EventType"] ?? 0, CultureInfo.InvariantCulture)));
            }
        }

        return list;
    }

    private sealed record VolumeRow(string DeviceId, string? DriveLetter, ulong? Capacity);

    /// <summary>Win32_Volume rows by drive letter ("C:").</summary>
    private static Dictionary<string, VolumeRow> QueryVolumes()
    {
        var map = new Dictionary<string, VolumeRow>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT DeviceID, DriveLetter, Capacity FROM Win32_Volume");
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                var letter = SystemProtectionRules.NormalizeDrive(item["DriveLetter"]?.ToString());
                var id = item["DeviceID"]?.ToString();
                if (letter is null || string.IsNullOrEmpty(id))
                {
                    continue;
                }

                ulong? capacity = item["Capacity"] is { } c ? Convert.ToUInt64(c, CultureInfo.InvariantCulture) : null;
                map[letter] = new VolumeRow(id, letter, capacity);
            }
        }

        return map;
    }

    /// <summary>Restore point space per drive letter; null when neither WMI nor vssadmin answered.</summary>
    private async Task<Dictionary<string, ShadowStorageInfo>?> QueryShadowStorageAsync(Dictionary<string, VolumeRow> volumes, CancellationToken cancellationToken)
    {
        var fromWmi = await RunWithTimeoutAsync(() => QueryShadowStorageViaWmi(volumes), "shadow storage", cancellationToken).ConfigureAwait(false);
        if (fromWmi is not null)
        {
            return fromWmi;
        }

        var output = await CommandRunner.RunAsync(CommandRunner.SystemTool("vssadmin.exe"), ["list", "shadowstorage"], ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (output.TimedOut || string.IsNullOrWhiteSpace(output.StdOut))
        {
            return null;
        }

        var map = new Dictionary<string, ShadowStorageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in ShadowStorageParser.ParseVssadmin(output.StdOut))
        {
            var letter = block.ForDrive ?? volumes.Values.FirstOrDefault(v => string.Equals(v.DeviceId, block.ForVolumeId, StringComparison.OrdinalIgnoreCase))?.DriveLetter;
            if (letter is not null)
            {
                map[letter] = ShadowStorageParser.ToInfo(block);
            }
        }

        return map;
    }

    private static Dictionary<string, ShadowStorageInfo> QueryShadowStorageViaWmi(Dictionary<string, VolumeRow> volumes)
    {
        var map = new Dictionary<string, ShadowStorageInfo>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT AllocatedSpace, MaxSpace, UsedSpace, Volume FROM Win32_ShadowStorage");
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                var volumeId = ShadowStorageParser.VolumeIdFromReference(item["Volume"]?.ToString());
                var volume = volumes.Values.FirstOrDefault(v => string.Equals(v.DeviceId, volumeId, StringComparison.OrdinalIgnoreCase));
                if (volume?.DriveLetter is null)
                {
                    continue;
                }

                map[volume.DriveLetter] = ShadowStorageParser.FromWmi(
                    ToUInt64(item["UsedSpace"]), ToUInt64(item["AllocatedSpace"]), ToUInt64(item["MaxSpace"]), volume.Capacity);
            }
        }

        return map;
    }

    private static ulong ToUInt64(object? value) => value is null ? 0 : Convert.ToUInt64(value, CultureInfo.InvariantCulture);

    /// <summary>The protected drives from the SPP client list; an empty list when none are; null when unreadable.</summary>
    private IReadOnlyList<ProtectedVolume>? ReadProtectedVolumes()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(SystemProtectionRules.SppClientsKey);
            if (key is null)
            {
                return [];
            }

            return key.GetValue(SystemProtectionRules.SystemRestoreClientValue) switch
            {
                string[] entries => SystemProtectionRules.ParseClients(entries),
                string single => SystemProtectionRules.ParseClients([single]),
                _ => [],
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the System Protection drive list.");
            return null;
        }
    }

    private bool ReadPolicyDisabled()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(SystemProtectionRules.PolicyKey);
            return key?.GetValue("DisableSR") is int value && value == 1;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the System Restore policy.");
            return false;
        }
    }

    private sealed record FixedDrive(string Letter, string? Label, long? Capacity);

    private IEnumerable<FixedDrive> FixedNtfsDrives()
    {
        DriveInfo[] all;
        try
        {
            all = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not list the drives.");
            yield break;
        }

        foreach (var drive in all)
        {
            FixedDrive? item = null;
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)
                    && SystemProtectionRules.NormalizeDrive(drive.Name) is { } letter)
                {
                    item = new FixedDrive(letter, string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel, drive.TotalSize);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that disappears or refuses access is left out.
            }

            if (item is not null)
            {
                yield return item;
            }
        }
    }

    // ---- Windows backups ----------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<WindowsBackupItem>> GetWindowsBackupsAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<WindowsBackupItem>>(() => [ReadFileHistory(), ReadOneDriveFolders(), WindowsBackupApp()], cancellationToken);

    private WindowsBackupItem ReadFileHistory()
    {
        var state = BackupFeatureState.Unknown;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), WindowsBackupRules.FileHistoryConfigFolder);
            bool exists = Directory.Exists(folder);
            state = WindowsBackupRules.FileHistoryState(exists, exists ? Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().ToList() : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the File History configuration.");
        }

        string detail = state switch
        {
            BackupFeatureState.On => "Set up for you. Open File History to see when it last ran and which drive it uses.",
            BackupFeatureState.Off => "Not set up. File History copies your folders to another drive every hour.",
            _ => "PC Checker could not tell. Open File History to check.",
        };
        return new WindowsBackupItem("file-history", "File History", state, detail, WindowsBackupRules.FileHistoryCommand, WindowsBackupRules.FileHistoryArguments);
    }

    private WindowsBackupItem ReadOneDriveFolders()
    {
        var roots = new List<string?>
        {
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveConsumer"),
            Environment.GetEnvironmentVariable("OneDriveCommercial"),
        };
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (var name in accounts?.GetSubKeyNames() ?? [])
            {
                using var account = accounts!.OpenSubKey(name);
                roots.Add(account?.GetValue("UserFolder") as string);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the OneDrive accounts.");
        }

        var folders = new List<KeyValuePair<string, string?>>
        {
            new("Desktop", SafeFolder(Environment.SpecialFolder.DesktopDirectory)),
            new("Documents", SafeFolder(Environment.SpecialFolder.MyDocuments)),
            new("Pictures", SafeFolder(Environment.SpecialFolder.MyPictures)),
            new("Music", SafeFolder(Environment.SpecialFolder.MyMusic)),
            new("Videos", SafeFolder(Environment.SpecialFolder.MyVideos)),
        };

        bool hasOneDrive = roots.Any(r => !string.IsNullOrWhiteSpace(r));
        var inOneDrive = WindowsBackupRules.FoldersInOneDrive(folders, roots);
        var state = inOneDrive.Count > 0 ? BackupFeatureState.On : BackupFeatureState.Off;
        string detail = inOneDrive.Count > 0
            ? $"OneDrive backs up {WindowsBackupRules.JoinNames(inOneDrive)}."
            : hasOneDrive
                ? "OneDrive is signed in, but your Desktop, Documents and Pictures are not backed up by it."
                : "OneDrive is not set up on this PC.";
        return new WindowsBackupItem("onedrive", "OneDrive folder backup", state, detail, WindowsBackupRules.WindowsBackupUri, null);
    }

    private static string? SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static WindowsBackupItem WindowsBackupApp() =>
        new("windows-backup", "Windows Backup", BackupFeatureState.Unknown,
            "Backs up your folders, apps, settings and passwords to your Microsoft account. Open it to see what is included.",
            WindowsBackupRules.WindowsBackupUri, null);

    public async Task<SystemImageStatus> CheckSystemImagesAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new SystemImageStatus(false, 0, null, "System image backups are only available on Windows.");
        }

        var tool = CommandRunner.SystemTool("wbadmin.exe");
        if (!File.Exists(tool))
        {
            return new SystemImageStatus(false, 0, null, "This edition of Windows has no wbadmin tool, so system images cannot be listed.");
        }

        var output = await CommandRunner.RunAsync(tool, ["get", "versions"], ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (output.TimedOut)
        {
            return new SystemImageStatus(false, 0, null, "Windows took too long to list its backups. Try again later.");
        }

        return WindowsBackupRules.DescribeSystemImages(output.ExitCode, output.StdOut, DateTimeOffset.UtcNow);
    }

    // ---- PC Checker's own backups -------------------------------------------------------------------------------------

    private IReadOnlyList<AppBackupLocation> Locations() =>
        AppBackupCatalog.Build(AppBackupFolders.ForCurrentUser(_setupBackups.BackupDirectory, _settings.SettingsDirectory, _driverCleanup.BackupRoot, _hosts.BackupFolder));

    public Task<IReadOnlyList<AppBackupSummary>> GetAppBackupsAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<AppBackupSummary>>(() => Locations().Select(l => Summarize(l, cancellationToken)).ToList(), cancellationToken);

    private AppBackupSummary Summarize(AppBackupLocation location, CancellationToken cancellationToken)
    {
        try
        {
            return new AppBackupSummary(location, AppBackupFiles.List(location, cancellationToken), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "Could not read the backups in {Folder}.", location.Folder);
            return new AppBackupSummary(location, [], "The folder could not be read: " + ex.Message);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    /// <summary>Runs a blocking read on the thread pool with a time limit; null when it failed or timed out.</summary>
    private async Task<T?> RunWithTimeoutAsync<T>(Func<T> read, string what, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await Task.Run(read, cancellationToken).WaitAsync(ReadTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the {What} failed.", what);
            return null;
        }
    }

    private sealed class PointRow
    {
        public long SequenceNumber { get; set; }
        public string? Description { get; set; }
        public string? CreationTime { get; set; }
        public int RestorePointType { get; set; }
        public int EventType { get; set; }
    }
}
