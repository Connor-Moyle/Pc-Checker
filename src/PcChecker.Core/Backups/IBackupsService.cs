using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Backups;

/// <summary>
/// The Backups page: System Restore points and System Protection per drive, the state of Windows' own backups (read
/// only), and PC Checker's own backup folders in one place. Reads never change anything and never throw for a Windows
/// failure; every change runs through <see cref="IChangeGuard"/> after the page asked the user.
/// </summary>
public interface IBackupsService
{
    /// <summary>Restore points, System Protection and restore point space for every fixed drive.</summary>
    Task<RestorePointOverview> GetRestorePointsAsync(CancellationToken cancellationToken);

    /// <summary>Whether System Protection covers the Windows drive; null when unknown. Cheap (for the Dashboard).</summary>
    Task<bool?> IsSystemDriveProtectedAsync(CancellationToken cancellationToken);

    /// <summary>File History, OneDrive folder backup and Windows Backup, read only.</summary>
    Task<IReadOnlyList<WindowsBackupItem>> GetWindowsBackupsAsync(CancellationToken cancellationToken);

    /// <summary>Looks for system image backups with wbadmin. Slow; only on request.</summary>
    Task<SystemImageStatus> CheckSystemImagesAsync(CancellationToken cancellationToken);

    /// <summary>PC Checker's own backup folders and what they hold.</summary>
    Task<IReadOnlyList<AppBackupSummary>> GetAppBackupsAsync(CancellationToken cancellationToken);

    /// <summary>Makes a restore point now (the restore point service, with its once-a-day bypass setting).</summary>
    Task<OperationResult<RestorePoint>> CreateRestorePointAsync(string description, CancellationToken cancellationToken);

    /// <summary>Deletes one restore point by its sequence number. Cannot be undone.</summary>
    Task<OperationResult> DeleteRestorePointAsync(SystemRestorePoint point, CancellationToken cancellationToken);

    /// <summary>Deletes every restore point except the newest (the same work as Cleanup's "Older restore points").</summary>
    Task<OperationResult> DeleteAllButNewestAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Turns System Protection on for one drive ("C:").</summary>
    Task<OperationResult> TurnOnProtectionAsync(string drive, CancellationToken cancellationToken);

    /// <summary>Turns System Protection off for one drive. Windows deletes that drive's restore points.</summary>
    Task<OperationResult> TurnOffProtectionAsync(string drive, CancellationToken cancellationToken);

    /// <summary>Sets the most space restore points may use on a drive, in percent of the drive.</summary>
    Task<OperationResult> SetMaxSpaceAsync(string drive, int percent, CancellationToken cancellationToken);

    /// <summary>Puts a drive's previous limit back (the undo of <see cref="SetMaxSpaceAsync"/>).</summary>
    Task<OperationResult> RestoreMaxSpaceAsync(string drive, ShadowStorageInfo previous, CancellationToken cancellationToken);

    /// <summary>
    /// Moves all but the newest <paramref name="keepNewest"/> backups of one location to the Recycle Bin. Returns how
    /// many were moved.
    /// </summary>
    Task<OperationResult<int>> DeleteOldAppBackupsAsync(string locationId, int keepNewest, CancellationToken cancellationToken);
}
