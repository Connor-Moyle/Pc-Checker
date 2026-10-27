namespace PcChecker.Core.Leftovers;

/// <summary>What kind of trace a finding is.</summary>
public enum LeftoverKind
{
    /// <summary>A folder with files in Program Files, ProgramData or AppData.</summary>
    Folder,

    /// <summary>A folder with no files in it at all (only empty sub-folders, or nothing).</summary>
    EmptyFolder,

    /// <summary>A key under HKLM/HKCU\Software\&lt;Vendor&gt;[\&lt;Product&gt;].</summary>
    RegistryKey,

    /// <summary>A Windows service whose program file no longer exists.</summary>
    Service,

    /// <summary>A scheduled task whose action points at a file that no longer exists.</summary>
    ScheduledTask,

    /// <summary>A Start menu shortcut whose target no longer exists.</summary>
    Shortcut,
}

/// <summary>How sure the scan is that a finding belongs to a removed program. Only <see cref="High"/> is pre-ticked.</summary>
public enum LeftoverConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>The folders a leftover folder can live in. Nothing outside these is ever reported or removed.</summary>
public enum LeftoverRoot
{
    ProgramFiles,
    ProgramFilesX86,
    ProgramData,
    LocalAppData,
    RoamingAppData,
    LocalLowAppData,
}

/// <summary>
/// One trace of a program that is no longer installed. <paramref name="Location"/> is what the removal acts on: a full
/// folder or shortcut path, a short registry key (<c>HKCU\Software\Vendor</c>), a service name or a task path
/// (<c>\Vendor\Task</c>). <paramref name="Target"/> is the missing file a service, task or shortcut points at.
/// <paramref name="CanRemove"/> is false for findings that are only reported (a service that is still running).
/// </summary>
public sealed record LeftoverItem(
    string Id,
    LeftoverKind Kind,
    string Name,
    string Location,
    string Reason,
    long? SizeBytes,
    LeftoverConfidence Confidence,
    bool CanRemove,
    string? Target = null,
    string? Note = null,
    bool MatchesFocus = false,
    DateTimeOffset? LastChanged = null)
{
    /// <summary>Only removable findings the scan is sure about are ticked before the user chooses.</summary>
    public bool IsPreselected => CanRemove && Confidence == LeftoverConfidence.High;
}

/// <summary>
/// The program the user has just uninstalled: its traces are scored High. <paramref name="ProgramId"/> is the inventory
/// id, so the entry can be ignored when its uninstaller has not removed it from the list yet.
/// </summary>
public sealed record LeftoverFocus(string ProgramName, string? Publisher, string? InstallLocation, string? ProgramId = null);

/// <summary>Outcome of a read-only leftover scan.</summary>
public sealed record LeftoverScanResult(
    IReadOnlyList<LeftoverItem> Items,
    int ProgramsCompared,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings,
    DateTimeOffset Captured,
    LeftoverFocus? Focus)
{
    public static LeftoverScanResult Empty(string? warning = null) =>
        new([], 0, TimeSpan.Zero, warning is null ? [] : [warning], DateTimeOffset.Now, null);
}

/// <summary>What happened to one selected finding.</summary>
public sealed record LeftoverItemOutcome(LeftoverItem Item, bool Success, string Message);

/// <summary>Outcome of removing a batch of findings. <paramref name="RestoreEntries"/> lists the undo entries it created.</summary>
public sealed record LeftoverRemovalResult(IReadOnlyList<LeftoverItemOutcome> Outcomes, IReadOnlyList<LeftoverRestoreEntry> RestoreEntries)
{
    public int Removed => Outcomes.Count(o => o.Success);

    public int NotRemoved => Outcomes.Count(o => !o.Success);

    public long FreedBytes => Outcomes.Where(o => o.Success).Sum(o => o.Item.SizeBytes ?? 0);
}

/// <summary>Which removals the restore list can undo (folders and shortcuts go to the Recycle Bin instead).</summary>
public enum LeftoverRestoreKind
{
    RegistryKeys,
    ScheduledTask,
}

/// <summary>
/// One undo entry: a registry backup (restored through the Registry module) or a scheduled task exported to XML before
/// it was deleted. <paramref name="BackupId"/> is the registry backup id or the task backup id (its file name without
/// extension); <paramref name="Location"/> is the task path or the backed-up keys.
/// </summary>
public sealed record LeftoverRestoreEntry(
    string Id,
    LeftoverRestoreKind Kind,
    DateTimeOffset Created,
    string Title,
    string Location,
    string BackupId,
    DateTimeOffset? Restored = null);
