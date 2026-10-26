namespace PcChecker.Core.Drivers;

/// <summary>
/// One third-party driver package in the Windows driver store, as listed by <c>pnputil /enum-drivers</c>.
/// <paramref name="PublishedName"/> is the <c>oemN.inf</c> name Windows assigned when the package was staged.
/// </summary>
public sealed record DriverPackage(
    string PublishedName,
    string? OriginalName,
    string? Provider,
    string? ClassName,
    string? ClassGuid,
    string? Version,
    DateTime? Date,
    string? Signer)
{
    /// <summary>Days since the package date, or null when the date is unknown.</summary>
    public int? AgeDays => Date.HasValue ? Math.Max(0, (int)(DateTime.Today - Date.Value.Date).TotalDays) : null;
}

/// <summary>Result of enumerating the driver store.</summary>
public sealed record DriverStoreResult(IReadOnlyList<DriverPackage> Packages, IReadOnlyList<string> Warnings, DateTimeOffset Captured)
{
    public static DriverStoreResult Empty(string? warning = null) => new([], warning is null ? [] : [warning], DateTimeOffset.Now);
}

/// <summary>
/// Optional capability of a driver service: lists the third-party packages staged in the driver store.
/// The Drivers page shows this in the advanced "Driver store" section when the service supports it.
/// </summary>
public interface IDriverStoreProvider
{
    Task<DriverStoreResult> GetDriverStoreAsync(CancellationToken cancellationToken);
}
