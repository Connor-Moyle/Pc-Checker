using PcChecker.Core.Common;

namespace PcChecker.Core.Scheduling;

/// <summary>
/// Demo mode: never runs schtasks. Status reads show sample registered tasks for the jobs the demo settings turn on;
/// every change answers "Demo mode: schedules are not created". Pure .NET.
/// </summary>
public sealed class DemoScheduledTaskService : IScheduledTaskService
{
    public const string DemoMessage = "Demo mode: schedules are not created.";
    private const string DemoExe = @"C:\Users\Alex\Downloads\PcChecker.exe";

    public bool CanSchedule => false;

    public string? UnavailableReason => DemoMessage;

    public string? ExecutablePath => DemoExe;

    public async Task<ScheduledTaskStatus> GetStatusAsync(ScheduledJobKind job, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 60, 180).ConfigureAwait(false);
        var now = DateTime.Now;
        var schedule = ScheduleRules.DefaultJobs().First(j => j.Job == job);
        schedule.Enabled = true;
        var next = ScheduleRules.NextRun(schedule, now);
        var last = next?.AddDays(-7);
        return new ScheduledTaskStatus(ScheduledJobIds.TaskName(job), true, "Ready", next, last, 0,
            SchtasksCommandBuilder.TaskRun(DemoExe, "--scheduled " + ScheduledJobIds.Id(job)),
            next?.ToString("g"), last?.ToString("g"));
    }

    public Task<OperationResult> CreateOrUpdateAsync(JobSchedule schedule, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Ok(DemoMessage));

    public Task<OperationResult> DeleteAsync(ScheduledJobKind job, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Ok(DemoMessage));

    public async Task<ScheduledTaskStatus> GetLogonTaskStatusAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 60, 180).ConfigureAwait(false);
        return ScheduledTaskStatus.Missing(SchtasksCommandBuilder.LogonTaskName);
    }

    public Task<OperationResult> SetStartWithWindowsAsync(bool enabled, bool startInTray, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Ok(DemoMessage));
}
