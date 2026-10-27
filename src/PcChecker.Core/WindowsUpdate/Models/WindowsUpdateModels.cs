namespace PcChecker.Core.WindowsUpdate;

/// <summary>What kind of update this is, decided by <see cref="UpdateClassifier"/>.</summary>
public enum UpdateClassification
{
    /// <summary>The monthly cumulative update for Windows or .NET (also a security update).</summary>
    Cumulative,

    /// <summary>A security update that is not a cumulative one.</summary>
    Security,

    /// <summary>A new Windows version (feature update or upgrade).</summary>
    Feature,

    /// <summary>Microsoft Defender security intelligence (virus definition) updates.</summary>
    Definition,

    /// <summary>A driver. Pending drivers are listed on the Drivers page; history shows them here too.</summary>
    Driver,

    /// <summary>Any other update (critical fixes, tools, rollups, servicing stack).</summary>
    Other,
}

/// <summary>The Microsoft Security Response Center rating of an update (<c>IUpdate.MsrcSeverity</c>).</summary>
public enum UpdateSeverity
{
    Unspecified,
    Low,
    Moderate,
    Important,
    Critical,
}

/// <summary>Whether installing an update needs a restart (<c>InstallationBehavior.RebootBehavior</c>).</summary>
public enum UpdateRestartNeed
{
    No,
    Maybe,
    Yes,
}

/// <summary>The outcome of one Windows Update history entry.</summary>
public enum UpdateHistoryOutcome
{
    Succeeded,
    SucceededWithErrors,
    Failed,
    Cancelled,
    InProgress,
    Unknown,
}

/// <summary>A software update Windows Update offers that is not installed yet.</summary>
public sealed record PendingUpdate(
    string Id,
    string Title,
    string? KbArticle,
    long SizeBytes,
    UpdateClassification Classification,
    UpdateSeverity Severity,
    UpdateRestartNeed Restart,
    bool IsDownloaded,
    string? Description)
{
    /// <summary>Offered as optional (a preview or a feature update) rather than recommended; Settings lists these under Optional updates.</summary>
    public bool IsOptional { get; init; }

    /// <summary>The update may ask questions while it installs, so it has to be installed from Settings.</summary>
    public bool NeedsUserInput { get; init; }

    /// <summary>When Microsoft published or last changed the update, when known.</summary>
    public DateTime? Released { get; init; }

    /// <summary>Microsoft's support page for the update, when it has one.</summary>
    public string? SupportUrl { get; init; }
}

/// <summary>One raw <c>IUpdateHistoryEntry</c> as read from Windows, before <see cref="UpdateHistoryMapper"/> turns it into text.</summary>
public sealed record UpdateHistoryRecord(
    string? Title,
    int Operation,
    int ResultCode,
    int HResult,
    DateTime? DateUtc,
    IReadOnlyList<string> CategoryIds,
    string? UpdateId,
    string? Description);

/// <summary>One install from the Windows Update history, ready to show.</summary>
public sealed record UpdateHistoryEntry(
    string Title,
    string? KbArticle,
    DateTimeOffset? Date,
    UpdateHistoryOutcome Outcome,
    int HResult,
    UpdateClassification Classification,
    string ResultText,
    string? ErrorText)
{
    public bool IsFailure => Outcome is UpdateHistoryOutcome.Failed or UpdateHistoryOutcome.Cancelled;
}

/// <summary>Whether updates are paused through the Settings app, and for how long.</summary>
public sealed record UpdatePauseState(bool IsPaused, DateTimeOffset? PausedSince, DateTimeOffset? PausedUntil)
{
    public static UpdatePauseState NotPaused { get; } = new(false, null, null);
}

/// <summary>The hours Windows avoids restarting the PC. <see cref="IsAutomatic"/> means Windows picks them from usage.</summary>
public sealed record ActiveHours(int StartHour, int EndHour, bool IsAutomatic);

/// <summary>Which Windows Update settings an organisation controls through Group Policy or device management.</summary>
public sealed record UpdatePolicyStatus(
    bool IsManaged,
    bool PauseManaged,
    bool ActiveHoursManaged,
    bool AutomaticUpdatesOff,
    bool UsesUpdateServer,
    bool AccessDisabled,
    IReadOnlyList<string> Notes)
{
    /// <summary>The longest active hours span a policy allows (ActiveHoursMaxRange, 8–17), or null for the usual 18.</summary>
    public int? ActiveHoursMaxSpan { get; init; }

    public static UpdatePolicyStatus None { get; } = new(false, false, false, false, false, false, []);
}

/// <summary>The raw policy values <see cref="UpdatePolicyRules"/> decides from. Missing keys are empty dictionaries.</summary>
public sealed record UpdatePolicyValues(
    IReadOnlyDictionary<string, object?> GroupPolicy,
    IReadOnlyDictionary<string, object?> AutomaticUpdates,
    IReadOnlyDictionary<string, object?> DeviceManagement)
{
    public static UpdatePolicyValues Empty { get; } = new(
        new Dictionary<string, object?>(), new Dictionary<string, object?>(), new Dictionary<string, object?>());
}

/// <summary>Everything the Status part of the Windows Update page shows.</summary>
public sealed record WindowsUpdateStatus(
    DateTimeOffset? LastCheck,
    DateTimeOffset? LastInstall,
    bool RestartPending,
    UpdatePauseState Pause,
    ActiveHours? ActiveHours,
    UpdatePolicyStatus Policy,
    int MaxPauseDays,
    bool ServiceDisabled,
    IReadOnlyList<string> Warnings,
    DateTimeOffset Captured);

/// <summary>The result of a check for software updates.</summary>
public sealed record PendingUpdateScan(IReadOnlyList<PendingUpdate> Updates, IReadOnlyList<string> Warnings, DateTimeOffset Captured);

/// <summary>The most recent installs from the Windows Update history.</summary>
public sealed record UpdateHistory(IReadOnlyList<UpdateHistoryEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>What happened to one update in an install run.</summary>
public sealed record UpdateInstallItem(PendingUpdate Update, bool Success, string Message, bool RebootRequired);

/// <summary>The per-update results of an install run.</summary>
public sealed record UpdateInstallResult(IReadOnlyList<UpdateInstallItem> Items, bool RebootRequired)
{
    public int Installed => Items.Count(i => i.Success);

    public int Failed => Items.Count(i => !i.Success);
}
