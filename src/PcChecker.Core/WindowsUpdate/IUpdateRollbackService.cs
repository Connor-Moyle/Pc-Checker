using PcChecker.Core.Common;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Going back after an update: removing installed updates, hiding updates so Windows Update does not reinstall them,
/// and going back to the previous version of Windows after a feature update. Every change runs through the change
/// guard and is journaled.
/// </summary>
public interface IUpdateRollbackService
{
    /// <summary>Installed updates (Get-HotFix, the update history and the DISM package list), newest first. Reads only.</summary>
    Task<InstalledUpdateList> GetInstalledUpdatesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes an installed update behind a restore point: wusa for standalone updates, DISM for the Windows cumulative
    /// update; servicing stack updates are refused. Never restarts; <see cref="OperationResult.RebootRequired"/> says
    /// when a restart is needed to finish.
    /// </summary>
    Task<OperationResult> UninstallAsync(InstalledUpdate update, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Updates hidden from Windows Update. Reads only.</summary>
    Task<HiddenUpdateList> GetHiddenUpdatesAsync(CancellationToken cancellationToken);

    /// <summary>Hides an update (pending, or just removed) so Windows Update does not offer or install it.</summary>
    Task<OperationResult> HideAsync(UpdateHideTarget target, CancellationToken cancellationToken);

    /// <summary>Shows a hidden update again.</summary>
    Task<OperationResult> UnhideAsync(HiddenUpdate update, CancellationToken cancellationToken);

    /// <summary>Whether going back to the previous version of Windows is possible, and for how many more days. Reads only.</summary>
    Task<RollbackWindow> GetRollbackWindowAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts going back to the previous version of Windows. Windows finishes it on the next restart, which the caller
    /// offers; <see cref="OperationResult.RebootRequired"/> is set on success.
    /// </summary>
    Task<OperationResult> GoBackAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Sets the number of days (2–60, counted from the upgrade) going back stays possible.</summary>
    Task<OperationResult> SetRollbackWindowAsync(int days, CancellationToken cancellationToken);
}
