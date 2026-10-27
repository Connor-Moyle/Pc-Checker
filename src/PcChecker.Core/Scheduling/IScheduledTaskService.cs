using PcChecker.Core.Common;

namespace PcChecker.Core.Scheduling;

/// <summary>
/// Creates, reads and removes PC Checker's Windows scheduled tasks: one per maintenance job (so they run while the app
/// is closed) and the logon task behind "Start with Windows". Every change goes through the change guard.
/// </summary>
public interface IScheduledTaskService
{
    /// <summary>False in demo mode or off Windows; <see cref="UnavailableReason"/> says why.</summary>
    bool CanSchedule { get; }

    string? UnavailableReason { get; }

    /// <summary>The PcChecker.exe that new tasks run.</summary>
    string? ExecutablePath { get; }

    Task<ScheduledTaskStatus> GetStatusAsync(ScheduledJobKind job, CancellationToken cancellationToken);

    /// <summary>Creates the job's task, or replaces it when it exists (schtasks /F).</summary>
    Task<OperationResult> CreateOrUpdateAsync(JobSchedule schedule, CancellationToken cancellationToken);

    /// <summary>Removes the job's task. Succeeds when it was already gone.</summary>
    Task<OperationResult> DeleteAsync(ScheduledJobKind job, CancellationToken cancellationToken);

    Task<ScheduledTaskStatus> GetLogonTaskStatusAsync(CancellationToken cancellationToken);

    /// <summary>Creates (or removes) the logon task that starts PC Checker when you sign in.</summary>
    Task<OperationResult> SetStartWithWindowsAsync(bool enabled, bool startInTray, CancellationToken cancellationToken);
}
