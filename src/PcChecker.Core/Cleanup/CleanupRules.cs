using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Pure decisions shared by the scanner, the cleaner and the page: which categories are report-only, which browsers must
/// be closed, which services to pause, how to order directory pruning, and how to summarise results.
/// </summary>
public static partial class CleanupRules
{
    /// <summary>Prefix used for informational entries in <see cref="CategoryScanResult.Errors"/> (they are not failures).</summary>
    public const string NotePrefix = "Note: ";

    public const string WindowsOldId = "windows.old";
    public const string RecycleBinId = "recycle.bin";
    public const string WindowsUpdateCacheId = "windows.update.cache";
    public const string DeliveryOptimizationId = "delivery.optimization";

    /// <summary>
    /// Shown with the Windows.old measurement: removing the folder in any way (Storage settings, Storage Sense, Disk
    /// Cleanup) ends the option to go back to the previous Windows version.
    /// </summary>
    public const string WindowsOldNote =
        "Removing it (here, or with Storage settings, Storage Sense or Disk Cleanup) ends the option to go back to the previous version of Windows. Windows Update → Roll back shows how many days are left.";

    /// <summary>
    /// Categories that cannot be undone in a way that matters: going back to the previous Windows, uninstalling
    /// installed updates, restore points. They are never part of the check-up's one-click clean or a scheduled clean.
    /// </summary>
    public static IReadOnlyList<string> OneWayCategories { get; } = [WindowsOldId, "windows.upgrade.leftovers", "windows.component.resetbase", "restore.points"];

    /// <summary>True for a category in <see cref="OneWayCategories"/>.</summary>
    public static bool IsOneWay(string categoryId) => OneWayCategories.Contains(categoryId, StringComparer.OrdinalIgnoreCase);

    public const string FontCacheId = "font.cache";

    /// <summary>Services paused while a category is cleaned, restarted afterwards.</summary>
    public static IReadOnlyList<string> ServicesToStop(IEnumerable<string> categoryIds)
    {
        var ids = new HashSet<string>(categoryIds ?? [], StringComparer.OrdinalIgnoreCase);
        var services = new List<string>();
        if (ids.Contains(WindowsUpdateCacheId))
        {
            services.Add("wuauserv");
            services.Add("bits");
        }

        if (ids.Contains(DeliveryOptimizationId))
        {
            services.Add("DoSvc");
        }

        // The Windows Font Cache service holds its .dat files open, so they could never be removed while it runs.
        if (ids.Contains(FontCacheId))
        {
            services.Add("FontCache");
        }

        return services;
    }

    /// <summary>Categories PC Checker measures but never deletes itself (Windows has a dedicated, safer tool).</summary>
    public static bool IsReportOnly(string categoryId) => CleanupCatalog.Find(categoryId)?.Kind == CleanupKind.ReportOnly;

    /// <summary>Categories a Windows tool cleans (DISM, Disk Cleanup, vssadmin, the clipboard API).</summary>
    public static bool IsCommand(string categoryId) => CleanupCatalog.Find(categoryId)?.Kind == CleanupKind.Command;

