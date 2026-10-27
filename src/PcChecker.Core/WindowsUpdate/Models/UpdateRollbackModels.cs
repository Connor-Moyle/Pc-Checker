namespace PcChecker.Core.WindowsUpdate;

/// <summary>What kind of installed update a row is, decided by <see cref="InstalledUpdateRules"/>.</summary>
public enum InstalledUpdateType
{
    /// <summary>A monthly cumulative update for Windows (the LCU) or for .NET.</summary>
    Cumulative,

    /// <summary>A security update that is not a cumulative one.</summary>
    Security,

    /// <summary>A servicing stack update. Permanent: Windows cannot remove it.</summary>
    ServicingStack,

    /// <summary>A hotfix.</summary>
    Hotfix,

    /// <summary>Any other update.</summary>
    Update,
}

/// <summary>How an installed update is removed.</summary>
public enum UninstallMethod
{
    /// <summary><c>wusa.exe /uninstall /kb:n</c>, for updates installed from a standalone package.</summary>
    Wusa,

    /// <summary>
    /// <c>DISM /Remove-Package</c> with the update's package name. Combined servicing stack and cumulative updates
    /// cannot be removed by wusa; their cumulative part is the <c>Package_for_RollupFix</c> package.
    /// </summary>
    DismPackage,

    /// <summary>Windows cannot remove it (a servicing stack update, or a cumulative update that a newer one replaced).</summary>
    NotRemovable,
}

/// <summary>One <c>Win32_QuickFixEngineering</c> row (what <c>Get-HotFix</c> lists), as read from Windows.</summary>
public sealed record QuickFixRecord(string? HotFixId, string? Description, string? InstalledOn);

/// <summary>The state of a package in <c>DISM /Get-Packages</c> (read with <c>/English</c>).</summary>
public enum DismPackageState
{
    Installed,
    Superseded,
    InstallPending,
    UninstallPending,
    Staged,
    Other,
}

/// <summary>One package from <c>DISM /Online /Get-Packages</c>.</summary>
public sealed record DismPackage(string Identity, DismPackageState State, string? ReleaseType)
{
    /// <summary>The name part of the identity, for example <c>Package_for_RollupFix</c>.</summary>
    public string Name => Identity.Split('~')[0];

    /// <summary>The version part of the identity (after the last <c>~</c>), for example <c>26100.4061.1.10</c>.</summary>
    public Version? Version => System.Version.TryParse(Identity[(Identity.LastIndexOf('~') + 1)..], out var v) ? v : null;
}

/// <summary>One installed update, ready to show on the Roll back tab.</summary>
public sealed record InstalledUpdate(
    string Kb,
    string Title,
    DateTimeOffset? InstalledOn,
    InstalledUpdateType Type)
{
    /// <summary>The Windows Update id from the install history, used to hide the update after it is removed.</summary>
    public string? UpdateId { get; init; }

    /// <summary>The monthly cumulative update for Windows itself (not .NET), which DISM removes by package name.</summary>
    public bool IsWindowsCumulative { get; init; }

    /// <summary>The DISM package that removes this update, when one was found.</summary>
    public string? PackageName { get; init; }

    /// <summary>A newer cumulative update has replaced this one, so it cannot be removed on its own any more.</summary>
    public bool IsSuperseded { get; init; }

    /// <summary>
    /// The title did not say it is a servicing stack update, but Windows lists no separate package for this KB while a
    /// servicing stack package is installed, so it most likely is one (or part of a combined update wusa cannot remove).
    /// Decided from the DISM package list, so it works in every display language and without the update history.
    /// </summary>
    public bool IsLikelyServicingStack { get; init; }
}

/// <summary>How to remove one installed update, or why it cannot be removed. From <see cref="InstalledUpdateRules.PlanUninstall"/>.</summary>
public sealed record UninstallPlan(UninstallMethod Method, string? PackageName, string Explanation)
{
    public bool CanUninstall => Method != UninstallMethod.NotRemovable;
}

/// <summary>The installed updates and anything that could not be read.</summary>
public sealed record InstalledUpdateList(IReadOnlyList<InstalledUpdate> Updates, IReadOnlyList<string> Warnings, DateTimeOffset Captured);

/// <summary>An update to hide: a pending one (its Windows Update id) or a removed one (its id from the history and its KB).</summary>
public sealed record UpdateHideTarget(string? UpdateId, string? Kb, string Title);

/// <summary>An update hidden from Windows Update, so it is not offered or installed.</summary>
public sealed record HiddenUpdate(string Id, string Title, string? Kb, UpdateClassification Classification);

/// <summary>The hidden updates and anything that could not be read.</summary>
public sealed record HiddenUpdateList(IReadOnlyList<HiddenUpdate> Updates, IReadOnlyList<string> Warnings);

/// <summary>Whether this PC can go back to the Windows version it had before the last feature update.</summary>
public enum RollbackState
{
    /// <summary>Going back is possible; <see cref="RollbackWindow.DaysLeft"/> says for how long.</summary>
    Available,

    /// <summary>The time to go back has run out (Windows removes the old version soon after).</summary>
    Expired,

    /// <summary>There is no previous version to go back to (no upgrade, or Windows.old was removed).</summary>
    NotAvailable,

    /// <summary>The window could not be read (for example DISM failed).</summary>
    Unknown,
}

/// <summary>The "go back" window after a feature update, from <c>DISM /Get-OSUninstallWindow</c> and the upgrade date.</summary>
public sealed record RollbackWindow(
    RollbackState State,
    int? WindowDays,
    int? DaysLeft,
    DateTimeOffset? UpgradedOn,
    bool WindowsOldExists,
    string Message,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The last day going back is possible, when the upgrade date is known.</summary>
    public DateTimeOffset? Deadline => UpgradedOn is { } on && WindowDays is { } days ? on.AddDays(days) : null;

    /// <summary>Whole days since the upgrade, when known.</summary>
    public int? DaysSinceUpgrade { get; init; }

    public static RollbackWindow Unknown(string message, IReadOnlyList<string>? warnings = null) =>
        new(RollbackState.Unknown, null, null, null, false, message, warnings ?? []);
}
