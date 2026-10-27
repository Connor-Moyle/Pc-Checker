namespace PcChecker.Core.Processes;

/// <summary>
/// Which Task Manager actions PC Checker offers for a process, with a plain reason when it does not. Builds on
/// <see cref="CriticalProcessRules"/> (never end or change Windows' core processes or PC Checker itself) and adds the
/// rules for pausing, efficiency mode, restarting and memory dumps.
/// </summary>
public static class ProcessSafetyRules
{
    /// <summary>Why the process may not be suspended, or null.</summary>
    public static string? SuspendRefusal(ProcessInfo process, ProcessCategory category, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (CriticalProcessRules.Reason(process.Name, process.Pid, ownPid) is { } reason)
        {
            return reason;
        }

        if (category == ProcessCategory.Windows)
        {
            return "This is part of Windows. Pausing it can freeze the desktop, sound or sign-in, so PC Checker does not offer it.";
        }

        return process.State == ProcessRunState.Suspended ? "It is already suspended. Use Resume to let it run again." : null;
    }

    /// <summary>Why the process may not be resumed, or null. Anything PC Checker could pause may be resumed.</summary>
    public static string? ResumeRefusal(ProcessInfo process, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.Pid == ownPid || process.Pid <= CriticalProcessRules.SystemPid)
        {
            return CriticalProcessRules.Reason(process.Name, process.Pid, ownPid);
        }

        return process.State == ProcessRunState.Suspended ? null : "It is not suspended.";
    }

    /// <summary>Why efficiency mode may not be changed, or null. Windows manages its own processes, as in Task Manager.</summary>
    public static string? EfficiencyRefusal(ProcessInfo process, ProcessCategory category, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (CriticalProcessRules.Reason(process.Name, process.Pid, ownPid) is { } reason)
        {
            return reason;
        }

        return category == ProcessCategory.Windows
            ? "Windows decides how its own processes save power, so efficiency mode is not offered for them (Task Manager does the same)."
            : null;
    }

    /// <summary>
    /// Why "Restart" is not offered, or null. Only programs started from a known file in the user's own session can be
    /// started again the same way; services, other accounts and Windows' own processes are left out, except File
    /// Explorer, which Windows itself starts again.
    /// </summary>
    public static string? RestartRefusal(ProcessInfo process, ProcessCategory category, int ownPid, string? currentUser)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (CriticalProcessRules.Reason(process.Name, process.Pid, ownPid) is { } reason)
        {
            return reason;
        }

        if (IsExplorer(process))
        {
            return null;
        }

        if (category == ProcessCategory.Windows)
        {
            return "This is part of Windows, which starts it again by itself when it is needed.";
        }

        if (!ProcessSuspicionRules.IsFullPath(process.Path))
        {
            return "PC Checker does not know which file started this program, so it cannot start it again.";
        }

        if (process.SessionId == 0)
        {
            return "This runs as a Windows service. Restart it from the Services list instead.";
        }

        if (!string.IsNullOrWhiteSpace(currentUser) && !string.IsNullOrWhiteSpace(process.User)
            && !string.Equals(currentUser, process.User, StringComparison.OrdinalIgnoreCase))
        {
            return $"This program runs for another account ({process.User}), so it is not started again from here.";
        }

        return null;
    }

    /// <summary>
    /// Why no memory dump is offered, or null. Windows' core processes are left out: Local Security Authority holds
    /// sign-in secrets, and dumping the others needs rights Windows does not give.
    /// </summary>
    public static string? DumpRefusal(ProcessInfo process, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(process);
        string name = ProcessCatalog.Normalize(process.Name);
        if (name is "lsass" or "lsaiso")
        {
            return "This process holds sign-in secrets. A copy of its memory would contain them, so PC Checker does not create one.";
        }

        if (process.Pid == ownPid)
        {
            return null;
        }

        return CriticalProcessRules.Reason(process.Name, process.Pid, ownPid) is not null
            ? "Windows protects the memory of its core processes, so a dump file cannot be created for them."
            : null;
    }

    /// <summary>True for File Explorer (the desktop and taskbar).</summary>
    public static bool IsExplorer(ProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return ProcessCatalog.Normalize(process.Name) == "explorer";
    }
}
