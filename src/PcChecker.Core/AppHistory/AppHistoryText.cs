using System.Globalization;
using PcChecker.Core.Common;

namespace PcChecker.Core.AppHistory;

/// <summary>How App history figures are written on the page. Pure.</summary>
public static class AppHistoryText
{
    /// <summary>
    /// Marks English interface text in this folder (failure messages, progress stages, notes) so
    /// <c>build/extract-strings.py</c> adds it to the strings to translate. Returns the text unchanged; the page
    /// translates it with Loc.T when it shows it.
    /// </summary>
    internal static string T(string english) => english;

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
}
