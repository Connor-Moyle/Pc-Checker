using System.Text.RegularExpressions;
using PcChecker.Core.Drivers.Sources;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>
/// BIOS facts shown on the "Your PC" card: how old the installed BIOS is, the hint when it is over two years old,
/// Lenovo's version inside "N3BET70W (1.48 )", and the plain safety note. PC Checker never flashes a BIOS. Pure.
/// </summary>
public static partial class BiosRules
{
    /// <summary>A BIOS older than this many years gets the "check for an update" hint.</summary>
    public const int OldAfterYears = 2;

    /// <summary>Shown wherever a BIOS update is mentioned.</summary>
    public const string FlashWarning =
        "PC Checker never updates the BIOS itself. If you update: use only your maker's file for this exact model, plug the laptop in, close other apps and do not turn the PC off until it restarts by itself.";

    /// <summary>Whole years between the BIOS release date and <paramref name="today"/>; null when the date is unknown or in the future.</summary>
    public static int? AgeYears(DateTime? releaseDate, DateTime today)
    {
        if (releaseDate is not { } d || d.Date > today.Date || d.Year < 1990)
        {
            return null;
        }

        int years = today.Year - d.Year;
        if (today.Month < d.Month || (today.Month == d.Month && today.Day < d.Day))
        {
            years--;
        }

        return Math.Max(0, years);
    }

    /// <summary>True when the BIOS is more than <see cref="OldAfterYears"/> years old.</summary>
    public static bool IsOld(DateTime? releaseDate, DateTime today) =>
        releaseDate is { } d && d.Date <= today.Date && d.Date.AddYears(OldAfterYears) < today.Date;

    /// <summary>"Your BIOS is 3 years old." with what an update can bring; null when it is not old or the date is unknown.</summary>
    public static string? AgeHint(DateTime? releaseDate, DateTime today)
    {
        if (!IsOld(releaseDate, today) || AgeYears(releaseDate, today) is not { } years)
        {
            return null;
        }

        return $"Your BIOS is {years} years old. Makers often release BIOS updates with security fixes, better stability and support for newer processors and memory. Check your maker's page.";
    }

    /// <summary>The version part of a Lenovo BIOS string: "N3BET70W (1.48 )" → "1.48". Null when it has none.</summary>
    public static string? LenovoVersion(string? smbiosVersion)
    {
        if (string.IsNullOrWhiteSpace(smbiosVersion))
        {
            return null;
        }

        var m = LenovoBracket().Match(smbiosVersion);
        return m.Success ? m.Groups["v"].Value : null;
    }

    [GeneratedRegex(@"\(\s*(?<v>\d+\.\d+)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex LenovoBracket();

    /// <summary>Lenovo's BIOS id: the first five characters of "N3BET70W" ("N3BET"); null for other formats.</summary>
    public static string? LenovoBiosId(string? smbiosVersion)
    {
        if (string.IsNullOrWhiteSpace(smbiosVersion))
        {
            return null;
        }

        var m = LenovoId().Match(smbiosVersion.Trim());
        return m.Success ? m.Groups["id"].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"^(?<id>[0-9A-Z]{2}[A-Z0-9]{2}T)\d{2}[A-Z]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LenovoId();

    /// <summary>
    /// The installed BIOS version in the form the maker publishes it: Lenovo's "1.48" from "N3BET70W (1.48 )",
    /// everything else as reported.
    /// </summary>
    public static string? PublishedForm(PcMaker maker, string? smbiosVersion) =>
        maker == PcMaker.Lenovo ? LenovoVersion(smbiosVersion) ?? smbiosVersion?.Trim() : smbiosVersion?.Trim();

    /// <summary>Compares the installed BIOS with the maker's newest; Unknown when either is missing or they are written differently.</summary>
    public static DriverVersionComparison Compare(string? installed, string? latest) =>
        VendorVersionText.Compare(latest, installed) switch
        {
            > 0 => DriverVersionComparison.Newer,
            0 => DriverVersionComparison.Same,
            < 0 => DriverVersionComparison.Older,
            null => DriverVersionComparison.Unknown,
        };
}