    /// <summary>The Recycle Bin is emptied through the shell rather than by deleting files.</summary>
    public static bool IsRecycleBin(string categoryId) => categoryId.Equals(RecycleBinId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Marks a message as informational for <see cref="CategoryScanResult.Errors"/>.</summary>
    public static string Note(string message) => NotePrefix + message;

    /// <summary>True for entries written with <see cref="Note"/>.</summary>
    public static bool IsNote(string entry) => entry is not null && entry.StartsWith(NotePrefix, StringComparison.Ordinal);

    /// <summary>Strips the note prefix for display.</summary>
    public static string NoteText(string entry) => IsNote(entry) ? entry[NotePrefix.Length..] : entry;

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> itself or anything inside it.</summary>
    public static bool IsUnderDirectory(string? path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var p = Normalize(path);
        var d = Normalize(directory);
        return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Deepest directories first so children are removed before their parents.</summary>
    public static IReadOnlyList<string> OrderForPruning(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        return directories
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(Depth)
            .ThenByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every ancestor of <paramref name="filePath"/> that is strictly inside one of the roots (never the root itself).</summary>
    public static IEnumerable<string> ParentDirectoriesInsideRoots(string filePath, IEnumerable<string> roots)
    {
        var rootList = roots.Select(Normalize).ToList();
        var current = Normalize(filePath);
        while (true)
        {
            int cut = current.LastIndexOf('\\');
            if (cut <= 0)
            {
                yield break;
            }

            current = current[..cut];
            if (rootList.Any(r => current.Equals(r, StringComparison.OrdinalIgnoreCase)))
            {
                yield break;
            }

            if (CleanupSafety.IsPathAllowed(current, rootList))
            {
                yield return current;
            }
            else
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Categories whose roots are scratch space (Temp folders, caches): any emptied sub-folder there may go. Every
    /// other category (Windows logs, Panther, Prefetch, …) keeps its first-level folders such as Logs\CBS or
    /// Logs\DISM, which Windows components expect to find; only deeper, emptied folders are removed.
    /// </summary>
    public static IReadOnlyList<string> PruneAnywhereCategories => CleanupCatalog.All.Where(c => c.PruneAnywhere).Select(c => c.Id).ToList();

    /// <summary>
    /// Whether an emptied directory may be deleted after a category's files were removed: always inside the category's
    /// roots; at any depth for scratch categories (<see cref="PruneAnywhereCategories"/>); otherwise only when it sits at
    /// least two levels below a root, so <c>C:\Windows\Logs\CBS</c> survives a <c>windows.logs</c> clean.
    /// </summary>
    public static bool MayPrune(string? directory, IEnumerable<string> roots, string? categoryId)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var rootList = roots.Select(Normalize).Where(r => r.Length > 0).ToList();
        var dir = Normalize(directory);
        var root = rootList.FirstOrDefault(r => dir.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
        if (root is null)
        {
            return false;
        }

        if (categoryId is not null && PruneAnywhereCategories.Contains(categoryId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        int depth = dir[(root.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        return depth >= 2;
    }

    /// <summary>Printed by the pause script when it stopped a running service.</summary>
    public const string ServicePausedMarker = "PCCHECKER_SERVICE_PAUSED";

    /// <summary>Printed by the pause script when the service was already stopped, so nothing is resumed afterwards.</summary>
    public const string ServiceNotRunningMarker = "PCCHECKER_SERVICE_NOT_RUNNING";

    /// <summary>True when the pause script's output says the service was running and has now been stopped by us.</summary>
    public static bool ServiceWasPaused(string? scriptOutput) =>
        scriptOutput is not null && scriptOutput.Contains(ServicePausedMarker, StringComparison.Ordinal);

    /// <summary>
    /// What a Windows tool freed, read from the drive's free space before and after it ran. Unknown readings or a drive
    /// that filled up meanwhile count as nothing freed rather than the scan estimate.
    /// </summary>
    public static long FreedByCommand(long? freeBefore, long? freeAfter) =>
        freeBefore is long before && freeAfter is long after && after > before ? after - before : 0;

    /// <summary>Total count and bytes over a set of files.</summary>
    public static (int Count, long Bytes) Aggregate(IEnumerable<CleanupFile> files)
    {
        int count = 0;
        long bytes = 0;
        foreach (var f in files ?? [])
        {
            count++;
            bytes += Math.Max(0, f.SizeBytes);
        }

        return (count, bytes);
    }

    /// <summary>The <paramref name="take"/> largest files, biggest first.</summary>
    public static IReadOnlyList<CleanupFile> Largest(IEnumerable<CleanupFile> files, int take) =>
        (files ?? []).OrderByDescending(f => f.SizeBytes).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(Math.Max(0, take)).ToList();

    /// <summary>Categories ordered by reclaimable size, biggest first; ties keep catalog order.</summary>
    public static IReadOnlyList<CategoryScanResult> SortBySize(IEnumerable<CategoryScanResult> results) =>
        (results ?? []).Select((r, i) => (r, i)).OrderByDescending(t => t.r.TotalBytes).ThenBy(t => t.i).Select(t => t.r).ToList();

    /// <summary>
    /// Which categories are ticked: the saved selection when there is one (unknown ids dropped), else the catalog
    /// defaults. Advanced categories are never selected outside advanced mode.
    /// </summary>
    public static IReadOnlyList<string> ResolveSelection(IEnumerable<string>? savedIds, IReadOnlyList<CleanupCategory> catalog, bool advancedMode)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var known = catalog.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var saved = (savedIds ?? []).Where(known.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var chosen = saved.Count > 0 ? saved : catalog.Where(c => c.EnabledByDefault).Select(c => c.Id).ToList();
        return chosen.Where(id => advancedMode || known[id].Risk != RiskLevel.Advanced).ToList();
    }

    /// <summary>True when every selected category is <see cref="RiskLevel.Safe"/> (no restore point needed by default).</summary>
    public static bool IsSafeOnly(IEnumerable<CleanupCategory> categories) =>
        (categories ?? []).All(c => c.Risk == RiskLevel.Safe);

    /// <summary>One-line, plain-English summary of a cleanup run.</summary>
    public static string Summarize(CleanupRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var files = result.DeletedFiles == 1 ? "1 file" : $"{result.DeletedFiles:N0} files";
        var head = result.DryRun
            ? $"Dry run: {ByteSize.Format(result.FreedBytes)} in {files} would be removed."
            : $"Freed {ByteSize.Format(result.FreedBytes)} by removing {files}.";
        var parts = new List<string> { head };
        if (result.Skipped.Count > 0)
        {
            parts.Add(result.Skipped.Count == 1 ? "1 item was skipped (in use or protected)." : $"{result.Skipped.Count:N0} items were skipped (in use or protected).");
        }

        if (result.Errors.Count > 0)
        {
            parts.Add(result.Errors.Count == 1 ? "1 error." : $"{result.Errors.Count:N0} errors.");
        }

        return string.Join(" ", parts);
    }

    private static string Normalize(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\');

    private static int Depth(string normalized) => normalized.Count(c => c == '\\');
}
