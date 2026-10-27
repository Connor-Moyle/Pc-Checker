namespace PcChecker.Core.Processes;

/// <summary>
/// Processes the app never offers to end or re-prioritise: ending them crashes or restarts Windows (or closes PC
/// Checker itself). The check uses the normalized name and the fixed process ids of the kernel.
/// </summary>
public static class CriticalProcessRules
{
    /// <summary>Process id of the System Idle Process.</summary>
    public const int IdlePid = 0;

    /// <summary>Process id of the System (kernel) process.</summary>
    public const int SystemPid = 4;

    private static readonly HashSet<string> CriticalNames = new(StringComparer.Ordinal)
    {
        "system",
        "system idle process",
        "idle",
        "[system process]",
        "secure system",
        "registry",
        "memcompression",
        "memory compression",
        "smss",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "lsaiso",
    };

    /// <summary>Reason given when Windows itself marks the process critical (checked at the moment of acting).</summary>
    public const string MarkedCriticalReason =
        "Windows marks this process as critical: ending it would stop the PC with a blue screen, so PC Checker leaves it alone.";

    /// <summary>True when ending or changing this process is never offered.</summary>
    public static bool IsCritical(string? name, int pid, int ownPid) => Reason(name, pid, ownPid) is not null;

    /// <summary>Why the process is protected, or null when it may be ended.</summary>
    public static string? Reason(string? name, int pid, int ownPid)
    {
        if (pid == ownPid)
        {
            return "This is PC Checker itself. Close it from its window instead.";
        }

        if (pid is IdlePid or SystemPid)
        {
            return "This is part of the Windows kernel and cannot be ended.";
        }

        string key = ProcessCatalog.Normalize(name);
        return CriticalNames.Contains(key)
            ? "Windows needs this process to keep running. Ending it would crash or restart the PC, so PC Checker does not offer it."
            : null;
    }
}
