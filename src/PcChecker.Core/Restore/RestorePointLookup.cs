namespace PcChecker.Core.Restore;

/// <summary>
/// Pure helper that identifies the restore point a <c>CreateRestorePoint</c> call just produced. Windows returns 0
/// even when the 24-hour frequency limit suppresses creation, so a point only counts when it is genuinely new.
/// </summary>
public static class RestorePointLookup
{
    /// <summary>
    /// Returns the newest point carrying <paramref name="description"/>, or, when Windows rewrote the description, the
    /// newest point whose sequence number is above <paramref name="previousNewest"/> (the newest sequence number seen
    /// before creation; 0 when there were none, null when the list could not be read then). Returns null, never an
    /// unrelated older point, when nothing new appeared.
    /// </summary>
    public static RestorePoint? FindCreated(IReadOnlyList<RestorePoint> points, string description, long? previousNewest)
    {
        ArgumentNullException.ThrowIfNull(points);
        var byDescription = points
            .Where(p => p.Description.Equals(description, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.SequenceNumber)
            .FirstOrDefault();

        if (previousNewest is null)
        {
            return byDescription;
        }

        if (byDescription is not null && byDescription.SequenceNumber > previousNewest.Value)
        {
            return byDescription;
        }

        return points
            .Where(p => p.SequenceNumber > previousNewest.Value)
            .OrderByDescending(p => p.SequenceNumber)
            .FirstOrDefault();
    }
}
