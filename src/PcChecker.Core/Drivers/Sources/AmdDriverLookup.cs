using System.Globalization;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>The current AMD Software: Adrenalin Edition release, read from AMD's driver page.</summary>
public sealed record AmdDriverRelease(string Version, DateTime? ReleaseDate, string DownloadUrl, string ReleaseNotesUrl);

/// <summary>
/// AMD's official Windows driver page (<see cref="DriversPage"/>) links the current Adrenalin auto-detect installer,
/// whose file name carries the Adrenalin version and the build date
/// (<c>amd-software-adrenalin-edition-26.10.1-minimalsetup-261018_web.exe</c>). The installer detects the Radeon GPU
/// itself and fetches the matching package, so the same link serves every supported Radeon card and Ryzen APU.
/// The Windows driver version (32.0.21013.1000) cannot be turned into an Adrenalin version, so the installed
/// Adrenalin version comes from the <c>RadeonSoftwareVersion</c> value the AMD driver writes to its registry key. Pure.
/// </summary>
public static partial class AmdDriverLookup
{
    public const string DriversPage = "https://www.amd.com/en/support/download/drivers.html";

    [GeneratedRegex(@"https://drivers\.amd\.com/drivers/installer/[0-9.]+/whql/amd-software-adrenalin-edition-(?<ver>\d{2}\.\d{1,2}\.\d{1,2})-minimalsetup-(?<date>\d{6})_web\.exe",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallerLink();

    /// <summary>Finds the newest Adrenalin installer link on the page; null when the page no longer has one in this form.</summary>
    public static AmdDriverRelease? ParseDriversPage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        AmdDriverRelease? best = null;
        foreach (Match m in InstallerLink().Matches(html))
        {
            var version = m.Groups["ver"].Value;
            if (!DriverVersionMath.IsAdrenalinVersion(version))
            {
                continue;
            }

            var release = new AmdDriverRelease(version, ParseBuildDate(m.Groups["date"].Value), m.Value, ReleaseNotesUrl(version));
            if (best is null || Common.VersionText.IsNewer(release.Version, best.Version))
            {
                best = release;
            }
        }

        return best;
    }

    /// <summary>"261018" → 18 October 2026. Null when it is not a valid yyMMdd date.</summary>
    public static DateTime? ParseBuildDate(string? yymmdd) =>
        yymmdd is { Length: 6 } && DateTime.TryParseExact("20" + yymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    /// <summary>AMD's release notes page for an Adrenalin version: 24.12.1 → …/RN-RAD-WIN-24-12-1.html.</summary>
    public static string ReleaseNotesUrl(string version) =>
        "https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-" + version.Trim().Replace('.', '-') + ".html";

    /// <summary>
    /// Picks the Adrenalin version for a GPU from the display class registry entries: the one whose
    /// <c>MatchingDeviceId</c> the GPU's hardware ids start with. Entries are (MatchingDeviceId, RadeonSoftwareVersion).
    /// </summary>
    public static string? InstalledAdrenalinVersion(IEnumerable<(string? MatchingDeviceId, string? RadeonSoftwareVersion)> entries, DriverInfo gpu)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(gpu);
        var ids = gpu.HardwareIds.Append(gpu.DeviceId).Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        string? best = null;
        int bestLength = -1;
        foreach (var (matching, version) in entries)
        {
            if (string.IsNullOrWhiteSpace(matching) || !DriverVersionMath.IsAdrenalinVersion(version?.Trim()))
            {
                continue;
            }

            var m = matching.Trim();
            if (ids.Any(i => i.StartsWith(m, StringComparison.OrdinalIgnoreCase)) && m.Length > bestLength)
            {
                best = version!.Trim();
                bestLength = m.Length;
            }
        }

        return best;
    }
}
