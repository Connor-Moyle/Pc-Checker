namespace PcChecker.Core.Processes;

/// <summary>How "End task" goes about closing a program.</summary>
public enum EndTaskPlan
{
    /// <summary>Ask the program's windows to close, as clicking their X does, so it can save; force it only if that fails.</summary>
    CloseWindowsFirst,

    /// <summary>End the process straight away (no window, or the program cannot react because it hangs or is suspended).</summary>
    EndNow,
}

/// <summary>Pure decisions behind the Status column and the way programs are ended.</summary>
public static class ProcessStatusRules
{
    /// <summary>Windows thread state "Waiting" (KTHREAD_STATE).</summary>
    public const int ThreadStateWaiting = 5;

    /// <summary>Wait reason "Suspended" (KWAIT_REASON).</summary>
    public const int WaitReasonSuspended = 5;

    /// <summary>Consecutive hung readings before a program is called "Not responding" (one good reading clears it).</summary>
    public const int HungReadingsNeeded = 2;

    /// <summary>
    /// A process is suspended when it has threads and every one of them waits with the reason "Suspended" (as Task
    /// Manager and Process Explorer decide it). A process without threads is exiting, not suspended.
    /// </summary>
    public static bool IsSuspended(IReadOnlyCollection<(int State, int WaitReason)> threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        return threads.Count > 0 && threads.All(t => t.State == ThreadStateWaiting && t.WaitReason == WaitReasonSuspended);
    }

    /// <summary>
    /// The state to show. Suspended wins: a suspended program's windows also stop reacting, and "Not responding" would
    /// send people to End task when Resume is the fix. A window counts as hung only when Windows marks it hung
    /// (IsHungAppWindow) and a test message also times out, and only after <see cref="HungReadingsNeeded"/> readings
    /// in a row (<paramref name="confirmedHung"/>), so a program that is busy for a moment does not flicker.
    /// </summary>
    public static ProcessRunState Decide(bool suspended, bool windowMarkedHung, bool pingTimedOut, bool confirmedHung) =>
        suspended ? ProcessRunState.Suspended
        : windowMarkedHung && pingTimedOut && confirmedHung ? ProcessRunState.NotResponding
        : ProcessRunState.Running;

    /// <summary>State of a group: not responding when any member hangs, suspended when every member is suspended.</summary>
    public static ProcessRunState GroupState(IReadOnlyCollection<ProcessInfo> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Any(m => m.State == ProcessRunState.NotResponding))
        {
            return ProcessRunState.NotResponding;
        }

        return members.Count > 0 && members.All(m => m.State == ProcessRunState.Suspended) ? ProcessRunState.Suspended : ProcessRunState.Running;
    }

    /// <summary>
    /// Close the windows first for a running program with a window; end straight away otherwise (a hung or suspended
    /// program cannot react to a close request, and a program without a window has nothing to close).
    /// </summary>
    public static EndTaskPlan PlanEndTask(bool hasWindow, ProcessRunState state) =>
        hasWindow && state == ProcessRunState.Running ? EndTaskPlan.CloseWindowsFirst : EndTaskPlan.EndNow;

    /// <summary>"Not responding", "Suspended", "Efficiency mode" or an empty string while running normally.</summary>
    public static string Label(ProcessRunState state, bool efficiencyMode) => state switch
    {
        ProcessRunState.NotResponding => "Not responding",
        ProcessRunState.Suspended => "Suspended",
        _ => efficiencyMode ? "Efficiency mode" : string.Empty,
    };
}

/// <summary>Counts hung readings per process so "Not responding" needs several in a row. Pure; the caller passes the readings.</summary>
public sealed class HungWindowTracker
{
    private Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>
    /// Records one reading per process with a window (key → hung now) and returns the keys confirmed as not
    /// responding. Processes missing from <paramref name="readings"/> are forgotten.
    /// </summary>
    public IReadOnlySet<string> Update(IReadOnlyDictionary<string, bool> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var next = new Dictionary<string, int>(StringComparer.Ordinal);
        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, hung) in readings)
        {
            if (!hung)
            {
                continue;
            }

            int count = _counts.GetValueOrDefault(key) + 1;
            next[key] = count;
            if (count >= ProcessStatusRules.HungReadingsNeeded)
            {
                confirmed.Add(key);
            }
        }

        _counts = next;
        return confirmed;
    }
}

/// <summary>
/// Efficiency mode as Task Manager sets it: Windows' EcoQoS power throttling ("execution speed" throttled) plus Low
/// priority; turning it off hands throttling back to Windows and sets Normal priority.
/// </summary>
public static class EfficiencyModeRules
{
    /// <summary>PROCESS_POWER_THROTTLING_EXECUTION_SPEED.</summary>
    public const uint ExecutionSpeed = 0x1;

    /// <summary>PROCESS_POWER_THROTTLING_CURRENT_VERSION.</summary>
    public const uint Version = 1;

    /// <summary>True when the throttling state read from Windows has execution speed throttling switched on.</summary>
    public static bool IsOn(uint controlMask, uint stateMask) =>
        (controlMask & ExecutionSpeed) != 0 && (stateMask & ExecutionSpeed) != 0;

    /// <summary>The throttling masks and priority to apply.</summary>
    public static (uint ControlMask, uint StateMask, ProcessPriority Priority) Request(bool on) =>
        on ? (ExecutionSpeed, ExecutionSpeed, ProcessPriority.Idle) : (0u, 0u, ProcessPriority.Normal);
}
