using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers;

/// <summary>How safe it is to remove an old or unused driver finding.</summary>
public enum DriverCleanupStatus
{
    /// <summary>A newer version of the same package is in the driver store and nothing uses this one.</summary>
    SafeToRemove,

    /// <summary>Nothing connected uses it, but there is no newer version to fall back to.</summary>
    ProbablySafe,

    /// <summary>In use, part of Windows, or a kind of driver Windows needs to start. Shown, not selectable by default.</summary>
    Keep,
}

/// <summary>What a cleanup finding is: a driver package in the driver store, or a device Windows remembers.</summary>
public enum DriverCleanupKind
{
    Package,
    DisconnectedDevice,
}

/// <summary>
/// A device Windows remembers but that is not connected right now (a "ghost" device), as listed by
/// <c>pnputil /enum-devices /disconnected</c> or by WMI (<c>Win32_PnPEntity.Present == false</c>).
/// <paramref name="DriverName"/> is the published name of its driver package (<c>oemN.inf</c> or an inbox INF).
/// </summary>
public sealed record DisconnectedDevice(
    string InstanceId,
    string? Description,
    string? ClassName,
    string? ClassGuid,
    string? Manufacturer,
    string? Status,
    string? DriverName)
{
    /// <summary>The device description, or the instance id when Windows has no description.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? InstanceId : Description;
}

/// <summary>
/// One finding of the old-driver scan: a third-party driver package (<see cref="DriverCleanupKind.Package"/>, id is the
/// <c>oemN.inf</c> published name) or a disconnected device that uses one (id is the device instance id).
/// </summary>
public sealed record DriverCleanupItem(
    DriverCleanupKind Kind,
    string Id,
    string Title,
    DriverCleanupStatus Status,
    string Reason)
{
    /// <summary>The driver package (for a package finding, or the package a disconnected device uses).</summary>
    public DriverPackage? Package { get; init; }

    /// <summary>The disconnected device, for a device finding.</summary>
    public DisconnectedDevice? Device { get; init; }

    /// <summary>True when a connected device uses the package.</summary>
    public bool InUse { get; init; }

    /// <summary>Names of the connected devices that use the package.</summary>
    public IReadOnlyList<string> UsedBy { get; init; } = [];

    /// <summary>True when a strictly newer version of the same package is in the driver store.</summary>
    public bool IsSuperseded { get; init; }

    /// <summary>The newest version of the same package, when this one is superseded.</summary>
    public DriverPackage? NewerPackage { get; init; }

    /// <summary>True for classes Windows needs to start or to reach disks, keyboard and mouse (see <see cref="DriverCleanupRules.ProtectedClassNames"/>).</summary>
    public bool IsProtectedClass { get; init; }

    /// <summary>True for packages that ship with Windows (not <c>oemN.inf</c>). These are never removed.</summary>
    public bool IsInbox { get; init; }

    /// <summary>Size of the package's folder in the driver store, or null when unknown.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Disconnected devices that use the package (package findings only).</summary>
    public IReadOnlyList<DisconnectedDevice> DisconnectedDevices { get; init; } = [];

    /// <summary>The <c>oemN.inf</c> name involved: the package itself, or the package a disconnected device uses.</summary>
    public string? PackageName => Kind == DriverCleanupKind.Package ? Id : Device?.DriverName;
}

/// <summary>Headline numbers for the old-driver page.</summary>
public sealed record DriverCleanupSummary(
    int PackagesFound,
    int SafeToRemove,
    int ProbablySafe,
    int Keep,
    long FreeableBytes,
    bool SizesIncomplete,
    int DisconnectedDevices)
{
    public static DriverCleanupSummary Empty { get; } = new(0, 0, 0, 0, 0, false, 0);
}

/// <summary>Result of an old-driver scan.</summary>
public sealed record DriverCleanupScanResult(
    IReadOnlyList<DriverCleanupItem> Items,
    DriverCleanupSummary Summary,
    IReadOnlyList<string> Warnings,
    DateTimeOffset Captured)
{
    public static DriverCleanupScanResult Empty(string? warning = null) =>
        new([], DriverCleanupSummary.Empty, warning is null ? [] : [warning], DateTimeOffset.Now);
}

/// <summary>One action of a removal: forget a disconnected device, or delete a driver package.</summary>
public enum DriverCleanupStepKind
{
    RemoveDevice,
    DeletePackage,
}

/// <summary>
/// One ordered step of a removal plan. <paramref name="Target"/> is the device instance id or the <c>oemN.inf</c> name.
/// <paramref name="AddedAutomatically"/> marks steps the plan added (a disconnected device's package, or the device
/// entries that still point at a selected package) so the confirmation can say so.
/// </summary>
public sealed record DriverCleanupStep(
    DriverCleanupStepKind Kind,
    string Target,
    string Title,
    long? SizeBytes,
    bool AddedAutomatically);

/// <summary>Outcome of one step, with the folder the package was exported to before it was deleted.</summary>
public sealed record DriverCleanupStepResult(DriverCleanupStep Step, OperationResult Result, string? BackupFolder);

/// <summary>Outcome of a removal run.</summary>
public sealed record DriverCleanupRemovalResult(
    IReadOnlyList<DriverCleanupStepResult> Results,
    string? BackupFolder,
    bool RebootRequired)
{
    public int Succeeded => Results.Count(r => r.Result.Success);

    public int Failed => Results.Count(r => !r.Result.Success);

    /// <summary>Driver-store space freed by the packages that were deleted (known sizes only).</summary>
    public long FreedBytes => Results
        .Where(r => r.Result.Success && r.Step.Kind == DriverCleanupStepKind.DeletePackage)
        .Sum(r => r.Step.SizeBytes ?? 0);

    /// <summary>One plain-English line: how many steps worked, the space freed and whether a restart is needed.</summary>
    public string Summary
    {
        get
        {
            if (Results.Count == 0)
            {
                return "Nothing was removed.";
            }

            var text = Failed == 0
                ? (Results.Count == 1 ? "Removed 1 item." : $"Removed all {Results.Count} items.")
                : $"Removed {Succeeded} of {Results.Count} items; {Failed} could not be removed.";
            if (FreedBytes > 0)
            {
                text += $" About {ByteSize.Format(FreedBytes)} freed.";
            }

            return RebootRequired ? text + " Restart Windows to finish." : text;
        }
    }
}
