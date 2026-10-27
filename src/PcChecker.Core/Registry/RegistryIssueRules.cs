using PcChecker.Core.Optimization;

namespace PcChecker.Core.RegistryHealth;

/// <summary>
/// Per-category rules: plain-English text, risk, whether a fix is offered, whether the category is advanced-only and
/// whether the fix removes a key or a single value. Also builds <see cref="RegistryIssue"/>s consistently for the
/// Windows and Demo services. Pure.
/// </summary>
public static class RegistryIssueRules
{
    private static readonly Dictionary<RegistryIssueCategory, (RegistryCategoryInfo Info, string ShortName)> Table = new()
    {
        [RegistryIssueCategory.UninstallEntries] = (new(RegistryIssueCategory.UninstallEntries,
            "Leftover uninstall entries",
            "Programs still listed in Installed apps although their uninstaller and install folder are gone. Removing the entry only tidies the list.",
            @"…\CurrentVersion\Uninstall (64-bit, 32-bit and per-user)",
            RiskLevel.Safe, SafeToFix: true, AdvancedOnly: false, FixDeletesKey: true), "uninstall entries"),
        [RegistryIssueCategory.AppPaths] = (new(RegistryIssueCategory.AppPaths,
            "Program shortcuts (App Paths)",
            "Names you can type in Run (Win+R) that point at a program that is no longer installed.",
            @"…\CurrentVersion\App Paths",
            RiskLevel.Safe, SafeToFix: true, AdvancedOnly: false, FixDeletesKey: true), "App Paths entries"),
        [RegistryIssueCategory.SharedDlls] = (new(RegistryIssueCategory.SharedDlls,
            "Shared file counters",
            "Usage counters installers keep for shared files. Entries for files that no longer exist are leftovers.",
            @"…\CurrentVersion\SharedDLLs",
            RiskLevel.Moderate, SafeToFix: true, AdvancedOnly: false, FixDeletesKey: false), "shared file counters"),
        [RegistryIssueCategory.FileAssociations] = (new(RegistryIssueCategory.FileAssociations,
            "Broken file associations",
            "\"Open with\" entries and file types that open a program that is gone. Fixing them can clean up the Open with menu.",
            @"Software\Classes and Explorer\FileExts\…\OpenWithList",
            RiskLevel.Moderate, SafeToFix: true, AdvancedOnly: false, FixDeletesKey: true), "file associations"),
        [RegistryIssueCategory.StartupEntries] = (new(RegistryIssueCategory.StartupEntries,
            "Startup entries for missing programs",
            "Programs set to start with Windows that no longer exist. Windows tries to start them at every sign-in and silently fails.",
            @"…\CurrentVersion\Run and RunOnce",
            RiskLevel.Safe, SafeToFix: true, AdvancedOnly: false, FixDeletesKey: false), "startup entries"),
        [RegistryIssueCategory.ComObjects] = (new(RegistryIssueCategory.ComObjects,
            "Orphaned COM components",
            "Registrations of third-party program components whose files were deleted. Only shown in advanced mode; Microsoft components are never touched.",
            @"Software\Classes\CLSID\…\InprocServer32 / LocalServer32",
            RiskLevel.Advanced, SafeToFix: true, AdvancedOnly: true, FixDeletesKey: true), "COM components"),
        [RegistryIssueCategory.MuiCache] = (new(RegistryIssueCategory.MuiCache,
            "Program name cache (MUI cache)",
            "Windows remembers the display names of programs you ran. Entries for programs that are gone are harmless leftovers.",
            @"Classes\Local Settings\…\Shell\MuiCache",
            RiskLevel.Safe, SafeToFix: true, AdvancedOnly: false, FixDeletesKey: false), "MUI cache entries"),
        [RegistryIssueCategory.Fonts] = (new(RegistryIssueCategory.Fonts,
            "Missing font files",
            "Fonts listed as installed whose font file is no longer in the Fonts folder.",
            @"…\Windows NT\CurrentVersion\Fonts",
            RiskLevel.Moderate, SafeToFix: true, AdvancedOnly: false, FixDeletesKey: false), "font entries"),
        [RegistryIssueCategory.HelpFiles] = (new(RegistryIssueCategory.HelpFiles,
            "Missing help files",
            "Help file registrations (.hlp/.chm) for files that were removed with their program.",
            @"…\Windows\Help and HTML Help",
            RiskLevel.Safe, SafeToFix: true, AdvancedOnly: false, FixDeletesKey: false), "help file entries"),
        [RegistryIssueCategory.InstallerFolders] = (new(RegistryIssueCategory.InstallerFolders,
            "Installer folder records",
            "Folders Windows Installer recorded as created by installs, which no longer exist. Only shown in advanced mode.",
            @"…\CurrentVersion\Installer\Folders",
            RiskLevel.Moderate, SafeToFix: true, AdvancedOnly: true, FixDeletesKey: false), "installer folder records"),
        [RegistryIssueCategory.ServicesMissingImage] = (new(RegistryIssueCategory.ServicesMissingImage,
            "Services with a missing program",
            "Background services whose program file is gone. Report only: uninstall the leftover program (or its vendor's cleanup tool) instead of editing services.",
            @"SYSTEM\CurrentControlSet\Services (read only)",
            RiskLevel.Advanced, SafeToFix: false, AdvancedOnly: false, FixDeletesKey: false), "services"),
        [RegistryIssueCategory.ContextMenuHandlers] = (new(RegistryIssueCategory.ContextMenuHandlers,
            "Broken right-click menu handlers",
            "Right-click menu extensions of third-party programs whose files were deleted. Only shown in advanced mode.",
            @"Classes\*, Directory, Folder, Drive\shellex\ContextMenuHandlers",
            RiskLevel.Advanced, SafeToFix: true, AdvancedOnly: true, FixDeletesKey: true), "context-menu handlers"),
    };

