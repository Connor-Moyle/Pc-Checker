namespace PcChecker.Core.Crashes;

/// <summary>User settings for the Crashes page. Lives on <c>AppSettings.Crashes</c>.</summary>
public sealed class CrashSettings
{
    /// <summary>Most crash ids kept; the oldest are dropped first (crashes older than 90 days are not shown anyway).</summary>
    public const int MaxSolved = 500;

    /// <summary>
    /// Crashes marked as solved, by id. They stay in the timeline (marked) but no longer count in the summary card or
    /// the Dashboard's "What needs attention".
    /// </summary>
    public List<string> SolvedIds { get; set; } = [];

    /// <summary>Marks or unmarks a crash; returns true when the list changed.</summary>
    public bool SetSolved(string id, bool solved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        SolvedIds ??= [];
        if (solved)
        {
            if (SolvedIds.Contains(id, StringComparer.Ordinal))
            {
                return false;
            }

            SolvedIds.Add(id);
            if (SolvedIds.Count > MaxSolved)
            {
                SolvedIds.RemoveRange(0, SolvedIds.Count - MaxSolved);
            }

            return true;
        }

        return SolvedIds.RemoveAll(s => string.Equals(s, id, StringComparison.Ordinal)) > 0;
    }

    public bool IsSolved(string id) => SolvedIds?.Contains(id, StringComparer.Ordinal) ?? false;
}
