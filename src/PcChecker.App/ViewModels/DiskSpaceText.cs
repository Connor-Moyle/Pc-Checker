using System.Globalization;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Translatable text for the Disk space page. Callers pass format strings through <see cref="Loc.T"/> so
/// build/extract-strings.py finds them; this class picks the singular or plural form and fills in the numbers.
/// </summary>
internal static class DiskSpaceText
{
    /// <summary>
    /// "1 file" or "12,345 files" from translated formats such as Loc.T("{0} file") and Loc.T("{0} files").
    /// </summary>
    public static string Count(long count, string singularFormat, string pluralFormat)
    {
        var number = count.ToString("#,0", CultureInfo.CurrentCulture);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, count == 1 ? singularFormat : pluralFormat, number);
        }
        catch (FormatException)
        {
            // A translation with broken placeholders must never crash the page.
            return number;
        }
    }

    public static string Files(long count) => Count(count, Loc.T("{0} file"), Loc.T("{0} files"));

    public static string Folders(long count) => Count(count, Loc.T("{0} folder"), Loc.T("{0} folders"));

    public static string Items(long count) => Count(count, Loc.T("{0} item"), Loc.T("{0} items"));

    /// <summary>"Last used 5 months ago".</summary>
    public static string LastUsed(DateTime lastUsed, DateTime now)
    {
        var (unit, count) = DiskSpaceFormat.AgeOf(lastUsed, now);
        return unit switch
        {
            AgeUnit.Today => Loc.T("Last used today"),
            AgeUnit.Days => count == 1 ? Loc.T("Last used yesterday") : Loc.F("Last used {0} days ago", count),
            AgeUnit.Months => count == 1 ? Loc.T("Last used 1 month ago") : Loc.F("Last used {0} months ago", count),
            _ => count == 1 ? Loc.T("Last used 1 year ago") : Loc.F("Last used {0} years ago", count),
        };
    }

    /// <summary>"12 s", "3 min 4 s", "1 h 2 min".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
        {
            return Loc.T("under a second");
        }

        if (duration.TotalMinutes < 1)
        {
            return Loc.F("{0} s", (int)duration.TotalSeconds);
        }

        return duration.TotalHours < 1
            ? Loc.F("{0} min {1} s", (int)duration.TotalMinutes, duration.Seconds)
            : Loc.F("{0} h {1} min", (int)duration.TotalHours, duration.Minutes);
    }

    /// <summary>"312 GB free of 1.82 TB".</summary>
    public static string FreeOf(long freeBytes, long totalBytes) =>
        Loc.F("{0} free of {1}", ByteSize.Format(freeBytes), ByteSize.Format(totalBytes));

    public static string CategoryName(FileTypeCategory category) => category switch
    {
        FileTypeCategory.Video => Loc.T("Video"),
        FileTypeCategory.Images => Loc.T("Images"),
        FileTypeCategory.Games => Loc.T("Games"),
        FileTypeCategory.Archives => Loc.T("Archives and disk images"),
        FileTypeCategory.Installers => Loc.T("Installers"),
        FileTypeCategory.System => Loc.T("Windows and programs"),
        _ => Loc.T("Other"),
    };

    public static string CategoryHint(FileTypeCategory category) => category switch
    {
        FileTypeCategory.Video => Loc.T("Films, clips and game recordings."),
        FileTypeCategory.Images => Loc.T("Photos, screenshots and image projects."),
        FileTypeCategory.Games => Loc.T("Installed games in Steam, Epic, Xbox and other libraries, and game data files."),
        FileTypeCategory.Archives => Loc.T("Zip and other archives, ISO and virtual disk images."),
        FileTypeCategory.Installers => Loc.T("Setup files and installer packages you may no longer need."),
        FileTypeCategory.System => Loc.T("The Windows folder and program libraries."),
        _ => Loc.T("Documents, music, program files and everything else."),
    };
}
