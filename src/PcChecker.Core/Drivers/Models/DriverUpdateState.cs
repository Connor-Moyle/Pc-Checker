namespace PcChecker.Core.Drivers;

/// <summary>
/// Optional capability of a driver service: reports which of the updates from the last check have already been
/// downloaded. A download of several updates can succeed only partly, so the page asks this after every download
/// instead of assuming all of them arrived.
/// </summary>
public interface IDriverUpdateStateProvider
{
    /// <summary>Returns the subset of <paramref name="updateIds"/> whose files are downloaded and ready to install.</summary>
    Task<IReadOnlySet<string>> GetDownloadedUpdateIdsAsync(IEnumerable<string> updateIds, CancellationToken cancellationToken);
}
