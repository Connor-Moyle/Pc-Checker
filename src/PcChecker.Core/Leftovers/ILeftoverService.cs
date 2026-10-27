using PcChecker.Core.Common;

namespace PcChecker.Core.Leftovers;

/// <summary>
/// Finds traces of programs that are no longer installed (folders, empty folders, registry keys, services, scheduled
/// tasks and Start menu shortcuts) and removes the ones the user picks. Scans only read. Removal runs through the change
/// guard with a restore point: folders and shortcuts go to the Recycle Bin, registry keys are backed up to a .reg file
/// and scheduled tasks to XML first, so both can be put back from the restore list.
/// </summary>
public interface ILeftoverService
{
    /// <summary>The last scan, or null before the first one.</summary>
    LeftoverScanResult? Last { get; }

    /// <summary>Where task backups and the restore list are kept.</summary>
    string BackupDirectory { get; }

    /// <summary>Scans for leftovers; with <paramref name="focus"/>, traces of that just-uninstalled program are scored High.</summary>
    Task<LeftoverScanResult> ScanAsync(LeftoverFocus? focus, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the given findings after re-checking each one. <paramref name="ownerWindow"/> owns any question Windows
    /// asks (an item too big for the Recycle Bin).
    /// </summary>
    Task<OperationResult<LeftoverRemovalResult>> RemoveAsync(IReadOnlyList<LeftoverItem> items, nint ownerWindow,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Undo entries for removed registry keys and scheduled tasks, newest first.</summary>
    Task<IReadOnlyList<LeftoverRestoreEntry>> ListRestoreEntriesAsync(CancellationToken cancellationToken);

    /// <summary>Puts the registry keys or the scheduled task of one entry back.</summary>
    Task<OperationResult> RestoreAsync(string entryId, CancellationToken cancellationToken);
}
