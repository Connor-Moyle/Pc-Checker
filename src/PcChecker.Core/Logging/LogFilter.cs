namespace PcChecker.Core.Logging;

/// <summary>The live viewer's filter: a minimum level, an optional category and a free-text search. Pure and testable.</summary>
public sealed record LogFilter(LogLevelSetting MinimumLevel = LogLevelSetting.Verbose, string? Category = null, string? Search = null)
{
    public static LogFilter None { get; } = new();

    /// <summary>True when no restriction is active.</summary>
    public bool IsEmpty => MinimumLevel == LogLevelSetting.Verbose && string.IsNullOrWhiteSpace(Category) && string.IsNullOrWhiteSpace(Search);

    /// <summary>True when the entry passes level, category and search (case-insensitive, matches message, category or exception text).</summary>
    public bool Matches(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Level < MinimumLevel)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Category) && !entry.Category.Equals(Category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        var term = Search.Trim();
        return entry.Message.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (entry.Exception?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Applies the filter to a sequence, preserving order.</summary>
    public IEnumerable<LogEntry> Apply(IEnumerable<LogEntry> entries) => entries.Where(Matches);

    /// <summary>Counts entries per level for the status line ("3 errors, 5 warnings").</summary>
    public static LogCounts Count(IEnumerable<LogEntry> entries)
    {
        int total = 0, warnings = 0, errors = 0;
        foreach (var e in entries)
        {
            total++;
            if (e.Level == LogLevelSetting.Warning)
            {
                warnings++;
            }
            else if (e.Level >= LogLevelSetting.Error)
            {
                errors++;
            }
        }

        return new LogCounts(total, warnings, errors);
    }
}

/// <summary>Totals shown under the live viewer.</summary>
public sealed record LogCounts(int Total, int Warnings, int Errors);
