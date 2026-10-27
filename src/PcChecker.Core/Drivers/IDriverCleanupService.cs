using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers;

/// <summary>
/// Finds old and unused driver packages (superseded versions, packages no connected device uses, drivers of devices
/// that are no longer connected) and removes the ones the user picks: each package is exported to a backup folder first
/// and deleted without <c>/force</c>, so Windows refuses anything still in use.
/// </summary>
public interface IDriverCleanupService
{
    /// <summary>Folder that holds one timestamped sub-folder of exported packages per removal run.</summary>
    string BackupRoot { get; }

    Task<DriverCleanupScanResult> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a plan from <see cref="DriverCleanupRules.BuildRemovalPlan"/> through the change guard (restore point, journal).
    /// The value carries one result per step even when some steps failed.
    /// </summary>
    Task<OperationResult<DriverCleanupRemovalResult>> RemoveAsync(IReadOnlyList<DriverCleanupStep> plan, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken);
}
