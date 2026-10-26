namespace PcChecker.Core.Startup;

/// <summary>Boot-related facts: boot menu timeout, fast startup, hibernation, safe mode, last boot time and duration.</summary>
public sealed record BootConfig(
    int TimeoutSeconds,
    string? DefaultDescription,
    bool FastStartup,
    bool HibernationEnabled,
    bool SafeModeNextBoot,
    bool MemoryDiagnosticScheduled,
    DateTimeOffset? LastBoot,
    double? LastBootSeconds,
    bool LegacyBootMenu)
{
    public static BootConfig Unknown { get; } = new(30, null, false, false, false, false, null, null, false);
}
