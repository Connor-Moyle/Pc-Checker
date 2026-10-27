using PcChecker.Core.Common;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// Every Windows Task Scheduler task, for the Scheduled tasks page: read the list, run, end, turn off or on, delete
/// with a saved copy, and put deleted tasks back. Every change goes through the change guard and the Action Journal.
/// (PC Checker's own schedules are created by <see cref="Scheduling.IScheduledTaskService"/>.)
/// </summary>
public interface ITaskSchedulerService
{
    /// <summary>Reads every task with its state, last and next run, and checks the signatures of other companies' programs.</summary>
    Task<ScheduledTaskList> GetTasksAsync(CancellationToken cancellationToken);

    /// <summary>Starts the task now (Task Scheduler runs it as its own account). Journaled, no restore point.</summary>
    Task<OperationResult> RunAsync(ScheduledTaskInfo task, CancellationToken cancellationToken);

    /// <summary>Stops the running instances of the task. Journaled.</summary>
    Task<OperationResult> EndAsync(ScheduledTaskInfo task, CancellationToken cancellationToken);

    /// <summary>Turns the task off or on. Journaled with the previous state; Windows' own tasks get a restore point first.</summary>
    Task<OperationResult> SetEnabledAsync(ScheduledTaskInfo task, bool enabled, CancellationToken cancellationToken);

    /// <summary>Saves the task's definition and deletes it (other companies' tasks only). It can be put back with <see cref="RestoreAsync"/>.</summary>
    Task<OperationResult> DeleteAsync(ScheduledTaskInfo task, CancellationToken cancellationToken);

    /// <summary>Tasks deleted here whose saved definition can be registered again, newest first.</summary>
    Task<IReadOnlyList<TaskBackupEntry>> GetBackupsAsync(CancellationToken cancellationToken);

    /// <summary>Registers a deleted task again from its saved definition (never over an existing task).</summary>
    Task<OperationResult> RestoreAsync(TaskBackupEntry backup, CancellationToken cancellationToken);

    /// <summary>Opens File Explorer with the task's program selected.</summary>
    OperationResult OpenFileLocation(ScheduledTaskInfo task);

    /// <summary>Opens a web search for the task name and program.</summary>
    OperationResult SearchOnline(ScheduledTaskInfo task);

    /// <summary>Opens Windows' Task Scheduler (taskschd.msc).</summary>
    OperationResult OpenTaskScheduler();
}
