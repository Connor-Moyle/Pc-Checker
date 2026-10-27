using PcChecker.Core.Optimization;

namespace PcChecker.Core.Cleanup;

/// <summary>How a category frees space.</summary>
public enum CleanupKind
{
    /// <summary>PC Checker deletes the listed files itself, inside the category roots.</summary>
    Files,

    /// <summary>PC Checker only measures the folders; a Windows tool removes them.</summary>
    ReportOnly,

    /// <summary>A Windows tool (DISM, Disk Cleanup, vssadmin, the clipboard API) does the work; folders are only measured.</summary>
    Command,
}

/// <summary>Where a category is shown on the Cleanup page.</summary>
public enum CleanupGroup
{
    Windows,
    Graphics,
    Browsers,
    Apps,
    Games,
    Developer,
    Privacy,
}

/// <summary>Conditions a category needs before it may run (checked at scan and again at clean time).</summary>
[Flags]
public enum CleanupRequirement
{
    None = 0,

    /// <summary>PC Checker must run as administrator.</summary>
    Elevated = 1,

    /// <summary>No restart may be pending (servicing, Windows Update or a driver install).</summary>
    NoPendingReboot = 2,

    /// <summary>No Windows Installer (MSI) install may be running.</summary>
    NoInstallerRunning = 4,

    /// <summary>No file renames may be waiting for the next restart (an install is not finished).</summary>
    NoPendingFileRenames = 8,

    /// <summary>Windows Update must not be installing (TrustedInstaller idle).</summary>
    NoUpdateInstalling = 16,
}

/// <summary>Which Windows tool a <see cref="CleanupKind.Command"/> category runs.</summary>
public enum CleanupCommand
{
    None,

    /// <summary>DISM /StartComponentCleanup (Windows Update cleanup).</summary>
    ComponentCleanup,

    /// <summary>DISM /StartComponentCleanup /ResetBase.</summary>
    ComponentResetBase,

    /// <summary>Selected Disk Cleanup handlers through StateFlags and cleanmgr /sagerun.</summary>
    DiskCleanupHandlers,

    /// <summary>Deletes every restore point except the newest (vssadmin /oldest, repeated).</summary>
    RestorePointsKeepNewest,

    /// <summary>Empties the clipboard and the Win+V clipboard history (pinned items stay).</summary>
    ClipboardClear,
}

/// <summary>
/// One folder a category scans. <paramref name="Patterns"/> and <paramref name="Recurse"/> default to the category's own
/// values; <paramref name="ElevatedOnly"/> folders are only used when PC Checker runs as administrator.
/// </summary>
public sealed record CleanupPath(string Template, IReadOnlyList<string>? Patterns = null, bool? Recurse = null, bool ElevatedOnly = false);

public sealed record CleanupCategory(
    string Id,
    string Name,
    string Description,
    RiskLevel Risk,
    IReadOnlyList<string> PathTemplates,
    IReadOnlyList<string> IncludePatterns,
    bool EnabledByDefault,
    bool Recurse)
{
    public CleanupGroup Group { get; init; } = CleanupGroup.Windows;

    public CleanupKind Kind { get; init; } = CleanupKind.Files;

    /// <summary>Extra folders with their own file patterns or recursion (for example a single file in a shared folder).</summary>
    public IReadOnlyList<CleanupPath> ExtraPaths { get; init; } = [];

    /// <summary>Folders (templates, wildcards allowed) that are never scanned or deleted, whatever the roots say.</summary>
    public IReadOnlyList<string> ExcludePathTemplates { get; init; } = [];

    /// <summary>File names that are never deleted in this category (wildcards allowed).</summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    /// <summary>Only files not written for this many days are removed (0 = the global "skip recent files" rule only).</summary>
    public int MinAgeDays { get; init; }

    /// <summary>One extra line for the card (a side effect or a tip).</summary>
    public string? Hint { get; init; }

    /// <summary>Added to the confirmation dialog when this category is about to be cleaned.</summary>
    public string? ConfirmWarning { get; init; }

    /// <summary>Never cleaned by a scheduled or tray cleanup, whatever its risk.</summary>
    public bool NeverScheduled { get; init; }

    /// <summary>Any emptied sub-folder may be removed (scratch space), not only those two levels below a root.</summary>
    public bool PruneAnywhere { get; init; }

    public CleanupRequirement Requires { get; init; } = CleanupRequirement.None;

    public CleanupCommand Command { get; init; } = CleanupCommand.None;

    /// <summary>Disk Cleanup handler key names under VolumeCaches for <see cref="CleanupCommand.DiskCleanupHandlers"/>.</summary>
    public IReadOnlyList<string> DiskCleanupHandlers { get; init; } = [];

    /// <summary>Every folder the category scans, with patterns and recursion filled in.</summary>
    public IReadOnlyList<CleanupPath> Paths =>
        PathTemplates.Select(t => new CleanupPath(t, IncludePatterns, Recurse))
            .Concat(ExtraPaths.Select(p => p with { Patterns = p.Patterns ?? IncludePatterns, Recurse = p.Recurse ?? Recurse }))
            .ToList();
}

