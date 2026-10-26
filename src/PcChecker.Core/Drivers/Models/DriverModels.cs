using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers;

public enum DriverUpdateSource
{
    WindowsUpdate,
    Vendor,
}

public sealed record DriverInfo(
    string DeviceName,
    string DeviceId,
    string DeviceClass,
    string? Provider,
    string? Version,
    DateTime? Date,
    string? InfName,
    bool IsSigned,
    string? Signer,
    bool IsInbox,
    IReadOnlyList<string> HardwareIds,
    string? Status,
    int ProblemCode,
    string? Location)
{
    /// <summary>
    /// False when Windows still remembers the device but it is not connected right now (an unplugged USB stick,
    /// phone or monitor). Device Manager hides these by default; so does the Drivers page.
    /// </summary>
    public bool IsPresent { get; init; } = true;

    /// <summary>Days since the driver's release date, or null when unknown.</summary>
    public int? AgeDays => Date.HasValue ? Math.Max(0, (int)(DateTime.Today - Date.Value.Date).TotalDays) : null;

    /// <summary>A Device Manager error code on a connected device. Disconnected devices are never counted as problems.</summary>
    public bool HasProblem => IsPresent && ProblemCode != 0;
}

public sealed record DriverUpdateInfo(
    string Id,
    string Title,
    string? Description,
    string? Version,
    DateTime? ReleaseDate,
    long SizeBytes,
    DriverUpdateSource Source,
    string? DeviceId,
    string? KbArticle,
    string? DownloadUrl,
    bool IsDownloaded,
    bool IsInstalled,
    bool RebootRequired)
{
    /// <summary>The driver publisher as Windows Update reports it (DriverProvider), or null when unknown.</summary>
    public string? Provider { get; init; }
}

public sealed record DriverScanResult(IReadOnlyList<DriverInfo> Drivers, IReadOnlyList<string> Warnings, DateTimeOffset Captured)
{
    public static DriverScanResult Empty(string? warning = null) => new([], warning is null ? [] : [warning], DateTimeOffset.Now);
}

public sealed record VendorLink(string Vendor, string Component, string Url, string Note);

public sealed record DriverUpdateCheckResult(
    IReadOnlyList<DriverUpdateInfo> Updates,
    IReadOnlyList<VendorLink> VendorLinks,
    IReadOnlyList<string> Warnings,
    DateTimeOffset Captured)
{
    public static DriverUpdateCheckResult Empty(string? warning = null) => new([], [], warning is null ? [] : [warning], DateTimeOffset.Now);
}

public sealed record DriverInstallResult(
    bool Success,
    bool RebootRequired,
    IReadOnlyList<(DriverUpdateInfo Update, OperationResult Result)> Results);