    /// <summary>Every category in display order.</summary>
    public static IReadOnlyList<RegistryCategoryInfo> All { get; } =
        Enum.GetValues<RegistryIssueCategory>().Select(c => Table[c].Info).ToList();

    public static RegistryCategoryInfo Info(RegistryIssueCategory category) => Table[category].Info;

    /// <summary>Lower-case plural noun for summaries ("startup entries").</summary>
    public static string ShortName(RegistryIssueCategory category) => Table[category].ShortName;

    /// <summary>Categories shown (and scanned by default) for the current mode.</summary>
    public static IReadOnlyList<RegistryIssueCategory> VisibleCategories(bool advancedMode) =>
        All.Where(i => advancedMode || !i.AdvancedOnly).Select(i => i.Category).ToList();

    /// <summary>
    /// Whether a category may report entries whose target lives under %WINDIR% or belongs to Microsoft. Font, help,
    /// installer-folder and MUI cache records are plain bookkeeping and may; program registrations may not.
    /// </summary>
    public static bool AllowsMicrosoftOwnedTargets(RegistryIssueCategory category) => category is
        RegistryIssueCategory.MuiCache or RegistryIssueCategory.Fonts or RegistryIssueCategory.HelpFiles or RegistryIssueCategory.InstallerFolders;

