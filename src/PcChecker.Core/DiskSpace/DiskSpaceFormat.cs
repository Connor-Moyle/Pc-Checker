using System.Globalization;
using PcChecker.Core.Common;

namespace PcChecker.Core.DiskSpace;

/// <summary>Display text for the Disk space page: shares, ages, counts and category names. Pure and culture-stable.</summary>
public static class DiskSpaceFormat
{
    /// <summary><paramref name="part"/> as a percentage of <paramref name="total"/>, 0–100 (0 when the total is unknown).</summary>
    public static double Percent(long part, long total) =>
        total <= 0 || part <= 0 ? 0 : Math.Clamp(100.0 * part / total, 0, 100);

    /// <summary>"42 %", "<1 %" for a small non-zero share, "0 %" for nothing.</summary>
    public static string PercentText(long part, long total)
    {
        double p = Percent(part, total);
        if (p <= 0)
        {
            return "0 %";
        }

        return p < 1 ? "<1 %" : Math.Round(p).ToString("0", CultureInfo.InvariantCulture) + " %";
    }

    /// <summary>"1 file", "12,345 files".</summary>
    public static string Count(long count, string singular, string plural) =>
        count == 1 ? "1 " + singular : count.ToString("#,0", CultureInfo.InvariantCulture) + " " + plural;

    /// <summary>How long ago a file was last used: "today", "3 days ago", "5 months ago", "2 years ago".</summary>
    public static string Age(DateTime lastUsed, DateTime now)
    {
        var span = now - lastUsed;
        if (span.TotalDays < 1)
        {
            return "today";
        }

        int days = (int)span.TotalDays;
        if (days < 31)
        {
            return days == 1 ? "yesterday" : $"{days} days ago";
        }

        int months = (now.Year - lastUsed.Year) * 12 + now.Month - lastUsed.Month - (now.Day < lastUsed.Day ? 1 : 0);
        if (months < 12)
        {
            months = Math.Max(1, months);
            return months == 1 ? "1 month ago" : $"{months} months ago";
        }

        int years = months / 12;
        return years == 1 ? "1 year ago" : $"{years} years ago";
    }

    /// <summary>"12 s", "3 min 4 s", "1 h 2 min".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
        {
            return "under a second";
        }

        if (duration.TotalMinutes < 1)
        {
            return $"{(int)duration.TotalSeconds} s";
        }

        return duration.TotalHours < 1
            ? $"{(int)duration.TotalMinutes} min {duration.Seconds} s"
            : $"{(int)duration.TotalHours} h {duration.Minutes} min";
    }

    /// <summary>"312 GB free of 1.82 TB".</summary>
    public static string FreeOf(long freeBytes, long totalBytes) =>
        $"{ByteSize.Format(freeBytes)} free of {ByteSize.Format(totalBytes)}";

    /// <summary>The name the page shows for a category.</summary>
    public static string CategoryName(FileTypeCategory category) => category switch
    {
        FileTypeCategory.Video => "Video",
        FileTypeCategory.Images => "Images",
        FileTypeCategory.Games => "Games",
        FileTypeCategory.Archives => "Archives and disk images",
        FileTypeCategory.Installers => "Installers",
        FileTypeCategory.System => "Windows and programs",
        _ => "Other",
    };

    /// <summary>One line on what a category holds.</summary>
    public static string CategoryHint(FileTypeCategory category) => category switch
    {
        FileTypeCategory.Video => "Films, clips and game recordings.",
        FileTypeCategory.Images => "Photos, screenshots and image projects.",
        FileTypeCategory.Games => "Installed games in Steam, Epic, Xbox and other libraries, and game data files.",
        FileTypeCategory.Archives => "Zip and other archives, ISO and virtual disk images.",
        FileTypeCategory.Installers => "Setup files and installer packages you may no longer need.",
        FileTypeCategory.System => "The Windows folder and program libraries.",
        _ => "Documents, music, program files and everything else.",
    };

    /// <summary>A long path shortened in the middle for the progress line: "C:\Users\…\Steam\steamapps".</summary>
    public static string ShortenPath(string path, int maxLength = 60)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length <= maxLength || maxLength < 10)
        {
            return path;
        }

        int head = maxLength / 3;
        int tail = maxLength - head - 1;
        return path[..head] + "…" + path[^tail..];
    }
}
