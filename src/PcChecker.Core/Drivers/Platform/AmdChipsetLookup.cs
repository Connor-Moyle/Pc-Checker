using System.Globalization;
using System.Text.RegularExpressions;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Sources;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>AMD Chipset Software as AMD's chipset page lists it.</summary>
public sealed record AmdChipsetRelease(string Version, DateTime? ReleaseDate, string? DownloadUrl, string PageUrl);

/// <summary>
/// AMD Chipset Software: the installed version (the "AMD Chipset Software" entry in Apps) and the newest one from
/// AMD's chipset driver page for the socket (AM5, AM4, sTRX4, sTR5). The page is plain HTML; each download is an
/// article with "Revision Number", "Release Date" and a link such as
/// <c>https://drivers.amd.com/drivers/amd_chipset_software_8.09.28.1124.exe</c>. The parser reads the link first and
/// the text second, so a redesign of the page only costs the date. Pure.
/// </summary>
public static partial class AmdChipsetLookup
{
    /// <summary>AMD's driver page, sent as the referrer like a browser that came from it.</summary>
    public const string Referrer = AmdDriverLookup.DriversPage;

    /// <summary>The page when the socket is unknown: AMD's driver search.</summary>
    public const string GeneralPage = AmdDriverLookup.DriversPage;

    /// <summary>The hosts the chipset lookup may talk to.</summary>
    public static IReadOnlyList<string> Hosts { get; } = ["www.amd.com"];

    /// <summary>The chipset page for a socket; every chipset on a socket uses the same package. Null when the socket has no page.</summary>
    public static string? PageFor(string? socket) => socket switch
    {
        "AM5" => "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am5/b650.html",
        "AM4" => "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am4/b550.html",
        "sTRX4" => "https://www.amd.com/en/support/downloads/drivers.html/chipsets/strx4/trx40.html",
        "sTR5" => "https://www.amd.com/en/support/downloads/drivers.html/chipsets/str5/trx50.html",
        _ => null,
    };

    [GeneratedRegex(@"https://drivers\.amd\.com/drivers/amd_chipset_software_(?<ver>\d+(?:\.\d+){2,3})\.exe", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DownloadLink();

    [GeneratedRegex(@"Release\s+Date\s*</strong>\s*<p>\s*(?<date>\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4})\s*</p>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseDate();

    [GeneratedRegex(@"<h4>\s*AMD\s+Chipset\s+Drivers\s*</h4>.{0,600}?Revision\s+Number\s*</strong>\s*<p>\s*(?<ver>\d+(?:\.\d+){2,3})\s*</p>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex RevisionText();

    /// <summary>The newest AMD Chipset Software on the page; null when the page has none in a recognisable form.</summary>
    public static AmdChipsetRelease? ParsePage(string? html, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        AmdChipsetRelease? best = null;
        foreach (Match m in DownloadLink().Matches(html))
        {
            var release = new AmdChipsetRelease(m.Groups["ver"].Value, DateBefore(html, m.Index), m.Value, pageUrl);
            if (best is null || VendorVersionText.IsNewer(release.Version, best.Version))
            {
                best = release;
            }
        }

        if (best is not null)
        {
            return best;
        }

        // No download link (AMD changed its links): the version text still tells the user whether to look.
        var text = RevisionText().Match(html);
        if (!text.Success)
        {
            return null;
        }

        int after = text.Index + text.Length;
        var date = ReleaseDate().Match(html, after) is { Success: true } d && d.Index - after < 800 ? ParseDate(d.Groups["date"].Value) : null;
        return new AmdChipsetRelease(text.Groups["ver"].Value, date, null, pageUrl);
    }

    /// <summary>The release date in the same download article as the link at <paramref name="linkIndex"/>.</summary>
    private static DateTime? DateBefore(string html, int linkIndex)
    {
        int start = html.LastIndexOf("<article", linkIndex, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || linkIndex - start > 6000)
        {
            start = Math.Max(0, linkIndex - 2000);
        }

        DateTime? found = null;
        foreach (Match m in ReleaseDate().Matches(html[start..linkIndex]))
        {
            found = ParseDate(m.Groups["date"].Value) ?? found;
        }

        return found;
    }

    /// <summary>"2026-10-21" or "10/21/2026" (AMD's US format) → the date; null otherwise.</summary>
    public static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] formats = ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy"];
        return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>
    /// The installed AMD Chipset Software version from uninstall entries (DisplayName, DisplayVersion): the highest
    /// version of an entry named "AMD Chipset Software" (® and ™ ignored). Null when it is not installed.
    /// </summary>
    public static string? InstalledVersion(IEnumerable<(string? DisplayName, string? DisplayVersion)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string? best = null;
        foreach (var (name, version) in entries)
        {
            if (!IsChipsetSoftware(name) || string.IsNullOrWhiteSpace(version) || !VersionText.TryParse(version, out _))
            {
                continue;
            }

            var v = version.Trim();
            if (best is null || VendorVersionText.IsNewer(v, best))
            {
                best = v;
            }
        }

        return best;
    }

    /// <summary>True for the Apps entry AMD's chipset installer writes.</summary>
    public static bool IsChipsetSoftware(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        var n = Regex.Replace(displayName.Replace("®", string.Empty, StringComparison.Ordinal).Replace("™", string.Empty, StringComparison.Ordinal), @"\s+", " ").Trim();
        return n.Equals("AMD Chipset Software", StringComparison.OrdinalIgnoreCase);
    }
}
