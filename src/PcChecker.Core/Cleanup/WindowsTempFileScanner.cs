using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Windows implementation of <see cref="ITempFileScanner"/>: walks every category folder (never following junctions or
/// symlinks) with that folder's own file patterns, applies <see cref="CleanupSafety"/> and the category's exclusions,
/// skips categories whose app is running or whose preconditions are not met, measures the Recycle Bin through the
/// shell and asks <see cref="ICleanupCommandService"/> for the size of command categories. Nothing is deleted here.
/// </summary>
public sealed class WindowsTempFileScanner : ITempFileScanner
{
    private const int ProgressEveryFiles = 500;

    private readonly ISettingsService _settings;
    private readonly ILogService _logService;
    private readonly ICleanupCommandService _commands;
    private readonly ILogger<WindowsTempFileScanner> _logger;

    public WindowsTempFileScanner(ISettingsService settings, ILogService logService, ICleanupCommandService commands, ILogger<WindowsTempFileScanner> logger)
    {
        _settings = settings;
        _logService = logService;
        _commands = commands;
        _logger = logger;
    }

    public IReadOnlyList<CleanupCategory> Categories => CleanupCatalog.All;

    public Task<CleanupScanResult> ScanAsync(IEnumerable<string> categoryIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(categoryIds ?? [], StringComparer.OrdinalIgnoreCase);
        var categories = Categories.Where(c => wanted.Count == 0 || wanted.Contains(c.Id)).ToList();
        return Task.Run(() => ScanCoreAsync(categories, progress, cancellationToken), cancellationToken);
    }

    private async Task<CleanupScanResult> ScanCoreAsync(List<CleanupCategory> categories, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return CleanupScanResult.Empty();
        }

        var env = CleanupEnvironment.Capture([SafeLogDirectory(), SafeSettingsDirectory()]);
        var running = CleanupEnvironment.RunningProcesses();
        var conditions = env.ReadConditions();
        int skipNewerThanMinutes = Math.Max(0, _settings.Current.Cleanup.SkipFilesNewerThanMinutes);
        var now = DateTime.Now;
        var results = new List<CategoryScanResult>(categories.Count);

        _logger.LogInformation("Cleanup scan started for {Count} categories (elevated: {Elevated}, extra profiles: {Profiles}).",
            categories.Count, env.IsElevated, env.OtherUserProfiles.Count);

        for (int i = 0; i < categories.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = categories[i];
            progress?.Report(new ProgressUpdate(category.Name, 100.0 * i / Math.Max(1, categories.Count)));
            try
            {
                results.Add(await ScanCategoryAsync(category, env, running, conditions, now, skipNewerThanMinutes, progress, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Category {Category} could not be scanned.", category.Id);
                results.Add(new CategoryScanResult(category, 0, 0, [], [$"Could not scan: {ex.Message}"]));
            }
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var sorted = CleanupRules.SortBySize(results);
        var total = sorted.Sum(r => r.TotalBytes);
        _logger.LogInformation("Cleanup scan finished: {Bytes} reclaimable in {Files} files.", ByteSize.Format(total), sorted.Sum(r => r.FileCount));
        return new CleanupScanResult(sorted, total, DateTimeOffset.Now);
    }

    private async Task<CategoryScanResult> ScanCategoryAsync(CleanupCategory category, CleanupEnvironment env, IReadOnlyList<RunningProcess> running,
        CleanupConditions conditions, DateTime now, int skipNewerThanMinutes, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var notes = new List<string>();

        if (CleanupRules.IsRecycleBin(category.Id))
        {
            return ScanRecycleBin(category);
        }

        var appReason = CleanupRules.AppRunningReason(category.Id, running);
        if (appReason is not null)
        {
            return new CategoryScanResult(category, 0, 0, [], [CleanupRules.Note(appReason)]) { BlockedReason = appReason };
        }

        // A category that cannot run now is still measured where possible, so its size is visible.
        var blocked = CleanupRules.BlockedReason(category, conditions);
        if (blocked is not null)
        {
            notes.Add(CleanupRules.Note(blocked));
        }

        if (category.Id is "windows.upgrade.leftovers" or CleanupRules.WindowsOldId
            && CleanupRules.RecentUpgradeWarning(conditions.LastFeatureUpdate, now) is { } upgradeWarning)
        {
            notes.Add(CleanupRules.Note(upgradeWarning));
        }

        var resolved = env.Resolve(category);
        if (resolved.UsersScanned.Count > 0)
        {
            notes.Add(CleanupRules.Note("Also scanned the profiles of: " + string.Join(", ", resolved.UsersScanned) + "."));
        }

        if (CleanupRules.IsReportOnly(category.Id))
        {
            notes.Add(CleanupRules.Note("Measured only. Remove it with Storage settings or Windows Disk Cleanup."));
        }

        int effectiveSkip = CleanupRules.EffectiveSkipMinutes(category, skipNewerThanMinutes);
        var files = new List<CleanupFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        int seen = 0;
        long bytes = 0;
        foreach (var entry in resolved.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = entry.Path;
            if (!Directory.Exists(root) || env.IsProtected(root))
            {
                continue;
            }

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = entry.Recurse,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };
            bool matchAll = entry.Patterns.Any(p => p is "*" or "*.*");

            try
            {
                var directory = new DirectoryInfo(root);
                if (CleanupSafety.IsReparsePoint(directory.Attributes))
                {
                    continue;
                }

                foreach (var info in directory.EnumerateFiles("*", options))
                {
                    if ((++seen % ProgressEveryFiles) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new ProgressUpdate(category.Name, null, $"{files.Count:N0} files, {ByteSize.Format(bytes)} so far"));
                    }

                    if (!matchAll && !CleanupSafety.MatchesAny(info.Name, entry.Patterns))
                    {
                        continue;
                    }

                    if (CleanupSafety.IsReparsePoint(info.Attributes) || env.IsProtected(info.FullName)
                        || CleanupSafety.IsExcluded(info.FullName, resolved.ExcludedPaths, category.ExcludePatterns))
                    {
                        continue;
                    }

                    long size;
                    DateTime lastWrite;
                    try
                    {
                        size = info.Length;
                        lastWrite = info.LastWriteTime;
                    }
                    catch (Exception)
                    {
                        // The file vanished or is inaccessible between enumeration and stat; skip it.
                        continue;
                    }

                    var file = new CleanupFile(info.FullName, size, lastWrite);
                    if (CleanupSafety.ShouldSkip(file, now, effectiveSkip) || !CleanupSafety.IsPathAllowed(file.Path, resolved.Roots) || !seenPaths.Add(file.Path))
                    {
                        continue;
                    }

                    files.Add(file);
                    bytes += size;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                errors.Add($"{root}: {ex.Message}");
            }
        }

        files.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        var (count, total) = CleanupRules.Aggregate(files);
        if (CleanupRules.IsReportOnly(category.Id))
        {
            // Report-only categories expose the size but not a deletable file list.
            return new CategoryScanResult(category, count, total, [], notes.Concat(errors).ToList()) { BlockedReason = blocked };
        }

        if (category.Kind == CleanupKind.Command && category.Paths.Count == 0)
        {
            // Nothing to measure on disk: ask the Windows tool (DISM's analysis, the shadow storage in use).
            progress?.Report(new ProgressUpdate(category.Name, null, "Asking Windows for the size…"));
            var estimate = blocked is not null
                ? CommandEstimate.Unknown()
                : category.Command == CleanupCommand.ComponentCleanup && !IsTicked(category.Id)
                    // DISM's analysis takes a minute or two, so it only runs for a ticked category.
                    ? CommandEstimate.Unknown("Tick this and scan again to let DISM measure it.")
                    : await _commands.EstimateAsync(category, cancellationToken).ConfigureAwait(false);
            notes.AddRange(estimate.Notes.Select(CleanupRules.Note));
            return new CategoryScanResult(category, 0, estimate.Bytes, [], notes.Concat(errors).ToList())
            {
                SizeKnown = estimate.SizeKnown,
                BlockedReason = blocked,
            };
        }

        return new CategoryScanResult(category, count, total, files, notes.Concat(errors).ToList()) { BlockedReason = blocked };
    }

    private bool IsTicked(string categoryId)
    {
        try
        {
            return _settings.Current.Cleanup.EnabledCategoryIds?.Contains(categoryId, StringComparer.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    private static CategoryScanResult ScanRecycleBin(CleanupCategory category)
    {
        var info = CleanupNativeMethods.QueryRecycleBin();
        if (info is null)
        {
            return new CategoryScanResult(category, 0, 0, [], ["Windows did not report the Recycle Bin size."]);
        }

        var (bytes, items) = info.Value;
        int count = (int)Math.Min(items, int.MaxValue);
        var note = CleanupRules.Note(count == 0 ? "The Recycle Bin is empty." : "Emptied through Windows; individual files are not listed.");
        return new CategoryScanResult(category, count, bytes, [], [note]);
    }

    private string? SafeLogDirectory()
    {
        try
        {
            return _logService.LogDirectory;
        }
        catch
        {
            return null;
        }
    }

    private string? SafeSettingsDirectory()
    {
        try
        {
            return _settings.SettingsDirectory;
        }
        catch
        {
            return null;
        }
    }
}
