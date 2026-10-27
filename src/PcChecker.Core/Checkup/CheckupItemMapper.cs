using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Health;
using PcChecker.Core.Optimization;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.Core.Checkup;

/// <summary>
/// Turns each page's scan result into check-up to-do items. Pure: the results go in, plain items come out. Each item
/// says what is wrong, why it matters, what fixing it brings and how risky the fix is; only fixes the owning service
/// can do safely by itself get a <see cref="CheckupFixKind"/>, everything else opens the page.
/// </summary>
public static partial class CheckupItemMapper
{
    /// <summary>Junk below this is not worth a line in the list.</summary>
    public const long MinCleanupBytes = 50 * ByteSize.Megabyte;

    /// <summary>Topic shared by every "restart to finish updates" finding.</summary>
    public const string RestartTopic = "updates.restart";

    /// <summary>Topic shared by every "updates are waiting" finding.</summary>
    public const string PendingUpdatesTopic = "updates.pending";

    /// <summary>Topic shared by every "a device has a problem" finding.</summary>
    public const string DevicesTopic = "devices";

    /// <summary>The whole to-do list for a check-up, ranked (see <see cref="CheckupPrioritizer"/>).</summary>
    public static IReadOnlyList<CheckupItem> MapAll(CheckupScanResults results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var items = new List<CheckupItem>();
        if (results.Health is { } health)
        {
            items.AddRange(FromHealth(health));
        }

        if (results.Cleanup is { } cleanup)
        {
            items.AddRange(FromCleanup(cleanup, results.CleanupCategoryIds));
        }

        if (results.Drivers is { } drivers)
        {
            items.AddRange(FromDriverScan(drivers));
        }

        if (results.DriverUpdates is { } driverUpdates)
        {
            items.AddRange(FromDriverUpdates(driverUpdates));
        }

        items.AddRange(FromWindowsUpdate(results.UpdateStatus, results.PendingUpdates));

        bool startupChecked = results.Startup is not null;
        if (results.Startup is { } startup)
        {
            items.AddRange(FromStartup(startup));
        }

        if (results.Performance is { } performance)
        {
            items.AddRange(FromPerformance(performance, results.Optimizations, skipStartupCount: startupChecked));
        }

        if (results.RecommendedTweaks is { } tweaks && results.TweakStates is { } states)
        {
            items.AddRange(FromDebloat(tweaks, states));
        }

        if (results.OldDrivers is { } oldDrivers)
        {
            items.AddRange(FromOldDrivers(oldDrivers));
        }

        if (results.PawnIo is { } pawnIo)
        {
            items.AddRange(FromPawnIo(pawnIo));
        }

        return CheckupPrioritizer.Rank(items);
    }

    /// <summary>One item per health check that is not good. They open the Health page; nothing there is fixed automatically.</summary>
    public static IReadOnlyList<CheckupItem> FromHealth(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var items = new List<CheckupItem>();
        int index = 0;
        foreach (var item in report.Items)
        {
            index++;
            if (item.Status is not (HealthStatus.Warning or HealthStatus.Critical))
            {
                continue;
            }

            bool critical = item.Status == HealthStatus.Critical;
            items.Add(new CheckupItem(
                $"health.{index}",
                CheckupSource.Health,
                critical ? Severity.Critical : Severity.Medium,
                item.Title,
                string.IsNullOrWhiteSpace(item.Recommendation) ? item.Detail : $"{item.Detail} {item.Recommendation}",
                critical ? "Prevents data loss, a security hole or a PC that stops working." : "A more reliable PC.",
                RiskLevel.Safe,
                CheckupFixKind.None,
                [])
            {
                Topic = HealthTopic(item),
            });
        }

        return items;
    }

    /// <summary>
    /// One item for the junk in the Safe categories that were scanned. Report-only categories (Windows.old) and
    /// anything not rated Safe never count, even if the scan included them.
    /// </summary>
    public static IReadOnlyList<CheckupItem> FromCleanup(CleanupScanResult scan, IReadOnlyCollection<string> categoryIds)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(categoryIds);
        var wanted = new HashSet<string>(categoryIds, StringComparer.OrdinalIgnoreCase);
        var found = scan.Categories
            .Where(c => wanted.Contains(c.Category.Id) && c.Category.Risk == RiskLevel.Safe && !CleanupRules.IsReportOnly(c.Category.Id) && c.TotalBytes > 0)
            .OrderByDescending(c => c.TotalBytes)
            .ToList();
        long total = found.Sum(c => c.TotalBytes);
        if (total < MinCleanupBytes)
        {
            return [];
        }