    /// <summary>
    /// True when a target path or publisher looks like a Microsoft component: under the Windows folder, in a
    /// Microsoft/Windows-named Program Files folder, in WindowsApps, or published by Microsoft.
    /// </summary>
    public static bool LooksMicrosoftOwned(string? targetPath, string? publisher, string windowsDirectory)
    {
        if (!string.IsNullOrWhiteSpace(publisher) && publisher.TrimStart().StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        if (RegistryPathTools.IsUnderDirectory(targetPath, windowsDirectory))
        {
            return true;
        }

        foreach (var segment in targetPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Windows Defender Advanced Threat Protection", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Windows NT", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Windows Kits", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Windows Mail", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Windows Media Player", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Windows Photo Viewer", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Windows Sidebar", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Internet Explorer", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds an issue with the category's rules applied: HKLM file associations are advanced-only, and anything in a
    /// protected location (or a container key) is report-only.
    /// </summary>
    public static RegistryIssue Create(RegistryIssueCategory category, string hive, string keyPath, string? valueName, string problem, string? missingTarget)
    {
        var info = Info(category);
        string root = RegistryPathTools.NormalizeHive(hive);
        string path = (keyPath ?? string.Empty).Trim('\\');
        bool deletesKey = info.FixDeletesKey;

        // File associations: ProgID keys are removed whole; "Open with" list entries are single values.
        if (category == RegistryIssueCategory.FileAssociations && valueName is not null)
        {
            deletesKey = false;
        }

        bool advancedOnly = info.AdvancedOnly || (category == RegistryIssueCategory.FileAssociations && root == "HKLM");
        bool allowed = deletesKey ? ProtectedRegistryPaths.CanDeleteKey(root, path) : ProtectedRegistryPaths.CanDeleteValue(root, path, valueName);
        bool safeToFix = info.SafeToFix && allowed;

        return new RegistryIssue(MakeId(category, root, path, valueName), category, root, path, valueName, problem, missingTarget,
            info.Risk, safeToFix, deletesKey, advancedOnly);
    }

    /// <summary>Stable id: the same entry gets the same id in every scan.</summary>
    public static string MakeId(RegistryIssueCategory category, string hive, string keyPath, string? valueName) =>
        $"{category}|{RegistryPathTools.NormalizeHive(hive)}\\{keyPath.Trim('\\')}|{valueName ?? "*"}".ToLowerInvariant();

    /// <summary>Counts per category, with a zero for each scanned category that had nothing.</summary>
    public static IReadOnlyDictionary<RegistryIssueCategory, int> CountByCategory(IEnumerable<RegistryIssue> issues, IEnumerable<RegistryIssueCategory> scanned)
    {
        var counts = scanned.Distinct().ToDictionary(c => c, _ => 0);
        foreach (var issue in issues)
        {
            counts[issue.Category] = counts.TryGetValue(issue.Category, out var n) ? n + 1 : 1;
        }

        return counts;
    }

    /// <summary>"6 uninstall entries, 3 startup entries and 1 font entry"-style text, biggest group first.</summary>
    public static string Summarize(IEnumerable<RegistryIssue> issues)
    {
        var parts = issues
            .GroupBy(i => i.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => $"{g.Count()} {CountNoun(g.Key, g.Count())}")
            .ToList();

        return parts.Count switch
        {
            0 => "No entries",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }

    /// <summary>One sentence describing a scan, deliberately calm: these are leftovers, not "errors".</summary>
    public static string DescribeScan(RegistryScanResult scan)
    {
        int total = scan.Issues.Count;
        if (total == 0)
        {
            return $"No leftovers found in {scan.KeysScanned:N0} registry keys. Nothing needs doing.";
        }

        int fixable = scan.Issues.Count(i => i.SafeToFix);
        int reportOnly = total - fixable;
        string text = $"{total} leftover {(total == 1 ? "entry" : "entries")} point at things that are no longer on this PC ({Summarize(scan.Issues)}).";
        if (reportOnly > 0)
        {
            text += $" {reportOnly} {(reportOnly == 1 ? "is" : "are")} shown for information only.";
        }

        return text + " Removing them tidies things up; it will not make Windows noticeably faster.";
    }

    /// <summary>Short result text for a fix run.</summary>
    public static string DescribeFix(RegistryFixResult result)
    {
        var parts = new List<string> { $"Removed {result.Fixed} {(result.Fixed == 1 ? "entry" : "entries")}" };
        if (result.Skipped > 0)
        {
            parts.Add($"{result.Skipped} skipped");
        }

        if (result.Failed > 0)
        {
            parts.Add($"{result.Failed} failed");
        }

        string text = string.Join(", ", parts) + ".";
        if (result.BackupId is not null && result.Fixed > 0)
        {
            text += " A backup was saved first; you can undo this under Backups.";
        }

        return text;
    }

    private static string CountNoun(RegistryIssueCategory category, int count)
    {
        string plural = ShortName(category);
        if (count != 1)
        {
            return plural;
        }

        return plural switch
        {
            "services" => "service",
            "file associations" => "file association",
            _ when plural.EndsWith("ies", StringComparison.Ordinal) => plural[..^3] + "y",
            _ when plural.EndsWith('s') => plural[..^1],
            _ => plural,
        };
    }
}
