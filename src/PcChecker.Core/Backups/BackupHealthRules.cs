namespace PcChecker.Core.Backups;

/// <summary>How urgent a backup hint is. Lower values come first.</summary>
public enum BackupHintLevel
{
    Critical,
    Warning,
    Suggestion,
}

/// <summary>What a hint's button does on the Backups page.</summary>
public enum BackupHintAction
{
    TurnOnProtection,
    CreateRestorePoint,
    SetMaxSpace,
    OpenWindowsBackup,
}

/// <summary>One plain-language hint. <see cref="Title"/> and <see cref="Detail"/> are English format strings filled with <see cref="Args"/>.</summary>
public sealed record BackupHint(string Id, BackupHintLevel Level, string Title, string Detail, IReadOnlyList<object> Args, string ActionText, BackupHintAction Action);

/// <summary>What the hints are built from. Unknown values stay null and never produce a hint.</summary>
public sealed record BackupHealthFacts
{
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    /// <summary>The Windows drive, for example "C:".</summary>
    public string SystemDrive { get; init; } = "C:";

    /// <summary>Whether System Protection covers the Windows drive; null when unknown.</summary>
    public bool? SystemDriveProtected { get; init; }

    /// <summary>True when the restore point list was read (an empty list then means "none").</summary>
    public bool PointsKnown { get; init; }

    /// <summary>When the newest restore point was made; null when there is none or the list is unknown.</summary>
    public DateTimeOffset? NewestPoint { get; init; }

    /// <summary>The space restore points may use on the Windows drive; null when unknown.</summary>
    public ShadowStorageInfo? SystemDriveStorage { get; init; }

    public BackupFeatureState FileHistory { get; init; } = BackupFeatureState.Unknown;

    public BackupFeatureState OneDriveFolders { get; init; } = BackupFeatureState.Unknown;
}

/// <summary>
/// The hints at the top of the Backups page: System Protection off on the Windows drive, no recent restore point, too
/// little room for restore points, and personal files that nothing backs up. Most urgent first.
/// </summary>
public static class BackupHealthRules
{
    /// <summary>A restore point older than this is "not recent".</summary>
    public const int StaleAfterDays = 30;

    /// <summary>Below this limit Windows deletes older restore points very soon.</summary>
    public const long SmallStorageBytes = 1L << 30;

    public static IReadOnlyList<BackupHint> Evaluate(BackupHealthFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var hints = new List<BackupHint>();
        if (facts.SystemDriveProtected == false)
        {
            hints.Add(new BackupHint("protection.off", BackupHintLevel.Critical, "System Protection is off on {0}",
                "Windows cannot make restore points, so driver and settings changes cannot be rolled back with System Restore.",
                [facts.SystemDrive], "Turn on", BackupHintAction.TurnOnProtection));
        }
        else if (facts.SystemDriveProtected == true)
        {
            if (facts.PointsKnown && IsStale(facts.NewestPoint, facts.Now))
            {
                hints.Add(facts.NewestPoint is null
                    ? new BackupHint("restore.none", BackupHintLevel.Warning, "No restore points yet",
                        "Make one now so you have a known good state to go back to.", [], "Create restore point", BackupHintAction.CreateRestorePoint)
                    : new BackupHint("restore.stale", BackupHintLevel.Warning, "No restore point in the last {0} days",
                        "Make one now so you have a recent state to go back to.", [StaleAfterDays], "Create restore point", BackupHintAction.CreateRestorePoint));
            }

            if (IsSmall(facts.SystemDriveStorage))
            {
                hints.Add(new BackupHint("storage.small", BackupHintLevel.Warning, "Restore points use less than 1 GB — older ones will be deleted quickly",
                    "Windows deletes the oldest restore point whenever a new one does not fit. 5 % of the drive is a good size.",
                    [], "Set space", BackupHintAction.SetMaxSpace));
            }
        }

        if (facts.FileHistory == BackupFeatureState.Off && facts.OneDriveFolders == BackupFeatureState.Off)
        {
            hints.Add(new BackupHint("files.unprotected", BackupHintLevel.Suggestion, "Your personal files are not backed up",
                "Restore points do not keep documents or photos. Turn on OneDrive folder backup or File History.",
                [], "Open Windows Backup", BackupHintAction.OpenWindowsBackup));
        }

        return hints.OrderBy(h => h.Level).ToList();
    }

    /// <summary>True when there is no restore point or the newest is at least <see cref="StaleAfterDays"/> days old.</summary>
    public static bool IsStale(DateTimeOffset? newest, DateTimeOffset now) =>
        newest is not { } time || now - time >= TimeSpan.FromDays(StaleAfterDays);

    /// <summary>True for a known, limited storage area smaller than <see cref="SmallStorageBytes"/>.</summary>
    public static bool IsSmall(ShadowStorageInfo? storage) =>
        storage is { IsUnbounded: false, MaxBytes: > 0 } && storage.MaxBytes < SmallStorageBytes;
}
