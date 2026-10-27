using PcChecker.Core.Common;
using PcChecker.Core.Processes;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// A believable Task Scheduler for demo mode and screenshots: updaters from other companies, a few of Windows' own
/// upkeep tasks, a leftover task whose program is gone, a turned-off task and one hidden task with a scrambled
/// PowerShell command so the warnings have something to show. Actions only change the in-memory list.
/// </summary>
public sealed class DemoTaskSchedulerService : ITaskSchedulerService
{
    private const string User = @"GAMING-DESKTOP\Alex";
    private const string System = "S-1-5-18";

    private readonly object _gate = new();
    private readonly List<ScheduledTaskInfo> _tasks;
    private readonly List<(TaskBackupEntry Entry, ScheduledTaskInfo Task)> _deleted = [];

    public DemoTaskSchedulerService()
    {
        _tasks = Build(DateTimeOffset.Now);
    }

    public async Task<ScheduledTaskList> GetTasksAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 200, 450).ConfigureAwait(false);
        lock (_gate)
        {
            return new ScheduledTaskList(_tasks.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList(), 0, true, null);
        }
    }

    public async Task<OperationResult> RunAsync(ScheduledTaskInfo task, CancellationToken cancellationToken)
    {
        if (TaskSafetyRules.RunRefusal(task) is { } refusal)
        {
            return OperationResult.Fail(refusal);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        Replace(task.Path, t => t with { State = TaskRunState.Running, LastRun = DateTimeOffset.Now, LastResult = TaskResultText.TaskRunning });
        return OperationResult.Ok($"{task.Name} was started.");
    }

    public async Task<OperationResult> EndAsync(ScheduledTaskInfo task, CancellationToken cancellationToken)
    {
        if (TaskSafetyRules.EndRefusal(task) is { } refusal)
        {
            return OperationResult.Fail(refusal);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        Replace(task.Path, t => t with { State = TaskRunState.Ready, LastResult = TaskResultText.TaskTerminated });
        return OperationResult.Ok($"{task.Name} was stopped.");
    }

    public async Task<OperationResult> SetEnabledAsync(ScheduledTaskInfo task, bool enabled, bool advancedMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (TaskSafetyRules.ToggleRefusal(task, advancedMode) is { } refusal)
        {
            return OperationResult.Fail(refusal);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        Replace(task.Path, t => t with
        {
            State = enabled ? TaskRunState.Ready : TaskRunState.Disabled,
            Definition = t.Definition with { Enabled = enabled },
            NextRun = enabled ? DateTimeOffset.Now.Date.AddDays(1).AddHours(3) : null,
        });
        return OperationResult.Ok(enabled ? $"{task.Name} is on again." : $"{task.Name} is turned off. It will not run until you turn it on.");
    }

    public async Task<OperationResult> DeleteAsync(ScheduledTaskInfo task, CancellationToken cancellationToken)
    {
        if (TaskSafetyRules.DeleteRefusal(task) is { } refusal)
        {
            return OperationResult.Fail(refusal);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var current = _tasks.FirstOrDefault(t => t.Path == task.Path);
            if (current is null)
            {
                return OperationResult.Fail("The task no longer exists.");
            }

            _tasks.Remove(current);
            var created = DateTimeOffset.Now;
            _deleted.Insert(0, (new TaskBackupEntry(TaskBackupNaming.BackupId(created, task.Path), created, task.Path, current.Definition.NeedsPassword), current));
        }

        return OperationResult.Ok($"{task.Name} was deleted. A copy was saved, so you can put it back under Deleted tasks.");
    }

    public Task<IReadOnlyList<TaskBackupEntry>> GetBackupsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TaskBackupEntry>>(_deleted.Select(d => d.Entry).ToList());
        }
    }

    public async Task<OperationResult> RestoreAsync(TaskBackupEntry backup, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            int index = _deleted.FindIndex(d => d.Entry.Id == backup.Id);
            if (index < 0)
            {
                return OperationResult.Fail("That saved task no longer exists.");
            }

            if (_tasks.Any(t => string.Equals(t.Path, backup.TaskPath, StringComparison.OrdinalIgnoreCase)))
            {
                return OperationResult.Fail($"A task named {backup.TaskPath} exists already, so the saved copy was not imported over it.");
            }

            _tasks.Add(_deleted[index].Task);
            _deleted.RemoveAt(index);
        }

        return OperationResult.Ok($"{backup.Name} is back in Task Scheduler.");
    }

    public OperationResult OpenFileLocation(ScheduledTaskInfo task) =>
        OperationResult.Ok($"Demo mode: File Explorer would open at {task.ProgramPath ?? "the program"}.");

    public OperationResult SearchOnline(ScheduledTaskInfo task) => OperationResult.Ok("Demo mode: a web search would open for " + task.Name + ".");

    public OperationResult OpenTaskScheduler() => OperationResult.Ok("Demo mode: Task Scheduler would open.");

    private void Replace(string path, Func<ScheduledTaskInfo, ScheduledTaskInfo> change)
    {
        lock (_gate)
        {
            int index = _tasks.FindIndex(t => t.Path == path);
            if (index >= 0)
            {
                _tasks[index] = change(_tasks[index]);
            }
        }
    }

    private static List<ScheduledTaskInfo> Build(DateTimeOffset now)
    {
        var today = now.Date;
        DateTimeOffset At(int daysFromToday, int hour, int minute = 0) => new(today.AddDays(daysFromToday).AddHours(hour).AddMinutes(minute));
        var signed = (string publisher) => new SignatureInfo(SignatureState.Signed, publisher);
        var unsigned = new SignatureInfo(SignatureState.Unsigned, null);
        var microsoft = signed("Microsoft Windows");

        TaskTrigger Daily(int hour, int minute = 0) => new(TaskTriggerKind.Daily) { Start = today.AddHours(hour).AddMinutes(minute) };
        TaskTrigger Logon(string? user = null) => new(TaskTriggerKind.Logon) { UserId = user };
        TaskTrigger Weekly(int hour, params DayOfWeek[] days) => new(TaskTriggerKind.Weekly) { Start = today.AddHours(hour), DaysOfWeek = days };

        ScheduledTaskInfo Task(string path, TaskRunState state, DateTimeOffset? last, int? result, DateTimeOffset? next, string? author,
            string command, string? args, string? user, bool highest, IReadOnlyList<TaskTrigger> triggers, SignatureInfo signature,
            bool? exists = true, bool hidden = false, string? description = null)
        {
            var definition = new TaskDefinition(author, description, triggers, [new TaskAction(TaskActionKind.Exec, command, args)],
                user, user is null ? "S-1-5-32-545" : null, "InteractiveToken", highest, hidden, state != TaskRunState.Disabled);
            var info = new ScheduledTaskInfo(path, state, last, result, next, definition);
            return TaskAnalysis.Complete(info, command.Trim('"'), exists, signature);
        }

        return
        [
            Task(@"\GoogleUpdateTaskMachineCore{8C5E7F64-2A1B-4C9F-9E61-0D2D1B7A3E10}", TaskRunState.Ready, At(0, 9, 12), 0, At(1, 9, 12), null,
                @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", "/c", System, false,
                [Logon(), Daily(9, 12) with { RepeatEvery = TimeSpan.FromHours(1), RepeatFor = TimeSpan.FromDays(1) }], signed("Google LLC"),
                description: "Keeps your Google software up to date."),
            Task(@"\NVIDIA GeForce Experience SelfUpdate_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}", TaskRunState.Ready, At(0, 12, 25), 0, At(1, 12, 25), "NVIDIA Corporation",
                @"C:\Program Files\NVIDIA Corporation\NVIDIA app\NvContainer\NvContainer.exe", "-f \"%PROGRAMDATA%\\NVIDIA\\NvBackend.log\"", User, false,
                [Daily(12, 25)], signed("NVIDIA Corporation")),
            Task(@"\ASUS\ArmouryCrate Update", TaskRunState.Running, At(0, 10, 3), TaskResultText.TaskRunning, At(1, 10), "ASUSTeK COMPUTER INC.",
                @"C:\Program Files\ASUS\ArmouryDevice\dll\ArmouryCrateUpdater.exe", null, System, true,
                [Logon(), Daily(10)], signed("ASUSTeK COMPUTER INC.")),
            Task(@"\Adobe Acrobat Update Task", TaskRunState.Disabled, At(-12, 14), 0, null, "Adobe Systems Incorporated",
                @"C:\Program Files (x86)\Common Files\Adobe\ARM\1.0\AdobeARM.exe", null, null, false,
                [Logon(), Daily(14)], signed("Adobe Inc."), description: "This task keeps your Adobe Reader and Acrobat applications up to date."),
            Task(@"\Opera scheduled Autoupdate 1714465123", TaskRunState.Ready, At(-1, 18, 40), 0, At(0, 18, 40), User,
                @"C:\Users\Alex\AppData\Local\Programs\Opera GX\launcher.exe", "--scheduledautoupdate $(Arg0)", User, false,
                [Daily(18, 40)], signed("Opera Norway AS")),
            Task(@"\Dropbox Update Task-S-1-5-21-3623811015-3361044348-30300820-1001", TaskRunState.Ready, At(0, 8), TaskResultText.FileNotFound, At(1, 8), "Dropbox, Inc.",
                @"C:\Program Files (x86)\Dropbox\Update\DropboxUpdate.exe", "/ua /installsource scheduler", User, false,
                [Logon(User), Daily(8)], SignatureInfo.Unknown, exists: false),
            Task(@"\PDFConverterUpdater", TaskRunState.Ready, At(0, 11, 30), 0, At(0, 13, 30), null,
                @"C:\Users\Alex\AppData\Roaming\PDFConverter\updater.exe", "/silent", User, false,
                [Logon(), Daily(9, 30) with { RepeatEvery = TimeSpan.FromHours(2) }], unsigned),
            Task(@"\SystemHealthMonitor", TaskRunState.Ready, At(0, 7, 55), 1, At(0, 13, 55), @"GAMING-DESKTOP\Alex",
                "powershell.exe", "-NoProfile -WindowStyle Hidden -EncodedCommand SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQAIABOAGUAdAAuAFcAZQBiAEMAbABpAGUAbgB0ACkA",
                User, true, [Logon() with { Delay = TimeSpan.FromMinutes(5) }, new TaskTrigger(TaskTriggerKind.Idle)], microsoft, hidden: true),
            Task(@"\PC Checker\Health check", TaskRunState.Ready, At(-3, 19), 0, At(4, 19), "PC Checker",
                @"C:\Program Files\PC Checker\PcChecker.exe", "--scheduled health", User, true,
                [Weekly(19, DayOfWeek.Sunday)], signed("PC Checker")),
            Task(@"\MicrosoftEdgeUpdateTaskMachineUA{4B9C2E0A-3B6F-4C85-9E50-0A1F2C3D4E5F}", TaskRunState.Ready, At(0, 13, 4), 0, At(0, 14, 4), "Microsoft Corporation",
                @"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe", "/ua /installsource scheduler", System, false,
                [Daily(13, 4) with { RepeatEvery = TimeSpan.FromHours(1), RepeatFor = TimeSpan.FromDays(1) }], signed("Microsoft Corporation")),
            Task(@"\OneDrive Standalone Update Task-S-1-5-21-3623811015-3361044348-30300820-1001", TaskRunState.Ready, At(0, 6, 17), 0, At(1, 6, 17), "Microsoft Corporation",
                @"C:\Users\Alex\AppData\Local\Microsoft\OneDrive\OneDriveStandaloneUpdater.exe", null, User, false,
                [Daily(6, 17)], signed("Microsoft Corporation")),
            Task(@"\Microsoft\Windows\Defrag\ScheduledDefrag", TaskRunState.Ready, At(-2, 3, 12), 0, At(5, 3), "$(@%SystemRoot%\\system32\\defragsvc.dll,-801)",
                @"%windir%\system32\defrag.exe", "-c -h -o -$", System, true,
                [Weekly(1, DayOfWeek.Wednesday)], microsoft, description: "Optimizes local storage drives."),
            Task(@"\Microsoft\Windows\Windows Defender\Windows Defender Scheduled Scan", TaskRunState.Ready, At(-1, 2, 47), 0, At(0, 2, 47), "Microsoft Corporation",
                @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.24090.11-0\MpCmdRun.exe", "Scan -ScheduleJob", System, true,
                [Daily(2, 47)], microsoft),
            Task(@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan", TaskRunState.Ready, At(0, 4, 30), 0, At(0, 16, 30), "$(@%SystemRoot%\\system32\\usosvc.dll,-603)",
                @"%systemroot%\system32\usoclient.exe", "StartScan", System, false,
                [Daily(4, 30) with { RepeatEvery = TimeSpan.FromHours(12) }], microsoft),
            Task(@"\Microsoft\Windows\DiskCleanup\SilentCleanup", TaskRunState.Ready, At(-4, 22, 5), 0, null, "Microsoft Corporation",
                @"%windir%\system32\cleanmgr.exe", "/autoclean /d %systemdrive%", null, true,
                [new TaskTrigger(TaskTriggerKind.Idle)], microsoft, description: "Maintenance task used by the system to launch a silent auto disk cleanup when running low on free disk space."),
            Task(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", TaskRunState.Ready, At(0, 3, 0), 0, At(1, 3, 0), "Microsoft Corporation",
                @"%windir%\system32\compattelrunner.exe", null, System, true,
                [Daily(3)], microsoft),
            Task(@"\Microsoft\Windows\Time Synchronization\SynchronizeTime", TaskRunState.Ready, At(-5, 1), 0, At(2, 1), "$(@%SystemRoot%\\system32\\w32time.dll,-202)",
                @"%windir%\system32\sc.exe", "start w32time task_started", "S-1-5-19", false,
                [Weekly(1, DayOfWeek.Sunday)], microsoft),
            Task(@"\Microsoft\Windows\Registry\RegIdleBackup", TaskRunState.Ready, At(-6, 0, 30), 0, At(4, 0, 30), "Microsoft Corporation",
                @"%windir%\system32\rundll32.exe", "regidle.dll,RegIdleBackup", System, false,
                [Weekly(0, DayOfWeek.Monday) with { Interval = 2 }], microsoft),
            Task(@"\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem", TaskRunState.Ready, At(-7, 5), 0, At(0, 5), "Microsoft Corporation",
                @"%windir%\system32\powercfg.exe", "-energy -auto", System, true,
                [Weekly(5, DayOfWeek.Monday)], microsoft),
            Task(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", TaskRunState.Ready, At(0, 12), 0, At(0, 18), "Microsoft Corporation",
                @"%SystemRoot%\System32\wsqmcons.exe", null, System, false,
                [Daily(0) with { RepeatEvery = TimeSpan.FromHours(6) }], microsoft),
        ];
    }
}
