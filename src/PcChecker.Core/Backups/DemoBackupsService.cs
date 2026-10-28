using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Backups;

/// <summary>
/// Demo backups for the sample PC: restore points from Windows Update, a driver install and PC Checker, System
/// Protection on C: and off on D:, OneDrive backing up Desktop and Documents, and PC Checker's own backup folders with a
/// few files each. Everything is kept in memory; nothing on the machine is read or changed.
/// </summary>
public sealed class DemoBackupsService : IBackupsService
{
    private const string Local = @"C:\Users\Alex\AppData\Local";
    private const long Gb = 1L << 30;

    private readonly object _gate = new();
    private readonly List<SystemRestorePoint> _points;
    private readonly Dictionary<string, DriveState> _drives;
    private readonly Dictionary<string, List<AppBackupItem>> _appBackups;
    private readonly IReadOnlyList<AppBackupLocation> _locations;

    private sealed class DriveState
    {
        public required string Letter { get; init; }
        public string? Label { get; init; }
        public bool IsSystem { get; init; }
        public long Capacity { get; init; }
        public bool Protected { get; set; }
        public long Used { get; set; }
        public int Percent { get; set; }
    }

    public DemoBackupsService()
    {
        var now = DateTimeOffset.Now;
        _points =
        [
            new(38, "Scheduled Checkpoint", now.AddDays(-26).AddHours(-3), 7, 100),
            new(39, "Installed NVIDIA Graphics Driver 566.36", now.AddDays(-19).AddHours(-5), 10, 100),
            new(40, "Windows Update", now.AddDays(-12).AddHours(-1), 17, 100),
            new(41, "PC Checker: before Apply 14 debloat tweaks", now.AddDays(-6).AddHours(-2), 12, 100),
            new(42, "Windows Update", now.AddDays(-3).AddHours(-7), 17, 100),
            new(43, "PC Checker: before Disable startup item", now.AddDays(-1).AddHours(-4), 12, 100),
        ];
        _drives = new Dictionary<string, DriveState>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:"] = new() { Letter = "C:", Label = "Windows", IsSystem = true, Capacity = 953L * Gb, Protected = true, Used = (long)(9.6 * Gb), Percent = 5 },
            ["D:"] = new() { Letter = "D:", Label = "Games", IsSystem = false, Capacity = 1863L * Gb, Protected = false, Used = 0, Percent = 0 },
        };

        _locations = AppBackupCatalog.Build(new AppBackupFolders(
            Local + @"\PcChecker\backups",
            Local + @"\PcChecker\registry-backups",
            Local + @"\PcChecker\driver-backups",
            Local + @"\PcChecker\debloat-capture.json",
            Local + @"\PcChecker\task-backups",
            Local + @"\PcChecker\backup\hosts",
            Local + @"\PcChecker\backup\sendto-off",
            Local + @"\PcChecker\backup\startup"));

