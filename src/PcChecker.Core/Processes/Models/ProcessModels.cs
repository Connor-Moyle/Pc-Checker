namespace PcChecker.Core.Processes;

/// <summary>Result of the Authenticode check of a program file.</summary>
public enum SignatureState
{
    /// <summary>Not checked yet; the check runs in the background a few files at a time.</summary>
    Pending,

    /// <summary>Signed with a trusted signature (embedded or through a Windows catalog).</summary>
    Signed,

    /// <summary>The file carries no signature.</summary>
    Unsigned,

    /// <summary>A signature is present but broken, untrusted or explicitly distrusted.</summary>
    Invalid,

    /// <summary>The file could not be read or its path is not known.</summary>
    Unknown,
}

/// <summary>Signature state and signer name of one program file.</summary>
public sealed record SignatureInfo(SignatureState State, string? Publisher)
{
    public static SignatureInfo Pending { get; } = new(SignatureState.Pending, null);

    public static SignatureInfo Unknown { get; } = new(SignatureState.Unknown, null);

    public bool IsSigned => State == SignatureState.Signed;

    /// <summary>True when the check finished and found no valid signature.</summary>
    public bool IsMissingOrBroken => State is SignatureState.Unsigned or SignatureState.Invalid;
}

/// <summary>Windows priority classes, lowest to highest.</summary>
public enum ProcessPriority
{
    Idle,
    BelowNormal,
    Normal,
    AboveNormal,
    High,
    Realtime,
}

/// <summary>
/// One raw reading of a process as the operating system reports it: cumulative CPU time and I/O byte counts.
/// <see cref="ProcessRateTracker"/> turns two readings into percentages and rates.
/// </summary>
public sealed record ProcessCounters(
    int Pid,
    int ParentPid,
    string Name,
    string? Path,
    string? User,
    DateTimeOffset? StartTime,
    TimeSpan CpuTime,
    long PrivateBytes,
    long ReadBytes,
    long WriteBytes,
    ProcessPriority? Priority)
{
    /// <summary>Identity of this run of the program (Windows reuses process ids).</summary>
    public string Key => ProcessKey.For(Pid, StartTime);
}

/// <summary>One running process as shown on the Processes page.</summary>
public sealed record ProcessInfo(
    int Pid,
    int ParentPid,
    string Name,
    string? Path,
    string? User,
    DateTimeOffset? StartTime,
    double CpuPercent,
    long PrivateBytes,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    double? GpuPercent,
    ProcessPriority? Priority,
    SignatureInfo Signature)
{
    /// <summary>Identity of this run of the program (Windows reuses process ids).</summary>
    public string Key => ProcessKey.For(Pid, StartTime);
}

/// <summary>Everything one refresh of the Processes page shows.</summary>
public sealed record ProcessSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<ProcessInfo> Processes,
    double TotalCpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    bool GpuAvailable,
    IReadOnlyList<string> Warnings)
{
    public static ProcessSnapshot Empty { get; } = new(DateTimeOffset.MinValue, [], 0, 0, 0, false, []);
}

/// <summary>A well-known program: a friendly name and a plain-English explanation of what it does.</summary>
/// <param name="ExeName">Normalized file name without ".exe" (see <see cref="ProcessCatalog.Normalize"/>).</param>
/// <param name="Title">Friendly name, also used to group processes by app.</param>
/// <param name="Description">One or two sentences for "What is this?".</param>
/// <param name="Category">Short category label (Windows, Security, Graphics driver, Game launcher, Browser…).</param>
/// <param name="RunsFromWindowsFolder">True for Windows system programs that only ever run from the Windows folder.</param>
public sealed record ProcessCatalogEntry(string ExeName, string Title, string Description, string Category, bool RunsFromWindowsFolder = false);

/// <summary>Builds the identity key of one run of a program.</summary>
public static class ProcessKey
{
    public static string For(int pid, DateTimeOffset? startTime) =>
        startTime is { } start ? $"{pid}@{start.UtcTicks}" : pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
