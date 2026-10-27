using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Windows implementation of <see cref="ITempFileScanner"/>: walks every category root (never following junctions or
/// symlinks), applies <see cref="CleanupSafety"/>, measures the Recycle Bin through the shell and Windows.old by size only.
/// Nothing is deleted here.
/// </summary>
public sealed class WindowsTempFileScanner : ITempFileScanner
{
    private const int ProgressEveryFiles = 500;

    private readonly ISettingsService _settings;
    private readonly ILogService _logService;
    private readonly ILogger<WindowsTempFileScanner> _logger;

    public WindowsTempFileScanner(ISettingsService settings, ILogService logService, ILogger<WindowsTempFileScanner> logger)
    {
        _settings = settings;
        _logService = logService;
        _logger = logger;
    }

    public IReadOnlyList<CleanupCategory> Categories => CleanupCatalog.All;

    public Task<CleanupScanResult> ScanAsync(IEnumerable<string> categoryIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(categoryIds ?? [], StringComparer.OrdinalIgnoreCase);
        var categories = Categories.Where(c => wanted.Count == 0 || wanted.Contains(c.Id)).ToList();
        return Task.Run(() => ScanCore(categories, progress, cancellationToken), cancellationToken);
    }

    private CleanupScanResult ScanCore(List<CleanupCategory> categories, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return CleanupScanResult.Empty();
        }

        var env = CleanupEnvironment.Capture([SafeLogDirectory(), SafeSettingsDirectory()]);
        var runningBrowsers = CleanupEnvironment.RunningBrowserProcesses();
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
                results.Add(ScanCategory(category, env, runningBrowsers, now, skipNewerThanMinutes, progress, cancellationToken));
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

    private CategoryScanResult ScanCategory(CleanupCategory category, CleanupEnvironment env, IReadOnlyList<string> runningBrowsers,
        DateTime now, int skipNewerThanMinutes, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var notes = new List<string>();

        if (CleanupRules.IsRecycleBin(category.Id))
        {
            return ScanRecycleBin(category);
        }

        var browserReason = CleanupRules.BrowserRunningReason(category.Id, runningBrowsers);
        if (browserReason is not null)
        {
            return new CategoryScanResult(category, 0, 0, [], [CleanupRules.Note(browserReason)]);
        }

        var resolved = env.Resolve(category);
        if (resolved.UsersScanned.Count > 0)
        {
            notes.Add(CleanupRules.Note("Also scanned the profiles of: " + string.Join(", ", resolved.UsersScanned) + "."));
        }

        if (CleanupRules.IsReportOnly(category.Id))
        {
            notes.Add(CleanupRules.Note(CleanupRules.WindowsOldNote));
        }

        var files = new List<CleanupFile>();
        var errors = new List<string>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = category.Recurse,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        int seen = 0;
        long bytes = 0;
        bool matchAll = category.IncludePatterns.Any(p => p == "*" || p == "*.*");
        foreach (var root in resolved.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root) || env.IsProtected(root))
            {
                continue;
            }

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

                    if (!matchAll && !CleanupSafety.MatchesAny(info.Name, category.IncludePatterns))
                    {
                        continue;
                    }

                    if (CleanupSafety.IsReparsePoint(info.Attributes) || env.IsProtected(info.FullName))
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
                    if (CleanupSafety.ShouldSkip(file, now, skipNewerThanMinutes) || !CleanupSafety.IsPathAllowed(file.Path, resolved.Roots))
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
            return new CategoryScanResult(category, count, total, [], notes.Concat(errors).ToList());
        }

        return new CategoryScanResult(category, count, total, files, notes.Concat(errors).ToList());
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
