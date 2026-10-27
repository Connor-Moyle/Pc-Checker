namespace PcChecker.Core.Processes;

/// <summary>Which priority classes are offered and what to warn about.</summary>
public static class PriorityRules
{
    private static readonly ProcessPriority[] Everyday =
        [ProcessPriority.Idle, ProcessPriority.BelowNormal, ProcessPriority.Normal, ProcessPriority.AboveNormal, ProcessPriority.High];

    /// <summary>Priorities offered in the menu: Realtime only in Advanced mode.</summary>
    public static IReadOnlyList<ProcessPriority> Available(bool advancedMode) =>
        advancedMode ? [.. Everyday, ProcessPriority.Realtime] : Everyday;

    /// <summary>True when <paramref name="priority"/> may be set with the given mode.</summary>
    public static bool IsAllowed(ProcessPriority priority, bool advancedMode) =>
        Enum.IsDefined(priority) && (priority != ProcessPriority.Realtime || advancedMode);

    /// <summary>Plain label: "Below normal".</summary>
    public static string Label(ProcessPriority? priority) => priority switch
    {
        ProcessPriority.Idle => "Low",
        ProcessPriority.BelowNormal => "Below normal",
        ProcessPriority.Normal => "Normal",
        ProcessPriority.AboveNormal => "Above normal",
        ProcessPriority.High => "High",
        ProcessPriority.Realtime => "Realtime",
        _ => "Unknown",
    };

    /// <summary>Extra sentence for the confirmation dialog, or null when the priority is harmless.</summary>
    public static string? Warning(ProcessPriority priority) => priority switch
    {
        ProcessPriority.Realtime =>
            "Realtime puts this program ahead of Windows itself, including the mouse, keyboard and disk. A busy program at Realtime can freeze the whole PC until it is restarted. Only use it for a short test.",
        ProcessPriority.High =>
            "High priority can make other programs, sound and the mouse stutter while this program is busy.",
        ProcessPriority.Idle =>
            "Low priority means the program only gets the processor when nothing else wants it, so it may run much slower.",
        _ => null,
    };

    /// <summary>Maps a Windows priority-class value (GetPriorityClass) to <see cref="ProcessPriority"/>.</summary>
    public static ProcessPriority? FromPriorityClass(uint value) => value switch
    {
        0x40 => ProcessPriority.Idle,
        0x4000 => ProcessPriority.BelowNormal,
        0x20 => ProcessPriority.Normal,
        0x8000 => ProcessPriority.AboveNormal,
        0x80 => ProcessPriority.High,
        0x100 => ProcessPriority.Realtime,
        _ => null,
    };

    /// <summary>The Windows priority-class value for SetPriorityClass.</summary>
    public static uint ToPriorityClass(ProcessPriority priority) => priority switch
    {
        ProcessPriority.Idle => 0x40,
        ProcessPriority.BelowNormal => 0x4000,
        ProcessPriority.AboveNormal => 0x8000,
        ProcessPriority.High => 0x80,
        ProcessPriority.Realtime => 0x100,
        _ => 0x20,
    };
}