        _appBackups = new Dictionary<string, List<AppBackupItem>>(StringComparer.Ordinal)
        {
            [AppBackupCatalog.SetupId] = Items(now, AppBackupCatalog.SetupId, "setup-{0}.pcchecker", [2, 40, 75], 18_400),
            [AppBackupCatalog.RegistryId] = Items(now, AppBackupCatalog.RegistryId, "registry-backup-{0}.reg", [4, 21, 47, 88, 130, 166, 201], 52_000),
            [AppBackupCatalog.DriversId] = Items(now, AppBackupCatalog.DriversId, "{0}", [9, 63], 61_500_000),
            [AppBackupCatalog.TasksId] = Items(now, AppBackupCatalog.TasksId, "task-{0}-adobe-acrobat-update-task.xml", [15], 3_900),
            [AppBackupCatalog.HostsId] = Items(now, AppBackupCatalog.HostsId, "hosts-{0}.bak", [1, 8, 30, 31, 55, 90, 140, 210], 2_100),
            [AppBackupCatalog.StartupId] = Items(now, AppBackupCatalog.StartupId, "{0}-Spotify.json", [11], 640),
            [AppBackupCatalog.DebloatId] = [new AppBackupItem(Local + @"\PcChecker\debloat-capture.json", now.AddDays(-6), 7_300)],
            [AppBackupCatalog.SendToId] =
            [
                new AppBackupItem(Local + @"\PcChecker\backup\sendto-off\Fax recipient.lnk", now.AddDays(-33), 1_200),
                new AppBackupItem(Local + @"\PcChecker\backup\sendto-off\Bluetooth device.lnk", now.AddDays(-33), 1_100),
            ],
        };
    }

    private List<AppBackupItem> Items(DateTimeOffset now, string id, string nameFormat, int[] daysAgo, long size)
    {
        var folder = _locations.First(l => l.Id == id).Folder;
        return daysAgo
            .Select((d, i) =>
            {
                var when = now.AddDays(-d).AddHours(-i);
                var name = string.Format(CultureInfo.InvariantCulture, nameFormat, when.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                return new AppBackupItem(folder + "\\" + name, when, size + (i * 731));
            })
            .ToList();
    }

    public async Task<RestorePointOverview> GetRestorePointsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 150, 450).ConfigureAwait(false);
        lock (_gate)
        {
            var drives = _drives.Values
                .Select(d => new DriveProtection(d.Letter, d.Label, d.IsSystem, d.Protected, d.Capacity,
                    d.Protected ? new ShadowStorageInfo(d.Used, d.Used + (512L << 20), d.Capacity * d.Percent / 100, false, d.Percent) : null))
                .OrderByDescending(d => d.IsSystemDrive)
                .ToList();
            return new RestorePointOverview(_points.OrderByDescending(p => p.SequenceNumber).ToList(), true, drives, false, [], DateTimeOffset.Now);
        }
    }

    public Task<bool?> IsSystemDriveProtectedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<bool?>(_drives["C:"].Protected);
        }
    }

    public async Task<IReadOnlyList<WindowsBackupItem>> GetWindowsBackupsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        return
        [
            new("file-history", "File History", BackupFeatureState.Off, "Not set up. File History copies your folders to another drive every hour.",
                WindowsBackupRules.FileHistoryCommand, WindowsBackupRules.FileHistoryArguments),
            new("onedrive", "OneDrive folder backup", BackupFeatureState.On, "OneDrive backs up Desktop and Documents.", WindowsBackupRules.WindowsBackupUri, null),
            new("windows-backup", "Windows Backup", BackupFeatureState.Unknown,
                "Backs up your folders, apps, settings and passwords to your Microsoft account. Open it to see what is included.",
                WindowsBackupRules.WindowsBackupUri, null),
        ];
    }

    public async Task<SystemImageStatus> CheckSystemImagesAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 600, 1200).ConfigureAwait(false);
        var output = "Backup time: 3/2/2026 9:14 PM\nBackup target: 1394/USB Disk labeled Backup(E:)\nVersion identifier: "
            + DateTimeOffset.UtcNow.AddDays(-57).ToString("MM/dd/yyyy-HH:mm", CultureInfo.InvariantCulture) + "\nCan recover: Volume(s), File(s), Bare Metal Recovery\n";
        return WindowsBackupRules.DescribeSystemImages(0, output, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<AppBackupSummary>> GetAppBackupsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 250).ConfigureAwait(false);
        lock (_gate)
        {
            return _locations
                .Select(l => new AppBackupSummary(l, _appBackups.TryGetValue(l.Id, out var items) ? items.OrderByDescending(i => i.Created).ToList() : [], null))
                .ToList();
        }
    }

    public async Task<OperationResult<RestorePoint>> CreateRestorePointAsync(string description, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 600, 1100).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_drives["C:"].Protected)
            {
                return OperationResult<RestorePoint>.Fail("System Restore is turned off on this PC (the System Restore service is disabled)");
            }

            var text = string.IsNullOrWhiteSpace(description) ? "Manual restore point (PC Checker)" : description.Trim();
            var point = new SystemRestorePoint(_points.Count == 0 ? 1 : _points.Max(p => p.SequenceNumber) + 1, text, DateTimeOffset.Now, 12, 100);
            _points.Add(point);
            _drives["C:"].Used += 380L << 20;
            return OperationResult<RestorePoint>.Ok(new RestorePoint(point.SequenceNumber, point.Description, point.Created, point.TypeText),
                $"Restore point \"{text}\" created (demo).");
        }
    }

    public async Task<OperationResult> DeleteRestorePointAsync(SystemRestorePoint point, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(point);
        await DemoData.DelayAsync(cancellationToken, 200, 500).ConfigureAwait(false);
        lock (_gate)
        {
            int removed = _points.RemoveAll(p => p.SequenceNumber == point.SequenceNumber);
            return removed == 0
                ? OperationResult.Fail("That restore point is already gone; refresh the list.")
                : OperationResult.Ok($"The restore point \"{point.Description}\" was deleted (demo).");
        }
    }

    public async Task<OperationResult> DeleteAllButNewestAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        int count;
        lock (_gate)
        {
            count = Math.Max(0, _points.Count - 1);
        }

        for (int i = 0; i < count; i++)
        {
            progress?.Report(new ProgressUpdate("Deleting older restore points", 100.0 * i / Math.Max(1, count), $"{count - i} to go"));
            await DemoData.DelayAsync(cancellationToken, 120, 260).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_points.Count <= 1)
            {
                return OperationResult.Ok("There was only one restore point, so nothing was deleted.");
            }

            var newest = _points.MaxBy(p => p.SequenceNumber)!;
            _points.RemoveAll(p => p.SequenceNumber != newest.SequenceNumber);
            _drives["C:"].Used = 1L * Gb;
            return OperationResult.Ok(count == 1 ? "Deleted 1 older restore point; the newest one stays (demo)." : $"Deleted {count} older restore points; the newest one stays (demo).");
        }
    }

    public async Task<OperationResult> TurnOnProtectionAsync(string drive, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 700).ConfigureAwait(false);
        lock (_gate)
        {
            var letter = SystemProtectionRules.NormalizeDrive(drive);
            if (letter is null || !_drives.TryGetValue(letter, out var state))
            {
                return OperationResult.Fail("That drive is not on this PC.");
            }

            state.Protected = true;
            state.Percent = state.Percent == 0 ? SystemProtectionRules.SuggestedPercent : state.Percent;
            return OperationResult.Ok($"System Protection is now on for {letter} (demo).");
        }
    }

    public async Task<OperationResult> TurnOffProtectionAsync(string drive, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 700).ConfigureAwait(false);
        lock (_gate)
        {
            var letter = SystemProtectionRules.NormalizeDrive(drive);
            if (letter is null || !_drives.TryGetValue(letter, out var state))
            {
                return OperationResult.Fail("That drive is not on this PC.");
            }

            state.Protected = false;
            state.Used = 0;
            if (state.IsSystem)
            {
                _points.Clear();
            }

            return OperationResult.Ok($"System Protection is now off for {letter} (demo).");
        }
    }

    public async Task<OperationResult> SetMaxSpaceAsync(string drive, int percent, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 200, 500).ConfigureAwait(false);
        lock (_gate)
        {
            var letter = SystemProtectionRules.NormalizeDrive(drive);
            if (letter is null || !_drives.TryGetValue(letter, out var state) || !state.Protected)
            {
                return OperationResult.Fail($"Windows did not change the space for restore points on {letter}. Turn System Protection on for the drive first.");
            }

            state.Percent = Math.Clamp(percent, SystemProtectionRules.MinPercent, SystemProtectionRules.MaxPercent);
            state.Used = Math.Min(state.Used, state.Capacity * state.Percent / 100);
            return OperationResult.Ok($"Restore points on {letter} can now use up to {state.Percent} % of the drive (demo).");
        }
    }

    public Task<OperationResult> RestoreMaxSpaceAsync(string drive, ShadowStorageInfo previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return SetMaxSpaceAsync(drive, SystemProtectionRules.PercentForEditing(previous), cancellationToken);
    }

    public async Task<OperationResult<int>> DeleteOldAppBackupsAsync(string locationId, int keepNewest, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 200, 500).ConfigureAwait(false);
        lock (_gate)
        {
            var location = _locations.FirstOrDefault(l => l.Id == locationId);
            if (location is null || !location.CanDeleteOld || !_appBackups.TryGetValue(locationId, out var items))
            {
                return OperationResult<int>.Fail("These backups cannot be deleted here.");
            }

            var toDelete = AppBackupCatalog.SelectToDelete(items, keepNewest);
            items.RemoveAll(i => toDelete.Contains(i));
            return toDelete.Count == 0
                ? OperationResult<int>.Ok(0, "Nothing to delete: there are no more backups than you chose to keep.")
                : OperationResult<int>.Ok(toDelete.Count, toDelete.Count == 1
                    ? "1 old backup was moved to the Recycle Bin (demo)."
                    : $"{toDelete.Count} old backups were moved to the Recycle Bin (demo).");
        }
    }
}
