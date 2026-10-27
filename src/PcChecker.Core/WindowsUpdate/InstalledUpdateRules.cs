using System.Globalization;
using System.Text.RegularExpressions;
using PcChecker.Core.Drivers;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Builds the installed updates list from the three places Windows keeps them (<c>Win32_QuickFixEngineering</c>, the
/// Windows Update history and the DISM package list) and decides how each one is removed: wusa for standalone
/// packages, DISM with the <c>Package_for_RollupFix</c> package for the Windows cumulative update (wusa cannot remove
/// combined servicing stack and cumulative updates), and never for servicing stack updates. Pure.
/// </summary>
public static partial class InstalledUpdateRules
{
    /// <summary>The DISM package name of the Windows cumulative update (LCU).</summary>
    public const string RollupFixPackage = "Package_for_RollupFix";

    /// <summary>The start of the DISM package name of a servicing stack update (SSU) that came in a combined package.</summary>
    public const string ServicingStackPackagePrefix = "Package_for_ServicingStack";

    // Enablement package releases: the title shows the enabled build, but the RollupFix package keeps the base build
    // the release is serviced from (Windows 10 21H2/22H2 on 19041, Windows 11 23H2 on 22621, 25H2 on 26100).
    private static readonly Dictionary<int, int> ServicingBaseBuilds = new()
    {
        [19042] = 19041,
        [19043] = 19041,
        [19044] = 19041,
        [19045] = 19041,
        [22631] = 22621,
        [26200] = 26100,
    };

    // "Servicing stack" in the Windows display languages PC Checker ships in (titles are localized).
    private static readonly string[] ServicingStackWords =
        ["Servicing Stack", "Wartungsstapel", "pile de maintenance", "pila de servicio", "pila de mantenimiento", "stack di manutenzione", "pilha de manutenção", "pilha de serviço"];

    /// <summary>
    /// Merges the sources into one list, newest first, one row per KB. <paramref name="history"/> supplies titles,
    /// categories and update ids; <paramref name="packages"/> supplies the cumulative update's package name.
    /// </summary>
    public static IReadOnlyList<InstalledUpdate> Merge(IEnumerable<QuickFixRecord> quickFixes, IEnumerable<UpdateHistoryRecord>? history,
        IEnumerable<DismPackage>? packages)
    {
        ArgumentNullException.ThrowIfNull(quickFixes);
        var installs = (history ?? [])
            .Where(h => h.Operation == UpdateHistoryMapper.OperationInstallation && WuaResultCodes.IsSuccess(h.ResultCode) && !string.IsNullOrWhiteSpace(h.Title))
            .Select(h => (Record: h, Kb: UpdateClassifier.ExtractKb(h.Title)))
            .Where(h => h.Kb is not null)
            .GroupBy(h => h.Kb!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.Record.DateUtc ?? DateTime.MinValue).First().Record, StringComparer.OrdinalIgnoreCase);
        var packageList = (packages ?? []).ToList();

        var rows = new List<InstalledUpdate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var qfe in quickFixes)
        {
            if (NormalizeKb(qfe.HotFixId) is not { } kb || !seen.Add(kb))
            {
                continue;
            }

            installs.TryGetValue(kb, out var record);
            var title = record?.Title?.Trim();
            var date = ParseQuickFixDate(qfe.InstalledOn) is { } d ? new DateTimeOffset(d, TimeSpan.Zero) : AsDate(record?.DateUtc);
            var type = Classify(title, qfe.Description, record?.CategoryIds);
            bool windowsCumulative = type == InstalledUpdateType.Cumulative && IsWindowsCumulativeTitle(title);
            var standalone = windowsCumulative ? null : StandalonePackage(packageList, kb);
            rows.Add(new InstalledUpdate(kb, string.IsNullOrWhiteSpace(title) ? FallbackTitle(kb, qfe.Description) : title, date, type)
            {
                UpdateId = string.IsNullOrWhiteSpace(record?.UpdateId) ? null : record!.UpdateId,
                IsWindowsCumulative = windowsCumulative,
                PackageName = standalone?.Identity,
                IsLikelyServicingStack = type is not (InstalledUpdateType.ServicingStack or InstalledUpdateType.Cumulative)
                    && standalone is null && HasServicingStackPackage(packageList) && !IsDotNetTitle(title),
            });
        }

        AssignCumulativePackages(rows, packageList);
        return rows
            .OrderByDescending(r => r.InstalledOn ?? DateTimeOffset.MinValue)
            .ThenByDescending(r => KbNumber(r.Kb) ?? 0)
            .ToList();
    }

    /// <summary>How to remove <paramref name="update"/>, or why Windows cannot.</summary>
    public static UninstallPlan PlanUninstall(InstalledUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Type == InstalledUpdateType.ServicingStack)
        {
            return new UninstallPlan(UninstallMethod.NotRemovable, null,
                "Servicing stack updates are a permanent part of Windows and cannot be removed. Windows needs them to install every later update.");
        }

        if (update.IsLikelyServicingStack)
        {
            return new UninstallPlan(UninstallMethod.NotRemovable, null,
                "Windows lists no separate package for this update, so it most likely came as a servicing stack update (a permanent part of Windows) or inside a combined update that wusa cannot remove. If you are sure it can be removed, use Settings → Windows Update → Update history → Uninstall updates.");
        }

        if (KbNumber(update.Kb) is null)
        {
            return new UninstallPlan(UninstallMethod.NotRemovable, null, "This entry has no KB number, so Windows cannot remove it on its own.");
        }

        if (update.IsWindowsCumulative)
        {
            if (update.IsSuperseded)
            {
                return new UninstallPlan(UninstallMethod.NotRemovable, null,
                    "A newer cumulative update has replaced this one, so it cannot be removed on its own. Only the newest cumulative update can be removed.");
            }

            if (update.PackageName is { } package && DismOutputParser.IsPackageIdentity(package))
            {
                return new UninstallPlan(UninstallMethod.DismPackage, package,
                    "Removed with DISM, because this cumulative update comes bundled with a servicing stack update that wusa cannot remove. The servicing stack part stays.");
            }

            return new UninstallPlan(UninstallMethod.NotRemovable, null,
                "PC Checker could not find this update's package. Use Settings → Windows Update → Update history → Uninstall updates instead.");
        }

        return new UninstallPlan(UninstallMethod.Wusa, null, "Removed with the Windows Update Standalone Installer (wusa).");
    }

    /// <summary>
    /// The <c>Package_for_RollupFix</c> package for a Windows cumulative update: the installed one whose version
    /// matches the build in the title ("… (KB5058411) (26100.4061)"). Releases that ship as an enablement package show
    /// the enabled build in the title ("(26200.6584)") while the package keeps the base build ("26100.6584"), so the
    /// builds are compared through <see cref="ServicingBaseBuild"/>; the revision (UBR) must always be equal. Without a
    /// build in the title, the installed package with the highest version (Windows keeps one installed; older ones are
    /// superseded). Null when none fits.
    /// </summary>
    public static DismPackage? SelectCumulativePackage(IEnumerable<DismPackage> packages, string? title)
    {
        var installed = InstalledRollups(packages);
        if (installed.Count == 0)
        {
            return null;
        }

        if (ParseBuild(title) is { } build)
        {
            return installed.FirstOrDefault(p => p.Version is { } v && SameServicingBuild(v, build) && v.Minor == build.Minor);
        }

        return installed[0];
    }

    /// <summary>
    /// The build a release is serviced from: 26200 (25H2) → 26100, 22631 (23H2) → 22621, 19042 to 19045 → 19041.
    /// Every other build is its own base.
    /// </summary>
    public static int ServicingBaseBuild(int build) => ServicingBaseBuilds.TryGetValue(build, out int baseBuild) ? baseBuild : build;

    /// <summary>The OS build in a cumulative update title, such as "(26100.4061)", or null.</summary>
    public static Version? ParseBuild(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var match = BuildPattern().Match(title);
        return match.Success
            ? new Version(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>Classifies an installed update from its title (any shipped language), the QFE description and categories.</summary>
    public static InstalledUpdateType Classify(string? title, string? quickFixDescription, IEnumerable<string>? categoryIds)
    {
        var text = title ?? string.Empty;
        if (ServicingStackWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return InstalledUpdateType.ServicingStack;
        }

        var classification = UpdateClassifier.Classify(categoryIds, title);
        if (classification == UpdateClassification.Cumulative)
        {
            return InstalledUpdateType.Cumulative;
        }

        // Win32_QuickFixEngineering.Description comes from the package and is not translated.
        var description = (quickFixDescription ?? string.Empty).Trim();
        if (classification == UpdateClassification.Security || description.Equals("Security Update", StringComparison.OrdinalIgnoreCase))
        {
            return InstalledUpdateType.Security;
        }

        return description.Equals("Hotfix", StringComparison.OrdinalIgnoreCase) ? InstalledUpdateType.Hotfix : InstalledUpdateType.Update;
    }

    /// <summary>Short label for the Type column.</summary>
    public static string Label(InstalledUpdateType type) => type switch
    {
        InstalledUpdateType.Cumulative => "Cumulative",
        InstalledUpdateType.Security => "Security",
        InstalledUpdateType.ServicingStack => "Servicing stack",
        InstalledUpdateType.Hotfix => "Hotfix",
        _ => "Update",
    };

    /// <summary>
    /// Reads <c>Win32_QuickFixEngineering.InstalledOn</c>: usually "M/d/yyyy" in every language, sometimes "yyyyMMdd"
    /// or a 16-digit hexadecimal FILETIME on older systems. Null when empty or unreadable.
    /// </summary>
    public static DateTime? ParseQuickFixDate(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length == 16 && long.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long fileTime))
        {
            try
            {
                return DateTime.FromFileTimeUtc(fileTime).Date;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        string[] formats = ["M/d/yyyy", "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm:ss", "yyyyMMdd", "yyyy-MM-dd", "d.M.yyyy"];
        return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            ? DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)
            : null;
    }

    /// <summary>"KB5054979" from "KB5054979", "kb5054979" or "5054979"; null for anything else.</summary>
    public static string? NormalizeKb(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var digits = value.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return digits.Length is >= 5 and <= 8 && digits.All(char.IsAsciiDigit) ? "KB" + digits : null;
    }

    /// <summary>The digits of a KB number, or null.</summary>
    public static int? KbNumber(string? kb) =>
        NormalizeKb(kb) is { } normal && int.TryParse(normal.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

    /// <summary>wusa.exe arguments that remove a KB without asking and without restarting.</summary>
    public static IReadOnlyList<string> WusaUninstallArguments(string kb)
    {
        var number = KbNumber(kb) ?? throw new ArgumentException("Not a KB number: " + kb, nameof(kb));
        return ["/uninstall", "/kb:" + number.ToString(CultureInfo.InvariantCulture), "/quiet", "/norestart"];
    }

    /// <summary>DISM arguments that remove a package without asking and without restarting.</summary>
    public static IReadOnlyList<string> DismRemoveArguments(string packageName)
    {
        if (!DismOutputParser.IsPackageIdentity(packageName))
        {
            throw new ArgumentException("Not a package identity: " + packageName, nameof(packageName));
        }

        return ["/Online", "/Remove-Package", "/PackageName:" + packageName.Trim(), "/Quiet", "/NoRestart", "/English"];
    }

    /// <summary>DISM arguments that list the installed packages in table form.</summary>
    public static IReadOnlyList<string> GetPackagesArguments() => ["/Online", "/Get-Packages", "/Format:Table", "/English"];

    /// <summary>True when a cumulative update title is for Windows itself rather than for .NET.</summary>
    public static bool IsWindowsCumulativeTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && !title.Contains(".NET", StringComparison.OrdinalIgnoreCase);

    private static void AssignCumulativePackages(List<InstalledUpdate> rows, List<DismPackage> packages)
    {
        var installed = InstalledRollups(packages);
        var newest = installed.FirstOrDefault();
        var cumulative = rows.Select((r, i) => (Row: r, Index: i)).Where(x => x.Row.IsWindowsCumulative).ToList();
        if (cumulative.Count == 0)
        {
            return;
        }

        // Titles with a build number say exactly which package is theirs.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, index) in cumulative.Where(x => ParseBuild(x.Row.Title) is not null))
        {
            var build = ParseBuild(row.Title)!;
            var match = SelectCumulativePackage(packages, row.Title);
            if (match is not null)
            {
                claimed.Add(match.Identity);
                rows[index] = row with { PackageName = match.Identity };
            }
            else
            {
                int titleBase = ServicingBaseBuild(build.Major);
                bool replaced = installed.Any(p => p.Version is { } v
                    && (ServicingBaseBuild(v.Major) > titleBase || (ServicingBaseBuild(v.Major) == titleBase && v.Minor > build.Minor)));
                rows[index] = row with { IsSuperseded = replaced };
            }
        }

        // Without a build, the newest such update owns the installed package unless a titled one already does.
        var untitled = cumulative.Where(x => ParseBuild(x.Row.Title) is null)
            .OrderByDescending(x => x.Row.InstalledOn ?? DateTimeOffset.MinValue)
            .ThenByDescending(x => KbNumber(x.Row.Kb) ?? 0)
            .ToList();
        bool packageFree = newest is not null && claimed.Count == 0;
        for (int i = 0; i < untitled.Count; i++)
        {
            var (row, index) = untitled[i];
            rows[index] = i == 0 && packageFree
                ? row with { PackageName = newest!.Identity }
                : row with { IsSuperseded = i > 0 || claimed.Count > 0 };
        }
    }

    private static List<DismPackage> InstalledRollups(IEnumerable<DismPackage> packages) =>
        (packages ?? [])
            .Where(p => p.Name.Equals(RollupFixPackage, StringComparison.OrdinalIgnoreCase) && p.State == DismPackageState.Installed)
            .OrderByDescending(p => p.Version ?? new Version(0, 0))
            .ToList();

    private static bool SameServicingBuild(Version package, Version title) => ServicingBaseBuild(package.Major) == ServicingBaseBuild(title.Major);

    private static bool HasServicingStackPackage(IEnumerable<DismPackage> packages) =>
        packages.Any(p => p.State == DismPackageState.Installed && p.Name.StartsWith(ServicingStackPackagePrefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsDotNetTitle(string? title) => title?.Contains(".NET", StringComparison.OrdinalIgnoreCase) == true;

    private static DismPackage? StandalonePackage(IEnumerable<DismPackage> packages, string kb) =>
        packages.FirstOrDefault(p => p.State == DismPackageState.Installed
            && p.Name.Equals("Package_for_" + kb, StringComparison.OrdinalIgnoreCase));

    private static string FallbackTitle(string kb, string? description)
    {
        var d = description?.Trim();
        return string.IsNullOrEmpty(d) ? "Update " + kb : $"{d} {kb}";
    }

    private static DateTimeOffset? AsDate(DateTime? utc) =>
        utc is { } d && d.Year > 1995 ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : null;

    [GeneratedRegex(@"\((\d{5})\.(\d{1,5})\)", RegexOptions.CultureInvariant)]
    private static partial Regex BuildPattern();
}
