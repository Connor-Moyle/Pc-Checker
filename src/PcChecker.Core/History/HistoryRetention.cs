using System.Globalization;

namespace PcChecker.Core.History;

/// <summary>
/// Day files and how long they are kept. History is stored as one file per UTC day named <c>yyyy-MM-dd.jsonl</c>;
/// a day older than the retention setting is deleted, today's file never is.
/// </summary>
public static class HistoryRetention
{
    public const string Extension = ".jsonl";
    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>"2026-10-27.jsonl".</summary>
    public static string FileName(DateOnly day) => day.ToString(DayFormat, CultureInfo.InvariantCulture) + Extension;

    /// <summary>The day a history file holds, or false for any other file name (temp files, backups).</summary>
    public static bool TryParseFileName(string? fileName, out DateOnly day)
    {
        day = default;
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^Extension.Length];
        return DateOnly.TryParseExact(stem, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>The UTC day a moment falls on, which is the file it is written to.</summary>
    public static DateOnly DayOf(DateTimeOffset time) => DateOnly.FromDateTime(time.UtcDateTime);

    /// <summary>The oldest day still kept: with 90 days of retention on 27 October, 29 July.</summary>
    public static DateOnly OldestKept(DateOnly today, int retentionDays) =>
        today.AddDays(-(HistorySettings.ClampRetention(retentionDays) - 1));

    /// <summary>The days among <paramref name="days"/> that fall outside the retention window, oldest first.</summary>
    public static IReadOnlyList<DateOnly> Expired(IEnumerable<DateOnly> days, DateOnly today, int retentionDays)
    {
        ArgumentNullException.ThrowIfNull(days);
        var oldest = OldestKept(today, retentionDays);
        return days.Where(d => d < oldest).Distinct().Order().ToList();
    }

    /// <summary>Every UTC day touched by the range, oldest first (the files to read).</summary>
    public static IReadOnlyList<DateOnly> DaysBetween(DateTimeOffset from, DateTimeOffset to)
    {
        if (to < from)
        {
            return [];
        }

        var first = DayOf(from);
        var last = DayOf(to);
        var days = new List<DateOnly>(last.DayNumber - first.DayNumber + 1);
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            days.Add(d);
        }

        return days;
    }
}
