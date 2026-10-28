namespace PcChecker.Core.AppHistory;

/// <summary>Where the App history numbers come from.</summary>
public enum AppUsageSource
{
    /// <summary>Windows' own records: the System Resource Usage Monitor (SRUM) database.</summary>
    WindowsRecords,

    /// <summary>PC Checker's own recording, made while PC Checker runs.</summary>
    OwnRecording,
}

/// <summary>Which source the page asks for.</summary>
public enum AppUsageSourceChoice
{
    /// <summary>Windows' records when they can be read, otherwise PC Checker's own recording.</summary>
    Automatic,
    WindowsRecords,
    OwnRecording,
}

/// <summary>The date ranges the page offers.</summary>
public enum AppHistoryRangeKind
{
    Today,
    Last7Days,
    Last30Days,
    SinceDate,
}

/// <summary>A resolved range: everything from <see cref="From"/> to <see cref="To"/>.</summary>
public sealed record AppHistoryRange(DateTimeOffset From, DateTimeOffset To);

/// <summary>What kind of thing a SRUM id stands for.</summary>
public enum SrumIdKind
{
    /// <summary>A program, usually a device path such as <c>\device\harddiskvolume3\...\chrome.exe</c>.</summary>
    Program,

    /// <summary>A Store app (package name) or a Windows service name.</summary>
    PackageOrService,

    /// <summary>A user account (binary SID).</summary>
    User,

    Unknown,
}

/// <summary>One decoded row of SRUM's <c>SruDbIdMapTable</c>.</summary>
public sealed record SrumIdEntry(int Index, SrumIdKind Kind, string Value);

/// <summary>
/// Usage of one app over one period. Windows' records come once an hour (<see cref="Start"/> equals
/// <see cref="End"/>); PC Checker's own recording covers a few minutes per line, rolled up to hours for older days.
/// Values a source does not have are null (PC Checker cannot see network use per app; Windows' records have no
/// separate first-seen time).
/// </summary>
public sealed record AppUsageRecord(
    string AppKey,
    DateTimeOffset Start,
    DateTimeOffset End,
    double CpuSeconds,
    long? BytesSent,
    long? BytesReceived,
    long? MeteredBytes,
    long? DiskReadBytes,
    long? DiskWriteBytes)
{
    /// <summary>The account SID that used the app, when the source knows it.</summary>
    public string? UserSid { get; init; }

    public long? NetworkBytes => BytesSent is null && BytesReceived is null ? null : (BytesSent ?? 0) + (BytesReceived ?? 0);
}

/// <summary>One app on the page: everything it used within the chosen range.</summary>
public sealed record AppUsageSummary(
    string AppKey,
    string Name,
    string? Path,
    TimeSpan CpuTime,
    long? NetworkBytes,
    long? MeteredBytes,
    long? DiskReadBytes,
    long? DiskWriteBytes,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>The figures on the tiles.</summary>
public sealed record AppUsageTotals(AppUsageSummary? TopCpu, AppUsageSummary? TopNetwork, long? TotalNetworkBytes, long? TotalMeteredBytes, int AppCount);

/// <summary>Why Windows' records could not be read.</summary>
public enum SrumFailureKind
{
    None,

    /// <summary>Not on Windows, or this Windows has no SRUM.</summary>
    Unsupported,

    /// <summary>PC Checker is not running as administrator.</summary>
    NotAdministrator,

    /// <summary>The database file is not there (the service is off or the feature is turned off by policy).</summary>
    Missing,

    /// <summary>The database is in use and could not be copied.</summary>
    Locked,

    /// <summary>Windows refused access to the file.</summary>
    AccessDenied,

    /// <summary>The copy is damaged or was not closed cleanly.</summary>
    Corrupt,

    /// <summary>Windows could not make a snapshot of the drive (Volume Shadow Copy).</summary>
    SnapshotFailed,

    /// <summary>Copying or reading took too long.</summary>
    TimedOut,

    Cancelled,

    Unknown,
}

/// <summary>A failed attempt to read Windows' records: the kind and a plain-language reason.</summary>
public sealed record SrumFailure(SrumFailureKind Kind, string Message);

/// <summary>Everything Windows' records hold: the usage rows plus the id-to-name map, already decoded.</summary>
public sealed record SrumReadResult(IReadOnlyList<AppUsageRecord> Records, int SkippedRows)
{
    /// <summary>Notes about parts that could not be read (a missing table, unknown ids).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>What the service hands the page.</summary>
public sealed record AppHistoryResult(
    AppUsageSource Source,
    IReadOnlyList<AppUsageRecord> Records,
    IReadOnlyDictionary<string, AppIdentity> Apps,
    DateTimeOffset Captured)
{
    /// <summary>Set when Windows' records were wanted but could not be read (the page explains it).</summary>
    public SrumFailure? WindowsRecordsFailure { get; init; }

    /// <summary>The oldest moment the shown source has data for, or null when it has none.</summary>
    public DateTimeOffset? OldestRecord { get; init; }

    /// <summary>True while PC Checker's own recording is switched on (the History setting).</summary>
    public bool OwnRecordingOn { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>How an app is shown: a friendly name, its program file (when there is one) and its publisher.</summary>
public sealed record AppIdentity(string AppKey, string Name, string? Path, string? Publisher);
