using PcChecker.Core.Common;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Windows Update for software (drivers stay on the Drivers page): status, pending updates, install history, and the
/// pause and active hours controls the Settings app offers. Every change runs through the change guard.
/// </summary>
public interface IWindowsUpdateService
{
    /// <summary>Last check and install, pending restart, pause, active hours and policy. Reads only.</summary>
    Task<WindowsUpdateStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Asks Windows Update for software updates that are not installed yet. Can take a minute.</summary>
    Task<PendingUpdateScan> CheckForUpdatesAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>The most recent installs, newest first.</summary>
    Task<UpdateHistory> GetHistoryAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads (where needed) and installs the given updates one at a time, behind a restore point. Each update gets
    /// its own result; cancelling stops before the next update.
    /// </summary>
    Task<OperationResult<UpdateInstallResult>> InstallAsync(IReadOnlyList<PendingUpdate> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Pauses updates for 1–5 more weeks, the way Settings does (a running pause is extended).</summary>
    Task<OperationResult> PauseAsync(int weeks, CancellationToken cancellationToken);

    /// <summary>Ends a pause so Windows looks for updates again.</summary>
    Task<OperationResult> ResumeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets active hours (whole hours, at most 18 apart or the policy limit). Refused while Windows adjusts them
    /// automatically, since that switch is left to Settings.
    /// </summary>
    Task<OperationResult> SetActiveHoursAsync(int startHour, int endHour, CancellationToken cancellationToken);
}
