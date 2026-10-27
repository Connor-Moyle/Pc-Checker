using PcChecker.Core.Processes;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>What Task Scheduler says a task is doing now (TASK_STATE).</summary>
public enum TaskRunState
{
    Unknown = 0,
    Disabled = 1,
    Queued = 2,
    Ready = 3,
    Running = 4,
}

/// <summary>The kind of a task trigger, one per trigger element of the task definition.</summary>
public enum TaskTriggerKind
{
    Once,
    Daily,
    Weekly,
    Monthly,
    MonthlyDayOfWeek,
    Boot,
    Logon,
    Idle,
    Event,
    Registration,
    SessionStateChange,
    Custom,
}

/// <summary>One trigger of a task, as read from its XML definition.</summary>
public sealed record TaskTrigger(TaskTriggerKind Kind)
{
    public bool Enabled { get; init; } = true;

    /// <summary>The start time as written (local clock time, or converted to local when the XML carries an offset).</summary>
    public DateTime? Start { get; init; }

    /// <summary>Every how many days (daily) or weeks (weekly).</summary>
    public int Interval { get; init; } = 1;

    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = [];

    /// <summary>Days of the month (1–31) for a monthly trigger.</summary>
    public IReadOnlyList<int> DaysOfMonth { get; init; } = [];

    public bool LastDayOfMonth { get; init; }

    /// <summary>Months (1–12); empty means every month.</summary>
    public IReadOnlyList<int> Months { get; init; } = [];

    /// <summary>Weeks of the month ("1"–"4", "Last") for a monthly day-of-week trigger.</summary>
    public IReadOnlyList<string> Weeks { get; init; } = [];

    /// <summary>Repeats this often after it starts, when set.</summary>
    public TimeSpan? RepeatEvery { get; init; }

    /// <summary>For how long it repeats; null with <see cref="RepeatEvery"/> means indefinitely.</summary>
    public TimeSpan? RepeatFor { get; init; }

    /// <summary>Waits this long after the event before starting.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>The account a log-on or session trigger is limited to, when set.</summary>
    public string? UserId { get; init; }

    /// <summary>SessionStateChange: ConsoleConnect, SessionLock, SessionUnlock…</summary>
    public string? StateChange { get; init; }
}

/// <summary>What a task action does.</summary>
public enum TaskActionKind
{
    Exec,
    ComHandler,
    SendEmail,
    ShowMessage,
}

/// <summary>One action of a task: a program with arguments, or a COM handler, e-mail or message (deprecated kinds).</summary>
public sealed record TaskAction(TaskActionKind Kind, string? Command, string? Arguments = null, string? WorkingDirectory = null, string? ClassId = null);

/// <summary>The parts of a task definition the page shows, read from the task's XML.</summary>
public sealed record TaskDefinition(
    string? Author,
    string? Description,
    IReadOnlyList<TaskTrigger> Triggers,
    IReadOnlyList<TaskAction> Actions,
    string? UserId,
    string? GroupId,
    string? LogonType,
    bool HighestPrivileges,
    bool Hidden,
    bool Enabled)
{
    /// <summary>An empty definition for a task whose XML could not be read.</summary>
    public static TaskDefinition Unreadable { get; } = new(null, null, [], [], null, null, null, false, false, true);

    /// <summary>The URI element (often the task path), when present.</summary>
    public string? Uri { get; init; }

    /// <summary>Runs "whether the user is signed in or not" with a saved password.</summary>
    public bool NeedsPassword => string.Equals(LogonType, "Password", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first program action, when there is one.</summary>
    public TaskAction? FirstProgram => Actions.FirstOrDefault(a => a.Kind == TaskActionKind.Exec && !string.IsNullOrWhiteSpace(a.Command));
}

/// <summary>One scheduled task with its live state from Task Scheduler and what PC Checker worked out about it.</summary>
public sealed record ScheduledTaskInfo(
    string Path,
    TaskRunState State,
    DateTimeOffset? LastRun,
    int? LastResult,
    DateTimeOffset? NextRun,
    TaskDefinition Definition)
{
    /// <summary>The task name (last part of the path).</summary>
    public string Name => TaskPaths.NameOf(Path);

    /// <summary>The folder that holds the task ("\" for the top level).</summary>
    public string Folder => TaskPaths.FolderOf(Path);

    /// <summary>The program file the first action runs, resolved to a full path when possible.</summary>
    public string? ProgramPath { get; init; }

    /// <summary>True when <see cref="ProgramPath"/> is known and the file exists.</summary>
    public bool? ProgramExists { get; init; }

    public SignatureInfo Signature { get; init; } = SignatureInfo.Unknown;

    /// <summary>True for Microsoft's tasks (its folder or author, and not contradicted by the program's signature).</summary>
    public bool IsMicrosoft { get; init; }

    /// <summary>Plain reasons this task looks suspicious; empty when nothing stands out.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>Notes that are not warnings (for example a program that no longer exists).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool IsWindowsTask => TaskPaths.IsWindowsTask(Path);

    public bool IsPcCheckerTask => TaskPaths.IsPcCheckerTask(Path);

    public bool IsFlagged => Flags.Count > 0;
}

/// <summary>The result of reading every task.</summary>
/// <param name="Tasks">The tasks, sorted by path.</param>
/// <param name="Unreadable">Tasks that exist but whose definition Windows would not hand out.</param>
/// <param name="HasLiveState">False when only the definitions could be read (no state, last or next run).</param>
/// <param name="Warning">A plain note when part of the list is missing.</param>
public sealed record ScheduledTaskList(IReadOnlyList<ScheduledTaskInfo> Tasks, int Unreadable, bool HasLiveState, string? Warning)
{
    public static ScheduledTaskList Empty(string? warning) => new([], 0, false, warning);
}

/// <summary>A deleted task whose definition PC Checker saved first, so it can be put back.</summary>
public sealed record TaskBackupEntry(string Id, DateTimeOffset Created, string TaskPath, bool NeedsPassword)
{
    public string Name => TaskPaths.NameOf(TaskPath);
}

/// <summary>Filters of the Scheduled tasks list.</summary>
public enum TaskListFilter
{
    /// <summary>Tasks from other companies, plus any task with a warning (the default view).</summary>
    NotMicrosoft,
    All,
    Microsoft,
    Flagged,
    TurnedOff,
}