        var severity = total >= 10 * ByteSize.Gigabyte ? Severity.High : total >= 2 * ByteSize.Gigabyte ? Severity.Medium : Severity.Low;
        var names = string.Join(", ", found.Take(3).Select(c => c.Category.Name)) + (found.Count > 3 ? $" and {found.Count - 3} more" : string.Empty);
        return
        [
            new CheckupItem("cleanup.safe", CheckupSource.Cleanup, severity,
                $"Clean {ByteSize.Format(total)} of temporary files",
                $"Caches and leftovers that Windows and programs no longer need: {names}.",
                $"About {ByteSize.Format(total)} more free space.",
                RiskLevel.Safe, CheckupFixKind.CleanSafeJunk, found.Select(c => c.Category.Id).ToList())
            {
                Magnitude = total / (double)ByteSize.Gigabyte,
            },
        ];
    }

    /// <summary>One item for connected devices with a Device Manager problem. Fixing a driver needs a person, so it opens Drivers.</summary>
    public static IReadOnlyList<CheckupItem> FromDriverScan(DriverScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var problems = scan.Drivers.Where(d => d.HasProblem).ToList();
        if (problems.Count == 0)
        {
            return [];
        }

        var flags = problems.Select(d => (Driver: d, Flag: DriverRules.Evaluate(d).OrderByDescending(f => f.Severity).FirstOrDefault())).ToList();
        var worst = flags.Max(f => f.Flag?.Severity ?? Severity.Medium);
        var first = flags[0];
        return
        [
            new CheckupItem("drivers.problems", CheckupSource.Drivers, worst,
                problems.Count == 1 ? $"{first.Driver.DeviceName} has a driver problem" : $"{problems.Count} devices have a driver problem",
                first.Flag?.Detail ?? "Windows reports that the device is not working properly.",
                "The device works properly again.",
                RiskLevel.Moderate, CheckupFixKind.None, [])
            {
                Topic = DevicesTopic,
                Magnitude = problems.Count,
            },
        ];
    }

    /// <summary>
    /// One item for the driver updates Windows Update offers. Installing is automatic but rated Moderate: a new driver
    /// can change how a device behaves, so it is never ticked for the user.
    /// </summary>
    public static IReadOnlyList<CheckupItem> FromDriverUpdates(DriverUpdateCheckResult check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var updates = check.Updates.Where(u => !u.IsInstalled).ToList();
        if (updates.Count == 0)
        {
            return [];
        }

        var names = string.Join(", ", updates.Take(3).Select(u => u.Title)) + (updates.Count > 3 ? $" and {updates.Count - 3} more" : string.Empty);
        return
        [
            new CheckupItem("drivers.updates", CheckupSource.Drivers, Severity.Medium,
                updates.Count == 1 ? "Install 1 driver update" : $"Install {updates.Count} driver updates",
                $"Windows Update offers newer drivers: {names}.",
                "Fixes and improvements from the hardware makers. A restore point comes first, so you can go back.",
                RiskLevel.Moderate, CheckupFixKind.InstallDriverUpdates, updates.Select(u => u.Id).ToList())
            {
                Magnitude = updates.Count,
                MayNeedRestart = updates.Any(u => u.RebootRequired),
            },
        ];
    }

    /// <summary>
    /// Items for Windows Update: a pending restart, the recommended updates that are waiting (installed the way
    /// "Install all" does), updates that must be installed from Settings, and a turned-off update service.
    /// </summary>
    public static IReadOnlyList<CheckupItem> FromWindowsUpdate(WindowsUpdateStatus? status, PendingUpdateScan? scan)
    {
        var items = new List<CheckupItem>();
        if (status?.ServiceDisabled == true)
        {
            items.Add(new CheckupItem("windowsupdate.service", CheckupSource.WindowsUpdate, Severity.High,
                "The Windows Update service is turned off",
                "Windows cannot look for or install security fixes while its update service is disabled.",
                "Security fixes arrive again.", RiskLevel.Moderate, CheckupFixKind.None, []) { Topic = PendingUpdatesTopic });
        }

        if (status?.RestartPending == true)
        {
            items.Add(new CheckupItem("windowsupdate.restart", CheckupSource.WindowsUpdate, Severity.Medium,
                "Restart to finish installing updates",
                "Some updates are installed but only take effect after a restart.",
                "The installed fixes start working.", RiskLevel.Safe, CheckupFixKind.None, []) { Topic = RestartTopic });
        }

        if (scan is null)
        {
            return items;
        }

        var installable = UpdateInstallSummary.InstallAllSelection(scan.Updates);
        if (installable.Count > 0)
        {
            bool security = installable.Any(u => u.Severity is UpdateSeverity.Critical or UpdateSeverity.Important
                || u.Classification is UpdateClassification.Security or UpdateClassification.Cumulative);
            bool definitionsOnly = installable.All(u => u.Classification == UpdateClassification.Definition);
            var kinds = installable.Select(u => UpdateClassifier.Label(u.Classification)).Distinct(StringComparer.Ordinal).ToList();
            items.Add(new CheckupItem("windowsupdate.pending", CheckupSource.WindowsUpdate,
                security ? Severity.High : definitionsOnly ? Severity.Low : Severity.Medium,
                installable.Count == 1 ? "Install 1 Windows update" : $"Install {installable.Count} Windows updates",
                $"Waiting to be installed: {string.Join(", ", kinds).ToLowerInvariant()}.",
                security ? "Security fixes from Microsoft." : "Fixes and improvements from Microsoft.",
                RiskLevel.Safe, CheckupFixKind.InstallWindowsUpdates, installable.Select(u => u.Id).ToList())
            {
                Topic = PendingUpdatesTopic,
                Magnitude = installable.Count,
                MayNeedRestart = installable.Any(u => u.Restart != UpdateRestartNeed.No),
            });
        }

        int settingsOnly = scan.Updates.Count(u => u.NeedsUserInput && !u.IsOptional);
        if (settingsOnly > 0)
        {
            items.Add(new CheckupItem("windowsupdate.settings", CheckupSource.WindowsUpdate, Severity.Low,
                settingsOnly == 1 ? "1 update must be installed from Settings" : $"{settingsOnly} updates must be installed from Settings",
                "These updates ask questions while they install, so Windows Update in Settings has to run them.",
                "The remaining updates get installed.", RiskLevel.Safe, CheckupFixKind.None, []));
        }

        return items;
    }

    private static string? HealthTopic(HealthItem item)
    {
        if (item.Component.Equals("Windows Update", StringComparison.OrdinalIgnoreCase))
        {
            return item.Title.Contains("restart", StringComparison.OrdinalIgnoreCase) ? RestartTopic : PendingUpdatesTopic;
        }

        return item.Component.Equals("Devices", StringComparison.OrdinalIgnoreCase) ? DevicesTopic : null;
    }
}
