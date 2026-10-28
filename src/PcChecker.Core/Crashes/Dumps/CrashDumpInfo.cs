namespace PcChecker.Core.Crashes.Dumps;

/// <summary>The file format a dump was written in.</summary>
public enum DumpFormat
{
    /// <summary>A Windows kernel dump with the 64-bit "PAGEDU64" header (minidumps in %SystemRoot%\Minidump, MEMORY.DMP, LiveKernelReports).</summary>
    Kernel64,

    /// <summary>A kernel dump with the old 32-bit "PAGEDUMP" header.</summary>
    Kernel32,

    /// <summary>A user-mode minidump of a program ("MDMP").</summary>
    UserMinidump,
}

/// <summary>What could be read from a dump file's header. Missing parts stay null or empty.</summary>
public sealed record CrashDumpInfo
{
    public required DumpFormat Format { get; init; }

    /// <summary>"Small memory dump", "Kernel memory dump", "Complete memory dump" or "Program memory dump".</summary>
    public string KindText { get; init; } = "Memory dump";

    /// <summary>The raw DUMP_TYPE value of a kernel dump (4 = small/triage); null for user minidumps.</summary>
    public uint? DumpType { get; init; }

    public uint? BugCheckCode { get; init; }

    public IReadOnlyList<ulong> Parameters { get; init; } = [];

    public uint? ExceptionCode { get; init; }

    public ulong? ExceptionAddress { get; init; }

    /// <summary>Windows build number (for example 26100).</summary>
    public int? OsBuild { get; init; }

    /// <summary>"x64", "ARM64" or "x86" when known.</summary>
    public string? Architecture { get; init; }

    /// <summary>When the crash happened, from the header (not the file date).</summary>
    public DateTimeOffset? CrashTime { get; init; }

    /// <summary>Drivers (kernel dumps) or program files (user dumps) loaded at the time, with their address ranges.</summary>
    public IReadOnlyList<CrashModule> Modules { get; init; } = [];

    /// <summary>Raw 8-byte values from the crashing thread's stack (small kernel dumps only), for the stack scan.</summary>
    public IReadOnlyList<ulong> StackValues { get; init; } = [];

    /// <summary>True when the file ended before a part the header pointed at; what was read before that is kept.</summary>
    public bool Truncated { get; init; }
}
