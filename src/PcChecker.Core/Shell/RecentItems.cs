namespace PcChecker.Core.Shell;

/// <summary>The "recently opened" list of quick search: newest first, no duplicates, a fixed number of entries.</summary>
public static class RecentItems
{
    public const int DefaultCapacity = 8;

    /// <summary>Puts <paramref name="id"/> at the front, removes its older copy and trims the list to <paramref name="capacity"/>.</summary>
    public static List<string> Push(IEnumerable<string>? current, string id, int capacity = DefaultCapacity)
    {
        var result = new List<string>();
        if (capacity <= 0)
        {
            return result;
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            result.Add(id);
        }

        foreach (var existing in current ?? [])
        {
            if (result.Count >= capacity)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(existing) && !result.Contains(existing, StringComparer.Ordinal))
            {
                result.Add(existing);
            }
        }

        return result;
    }

    /// <summary>
    /// Drops entries that are no longer in the index (a renamed setting, a page removed in this build), blanks and
    /// duplicates, keeping the order.
    /// </summary>
    public static List<string> Prune(IEnumerable<string>? current, IEnumerable<string> knownIds, int capacity = DefaultCapacity)
    {
        var known = new HashSet<string>(knownIds, StringComparer.Ordinal);
        return (current ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id) && known.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Max(0, capacity))
            .ToList();
    }
}
