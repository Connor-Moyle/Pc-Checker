namespace PcChecker.Core.AppHistory;

/// <summary>
/// Turns usage records into the page's rows and tiles: one row per app with everything it used inside the chosen
/// range, and the date ranges themselves. Pure.
/// </summary>
public static class AppUsageAggregator
{
    /// <summary>
    /// The range for a choice. Today starts at local midnight; 7 and 30 days start at local midnight that many days
    /// back (today included); "since a date" starts at local midnight of <paramref name="since"/> (clamped to today).
    /// </summary>
    public static AppHistoryRange Resolve(AppHistoryRangeKind kind, DateOnly? since, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var first = kind switch
        {
            AppHistoryRangeKind.Today => today,
            AppHistoryRangeKind.Last7Days => today.AddDays(-6),
            AppHistoryRangeKind.Last30Days => today.AddDays(-29),
            _ => since is { } date && date <= today ? date : today,
        };

        return new AppHistoryRange(LocalMidnight(first, zone), now);
    }

    /// <summary>True when the record overlaps the range.</summary>
    public static bool InRange(AppUsageRecord record, AppHistoryRange range) =>
        record.End >= range.From && record.Start <= range.To;

    /// <summary>
    /// One row per app (grouped by <see cref="AppUsageRecord.AppKey"/>, ignoring case) for the records inside the range,
    /// busiest processor user first. A figure stays null when no record of the app had it (the source does not measure
    /// it). Names come from <paramref name="apps"/>, or from the key when the app is not listed.
    /// </summary>
    public static IReadOnlyList<AppUsageSummary> Summarize(IEnumerable<AppUsageRecord> records, IReadOnlyDictionary<string, AppIdentity> apps, AppHistoryRange range)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(range);
        var groups = new Dictionary<string, Accumulated>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!InRange(record, range) || string.IsNullOrWhiteSpace(record.AppKey))
            {
                continue;
            }

            if (!groups.TryGetValue(record.AppKey, out var totals))
            {
                groups[record.AppKey] = totals = new Accumulated(record.Start, record.End);
            }

            totals.Add(record);
        }

        return groups
            .Select(g =>
            {
                var identity = apps.TryGetValue(g.Key, out var known) ? known : null;
                var t = g.Value;
                return new AppUsageSummary(g.Key, identity?.Name ?? AppNames.ProgramName(g.Key), identity?.Path,
                    TimeSpan.FromSeconds(t.Cpu), t.Network, t.Metered, t.Read, t.Write, t.First, t.Last);
            })
            .Where(s => s.CpuTime > TimeSpan.Zero || s.NetworkBytes > 0 || s.DiskReadBytes > 0 || s.DiskWriteBytes > 0)
            .OrderByDescending(s => s.CpuTime)
            .ThenByDescending(s => s.NetworkBytes ?? 0)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The tiles: the app with the most processor time, the one with the most network data, and the totals.</summary>
    public static AppUsageTotals Totals(IReadOnlyList<AppUsageSummary> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var topCpu = rows.Where(r => r.CpuTime > TimeSpan.Zero).MaxBy(r => r.CpuTime);
        var topNetwork = rows.Where(r => r.NetworkBytes > 0).MaxBy(r => r.NetworkBytes!.Value);
        bool hasNetwork = rows.Any(r => r.NetworkBytes is not null);
        bool hasMetered = rows.Any(r => r.MeteredBytes is not null);
        return new AppUsageTotals(topCpu, topNetwork,
            hasNetwork ? rows.Sum(r => r.NetworkBytes ?? 0) : null,
            hasMetered ? rows.Sum(r => r.MeteredBytes ?? 0) : null,
            rows.Count);
    }

    /// <summary>Rows whose name or path contains <paramref name="search"/> (ignoring case); all rows for an empty search.</summary>
    public static IEnumerable<AppUsageSummary> Filter(IEnumerable<AppUsageSummary> rows, string? search)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrWhiteSpace(search))
        {
            return rows;
        }

        var term = search.Trim();
        return rows.Where(r => r.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || (r.Path?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || r.AppKey.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // A clock change at midnight skips it; the day then starts an hour later.
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private sealed class Accumulated(DateTimeOffset first, DateTimeOffset last)
    {
        public double Cpu;
        public long? Network;
        public long? Metered;
        public long? Read;
        public long? Write;
        public DateTimeOffset First = first;
        public DateTimeOffset Last = last;

        public void Add(AppUsageRecord r)
        {
            Cpu += Math.Max(0, r.CpuSeconds);
            Network = Sum(Network, r.NetworkBytes);
            Metered = Sum(Metered, r.MeteredBytes);
            Read = Sum(Read, r.DiskReadBytes);
            Write = Sum(Write, r.DiskWriteBytes);
            if (r.Start < First)
            {
                First = r.Start;
            }

            if (r.End > Last)
            {
                Last = r.End;
            }
        }

        private static long? Sum(long? total, long? value) =>
            value is null ? total : (total ?? 0) + Math.Max(0, value.Value);
    }
}
