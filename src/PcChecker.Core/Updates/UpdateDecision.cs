using PcChecker.Core.Common;

namespace PcChecker.Core.Updates;

/// <summary>Well-known addresses for the project's releases.</summary>
public static class UpdateLinks
{
    public const string Repository = "https://github.com/Connor-Moyle/Pc-Checker";
    public const string ReleasesPage = Repository + "/releases";
    public const string LatestReleasePage = Repository + "/releases/latest";
    public const string ReleasesApi = "https://api.github.com/repos/Connor-Moyle/Pc-Checker/releases";
}

/// <summary>Pure rules for deciding whether a release is an update and for shaping <see cref="AppUpdateInfo"/>.</summary>
public static class UpdateDecision
{
    /// <summary>
    /// True when <paramref name="latest"/> is strictly newer than <paramref name="current"/>. Tolerates "v" prefixes,
    /// build metadata ("1.2.3+abc"), prerelease suffixes ("1.3.0-beta.1") and two- to four-part numbers.
    /// </summary>
    public static bool IsNewer(string? current, string? latest)
    {
        var a = Clean(current);
        var b = Clean(latest);
        if (string.IsNullOrEmpty(b))
        {
            return false;
        }

        if (string.IsNullOrEmpty(a))
        {
            return true;
        }

        int numeric = VersionText.Compare(Numeric(b), Numeric(a));
        if (numeric != 0)
        {
            return numeric > 0;
        }

        // Same numbers: a stable release is newer than its own prerelease ("1.2.0" > "1.2.0-beta.2"),
        // and a later prerelease label is newer than an earlier one.
        var preA = Prerelease(a);
        var preB = Prerelease(b);
        if (preA is null && preB is null)
        {
            return false;
        }

        if (preB is null)
        {
            return true;
        }

        if (preA is null)
        {
            return false;
        }

        return ComparePrerelease(preB, preA) > 0;
    }

    /// <summary>
    /// SemVer 2 §11 ordering of prerelease labels: dot-separated identifiers, numeric ones compared as numbers
    /// (beta.10 &gt; beta.9), numeric before alphanumeric, a shorter label older when every shared identifier matches.
    /// </summary>
    public static int ComparePrerelease(string? a, string? b)
    {
        var left = (a ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries);
        var right = (b ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries);
        int count = Math.Min(left.Length, right.Length);
        for (int i = 0; i < count; i++)
        {
            bool leftNumeric = long.TryParse(left[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long ln);
            bool rightNumeric = long.TryParse(right[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long rn);
            int cmp = (leftNumeric, rightNumeric) switch
            {
                (true, true) => ln.CompareTo(rn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(left[i], right[i], StringComparison.OrdinalIgnoreCase),
            };
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    /// <summary>Builds the info record shown in the UI from a picked GitHub release.</summary>
    public static AppUpdateInfo FromRelease(string currentVersion, GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var latest = release.Version;
        return new AppUpdateInfo(
            Clean(currentVersion),
            latest,
            string.IsNullOrWhiteSpace(release.Body) ? release.Name : release.Body,
            release.PublishedAt,
            release.IsPrerelease,
            IsNewer(currentVersion, latest));
    }

    /// <summary>Removes a leading "v" and any "+build" metadata.</summary>
    public static string Clean(string? version)
    {
        var text = GitHubReleaseParser.NormalizeTag(version);
        int plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }

    private static string Numeric(string version)
    {
        int dash = version.IndexOf('-');
        return dash >= 0 ? version[..dash] : version;
    }

    private static string? Prerelease(string version)
    {
        int dash = version.IndexOf('-');
        return dash >= 0 && dash < version.Length - 1 ? version[(dash + 1)..] : null;
    }
}
