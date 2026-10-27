using System.Globalization;
using System.Text.RegularExpressions;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Reads the output of <c>DISM /Online /Get-Packages</c> and <c>DISM /Online /Get-OSUninstallWindow</c>. PC Checker
/// runs DISM with <c>/English</c>, but the parser does not depend on the labels: package identities have a fixed shape
/// in every language, and the uninstall window is the only "label : number" line DISM prints. Pure.
/// </summary>
public static partial class DismOutputParser
{
    /// <summary>
    /// Parses the package list, in table format (<c>/Format:Table</c>: identity | state | release type | time) or in
    /// the default list format ("Package Identity : …" followed by "State : …"). Duplicates are dropped.
    /// </summary>
    public static IReadOnlyList<DismPackage> ParsePackages(string? output)
    {
        var packages = new List<DismPackage>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return packages;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? pendingIdentity = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Contains('|'))
            {
                var cells = line.Split('|').Select(c => c.Trim()).ToArray();
                if (IsPackageIdentity(cells[0]))
                {
                    Add(cells[0], cells.Length > 1 ? cells[1] : null, cells.Length > 2 ? cells[2] : null);
                }

                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            if (IsPackageIdentity(value))
            {
                if (pendingIdentity is not null)
                {
                    Add(pendingIdentity, null, null);
                }

                pendingIdentity = value;
            }
            else if (pendingIdentity is not null && ParseState(value) is { } state && state != DismPackageState.Other)
            {
                // The line right after the identity in list format is its state.
                Add(pendingIdentity, value, null);
                pendingIdentity = null;
            }
        }

        if (pendingIdentity is not null)
        {
            Add(pendingIdentity, null, null);
        }

        return packages;

        void Add(string identity, string? state, string? releaseType)
        {
            if (seen.Add(identity))
            {
                packages.Add(new DismPackage(identity, ParseState(state), string.IsNullOrWhiteSpace(releaseType) ? null : releaseType));
            }
        }
    }

    /// <summary>True for a component package identity: <c>name~publickeytoken~arch~language~version</c>.</summary>
    public static bool IsPackageIdentity(string? text) => !string.IsNullOrWhiteSpace(text) && IdentityPattern().IsMatch(text.Trim());

    /// <summary>Maps DISM's English state text (<c>/English</c>). Anything else is <see cref="DismPackageState.Other"/>.</summary>
    public static DismPackageState ParseState(string? text) => (text ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "INSTALLED" => DismPackageState.Installed,
        "SUPERSEDED" => DismPackageState.Superseded,
        "INSTALL PENDING" => DismPackageState.InstallPending,
        "UNINSTALL PENDING" => DismPackageState.UninstallPending,
        "STAGED" => DismPackageState.Staged,
        _ => DismPackageState.Other,
    };

    /// <summary>
    /// The number of days in the uninstall window ("Uninstall Window : 10"), or null when the output has none. Version
    /// lines ("Version: 10.0.26100.1") never match because only a bare whole number is accepted; a line without the
    /// English label only counts when its number is a possible window (at most 60 days).
    /// </summary>
    public static int? ParseUninstallWindow(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        int? found = null;
        foreach (var raw in output.Split('\n'))
        {
            var match = WindowLinePattern().Match(raw.Trim());
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int days))
            {
                if (raw.Contains("Uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    return days;
                }

                if (days <= RollbackRules.MaxWindowDays)
                {
                    found ??= days;
                }
            }
        }

        return found;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]*~[0-9a-fA-F]{16}~[A-Za-z0-9_]*~[A-Za-z\-]*~\d+(\.\d+){1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();

    [GeneratedRegex(@"^[^:\d][^:]*:\s*(\d{1,3})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowLinePattern();
}
