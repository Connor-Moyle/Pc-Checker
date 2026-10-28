using System.Globalization;
using PcChecker.Core.Common;

namespace PcChecker.Core.AppHistory;

/// <summary>How App history figures are written on the page. Pure.</summary>
public static class AppHistoryText
{
    /// <summary>Processor time the way Task Manager shows it: hours:minutes:seconds, hours counting past 24 ("27:03:10").</summary>
    public static string CpuTime(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return "0:00:00";
        }

        long totalSeconds = (long)Math.Round(time.TotalSeconds);
        long hours = totalSeconds / 3600;
        int minutes = (int)(totalSeconds / 60 % 60);
        int seconds = (int)(totalSeconds % 60);
        return string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}");
    }

    /// <summary>A byte figure, "0 MB" for nothing, or a dash when the source does not measure it.</summary>
    public static string Bytes(long? bytes) => bytes switch
    {
        null => "—",
        <= 0 => "0 MB",
        _ => ByteSize.Format(bytes.Value),
    };

    /// <summary>The page subtitle: what the numbers cover.</summary>
    public static string RangeLabel(AppHistoryRangeKind kind, DateOnly? since) => kind switch
    {
        AppHistoryRangeKind.Today => "Today",
        AppHistoryRangeKind.Last7Days => "The last 7 days",
        AppHistoryRangeKind.Last30Days => "The last 30 days",
        _ => since is { } date ? "Since " + date.ToString("d", CultureInfo.CurrentCulture) : "Since a date",
    };
}
