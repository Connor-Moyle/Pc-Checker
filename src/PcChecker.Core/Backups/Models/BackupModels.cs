using PcChecker.Core.Restore;

namespace PcChecker.Core.Backups;

/// <summary>One System Restore point as the <c>SystemRestore</c> WMI class reports it.</summary>
public sealed record SystemRestorePoint(long SequenceNumber, string Description, DateTimeOffset Created, int Type, int EventType)
{
    /// <summary>The restore point type in words ("Windows Update", "Device driver install", …).</summary>
    public string TypeText => RestorePointResultCodes.TypeName(Type);

    /// <summary>Who most likely made the point ("PC Checker", "Windows Update", "Windows", …).</summary>
    public string Origin => RestorePointRules.Origin(this);
}

/// <summary>
/// How much room restore points have on one drive (the Volume Shadow Copy storage area). <see cref="MaxBytes"/> is
/// meaningless when <see cref="IsUnbounded"/>; <see cref="MaxPercent"/> is the limit as a share of the drive, when known.
/// </summary>
public sealed record ShadowStorageInfo(long UsedBytes, long AllocatedBytes, long MaxBytes, bool IsUnbounded, double? MaxPercent);

/// <summary>System Protection on one fixed drive. <see cref="IsProtected"/> is null when Windows did not say.</summary>
public sealed record DriveProtection(
    string Drive,
    string? Label,
    bool IsSystemDrive,
    bool? IsProtected,
    long? CapacityBytes,
    ShadowStorageInfo? Storage);

/// <summary>What the System Restore part of the Backups page shows.</summary>
public sealed record RestorePointOverview(
    IReadOnlyList<SystemRestorePoint> Points,
    bool PointsKnown,
    IReadOnlyList<DriveProtection> Drives,
    bool TurnedOffByPolicy,
    IReadOnlyList<string> Warnings,
    DateTimeOffset Captured)
{
    /// <summary>The newest point, or null.</summary>
    public SystemRestorePoint? Newest => Points.Count == 0 ? null : Points.MaxBy(p => p.SequenceNumber);

    /// <summary>The Windows drive, or null when it is not in the list.</summary>
    public DriveProtection? SystemDrive => Drives.FirstOrDefault(d => d.IsSystemDrive);
}

/// <summary>Whether a Windows backup feature is in use.</summary>
public enum BackupFeatureState
{
    /// <summary>Windows did not say (or PC Checker could not read it).</summary>
    Unknown,

    /// <summary>Set up and in use.</summary>
    On,

    /// <summary>Not set up.</summary>
    Off,
}

/// <summary>
/// One Windows backup feature on the page (File History, OneDrive folder backup, Windows Backup): read only, with a
/// link that opens the Windows screen where it is set up.
/// </summary>
public sealed record WindowsBackupItem(string Id, string Name, BackupFeatureState State, string Detail, string OpenTarget, string? OpenArguments);

/// <summary>The result of looking for system image backups (wbadmin), which only runs when the user asks.</summary>
public sealed record SystemImageStatus(bool Checked, int Count, DateTimeOffset? Newest, string Message);

/// <summary>Whether each backup in a location is one file or one folder.</summary>
public enum BackupItemKind
{
    Files,
    Folders,

    /// <summary>A single file that must stay (the debloat undo values); it is shown but never offered for deletion.</summary>
    SingleFile,
}

/// <summary>A place where one PC Checker feature keeps its backups.</summary>
public sealed record AppBackupLocation(
    string Id,
    string Name,
    string Description,
    string Folder,
    string SearchPattern,
    BackupItemKind Kind,
    bool CanDeleteOld,
    string? KeepReason,
    string PageKey)
{
    /// <summary>Files with these extensions next to a backup file belong to it (a .reg backup's .json sidecar).</summary>
    public IReadOnlyList<string> CompanionExtensions { get; init; } = [];
}

/// <summary>One backup (file or folder) in a location.</summary>
public sealed record AppBackupItem(string Path, DateTimeOffset Created, long SizeBytes);

/// <summary>A location with what it holds now.</summary>
public sealed record AppBackupSummary(AppBackupLocation Location, IReadOnlyList<AppBackupItem> Items, string? Error)
{
    public int Count => Items.Count;

    public long SizeBytes => Items.Sum(i => i.SizeBytes);

    public DateTimeOffset? Newest => Items.Count == 0 ? null : Items.Max(i => i.Created);

    public bool Exists => Items.Count > 0;
}