public sealed record CleanupFile(string Path, long SizeBytes, DateTime LastWrite);

public sealed record CategoryScanResult(
    CleanupCategory Category,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<CleanupFile> Files,
    IReadOnlyList<string> Errors)
{
    /// <summary>False when Windows only knows the size after cleaning (some Disk Cleanup handlers, the clipboard).</summary>
    public bool SizeKnown { get; init; } = true;

    /// <summary>Set when a precondition (an app running, a pending restart, not elevated) stops this category for now.</summary>
    public string? BlockedReason { get; init; }
}

public sealed record CleanupScanResult(IReadOnlyList<CategoryScanResult> Categories, long TotalBytes, DateTimeOffset Captured)
{
    public static CleanupScanResult Empty() => new([], 0, DateTimeOffset.Now);
}

public sealed record CleanupRunResult(int DeletedFiles, long FreedBytes, IReadOnlyList<string> Skipped, IReadOnlyList<string> Errors, bool DryRun)
{
    /// <summary>What the Windows tools reported for command categories (one line each).</summary>
    public IReadOnlyList<string> Messages { get; init; } = [];
}

/// <summary>Facts about the machine that decide whether a category may run now.</summary>
public sealed record CleanupConditions(
    bool IsElevated,
    bool PendingReboot,
    bool InstallerRunning,
    bool PendingFileRenames,
    bool UpdateInstalling)
{
    /// <summary>Elevated, nothing pending: everything may run.</summary>
    public static CleanupConditions Clear { get; } = new(true, false, false, false, false);

    /// <summary>When the last Windows feature update was installed, if known.</summary>
    public DateTime? LastFeatureUpdate { get; init; }
}

/// <summary>A running process as the app-running guard sees it: name without ".exe" and the image path when readable.</summary>
public sealed record RunningProcess(string Name, string? ImagePath = null);

/// <summary>
/// Matches a process by name (wildcards allowed) and, when <paramref name="PathContains"/> is set, by a piece of its image
/// path. Several programs share a process name (chrome.exe for Chrome and Chromium, opera.exe for Opera and Opera GX,
/// browser.exe for Yandex), so the path tells them apart. An unreadable path counts as a match (the app may be running).
/// </summary>
public sealed record ProcessMatch(string Name, string? PathContains = null);

/// <summary>The app that must be closed before a category is cleaned, and how to recognise it.</summary>
public sealed record AppRule(string App, IReadOnlyList<ProcessMatch> Processes, string? CloseHint = null);

/// <summary>Size facts a command category reports during the scan.</summary>
public sealed record CommandEstimate(long Bytes, bool SizeKnown, IReadOnlyList<string> Notes)
{
    public static CommandEstimate Unknown(params string[] notes) => new(0, false, notes);
}
