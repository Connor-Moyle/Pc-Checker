using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Windows implementation of <see cref="ITempFileCleaner"/>. Every real run goes through <see cref="IChangeGuard"/>;
/// files are deleted one by one inside the category roots only, locked files are counted as skipped, empty folders are
/// pruned bottom-up, the Recycle Bin is emptied through the shell and the Windows Update services are paused while
/// their download cache is removed.
/// </summary>
public sealed class WindowsTempFileCleaner : ITempFileCleaner
{
    private const string Module = "Cleanup";
    private const int ProgressEveryFiles = 200;

    private readonly IChangeGuard _guard;
    private readonly ISettingsService _settings;
    private readonly ILogService _logService;
    private readonly ILogger<WindowsTempFileCleaner> _logger;

    public WindowsTempFileCleaner(IChangeGuard guard, ISettingsService settings, ILogService logService, ILogger<WindowsTempFileCleaner> logger)
    {
        _guard = guard;
        _settings = settings;
        _logService = logService;
        _logger = logger;
    }

    public async Task<CleanupRunResult> CleanAsync(CleanupScanResult scan, IEnumerable<string> categoryIds, bool dryRun, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var wanted = new HashSet<string>(categoryIds ?? [], StringComparer.OrdinalIgnoreCase);
        var selected = scan.Categories.Where(c => wanted.Count == 0 || wanted.Contains(c.Category.Id)).ToList();
        if (selected.Count == 0)
        {
            return new CleanupRunResult(0, 0, [], ["No categories were selected."], dryRun);
        }

        if (!PlatformGuard.IsWindows)
        {
            return new CleanupRunResult(0, 0, [], ["Cleaning is only possible on Windows."], dryRun);
        }

        if (dryRun)
        {
            return await Task.Run(() => DryRun(selected, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        var categoryNames = string.Join(", ", selected.Select(c => c.Category.Name));
        var options = new ChangeOptions(
            CreateBefore: CleanupRules.IsSafeOnly(selected.Select(c => c.Category)) ? false : null,
            CreateAfter: false,
            Module: Module,
            Target: categoryNames,
            Reversible: false,
            UndoHint: "Deleted temporary files cannot be restored; Windows and programs recreate them as needed.");

        var guarded = await _guard.RunGuardedAsync(
            $"Clean temporary files ({selected.Count} categories)",
            options,
            ct => Task.Run(() => OperationResult<CleanupRunResult>.Ok(CleanCore(selected, progress, ct), "Cleanup finished."), ct),
            progress,
            cancellationToken).ConfigureAwait(false);

        if (guarded.Value is { } result)
        {
            return result;
        }

        return new CleanupRunResult(0, 0, [], [guarded.Message], false);
    }

    public Task<OperationResult> EmptyRecycleBinAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("The Recycle Bin can only be emptied on Windows."));
        }

        var options = new ChangeOptions(false, false, Module, "Recycle Bin", false, "Emptied items cannot be restored.");
        return _guard.RunGuardedAsync("Empty the Recycle Bin", options, _ => Task.Run(CleanupNativeMethods.EmptyRecycleBin), null, cancellationToken);
    }

    private static CleanupRunResult DryRun(List<CategoryScanResult> selected, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        int files = 0;
        long bytes = 0;
        var skipped = new List<string>();
        for (int i = 0; i < selected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = selected[i];
            progress?.Report(new ProgressUpdate("Checking " + category.Category.Name, 100.0 * i / selected.Count));
            if (CleanupRules.IsReportOnly(category.Category.Id))
            {
                skipped.Add($"{category.Category.Name}: measured only ({ByteSize.Format(category.TotalBytes)}); use Storage settings or Disk Cleanup.");
                continue;
            }

            files += category.FileCount;
            bytes += category.TotalBytes;
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        return new CleanupRunResult(files, bytes, skipped, [], true);
    }

    private CleanupRunResult CleanCore(List<CategoryScanResult> selected, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var env = CleanupEnvironment.Capture([SafeLogDirectory(), SafeSettingsDirectory()]);
        var runningBrowsers = CleanupEnvironment.RunningBrowserProcesses();
        int skipNewerThanMinutes = Math.Max(0, _settings.Current.Cleanup.SkipFilesNewerThanMinutes);
        var now = DateTime.Now;

        int deleted = 0;
        long freed = 0;
        var skipped = new List<string>();
        var errors = new List<string>();
        var services = CleanupRules.ServicesToStop(selected.Select(c => c.Category.Id));
        var stoppedServices = new List<string>();

        try
        {
            foreach (var service in services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ProgressUpdate("Pausing the " + service + " service"));
                if (StopService(service, errors))
                {
                    stoppedServices.Add(service);
                }
            }

            int totalFiles = Math.Max(1, selected.Sum(c => c.Files.Count));
            int processed = 0;
            foreach (var category in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = category.Category.Id;
                progress?.Report(new ProgressUpdate("Cleaning " + category.Category.Name, 100.0 * processed / totalFiles));

                if (CleanupRules.IsReportOnly(id))
                {
                    skipped.Add($"{category.Category.Name}: measured only; remove it with Storage settings or Disk Cleanup.");
                    continue;
                }

                if (CleanupRules.IsRecycleBin(id))
                {
                    var bin = CleanupNativeMethods.EmptyRecycleBin();
                    if (bin.Success)
                    {
                        deleted += category.FileCount;
                        freed += category.TotalBytes;
                    }
                    else
                    {
                        errors.Add("Recycle Bin: " + bin.Message);
                    }

                    continue;
                }

                var browserReason = CleanupRules.BrowserRunningReason(id, runningBrowsers);
                if (browserReason is not null)
                {
                    skipped.Add($"{category.Category.Name}: {browserReason}");
                    continue;
                }

                var roots = env.Resolve(category.Category).Roots;
                var touchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in category.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((++processed % ProgressEveryFiles) == 0)
                    {
                        progress?.Report(new ProgressUpdate("Cleaning " + category.Category.Name, 100.0 * processed / totalFiles, $"{ByteSize.Format(freed)} freed"));
                    }

                    if (!CleanupSafety.IsPathAllowed(file.Path, roots) || env.IsProtected(file.Path))
                    {
                        skipped.Add(file.Path + " (outside the allowed folders)");
                        continue;
                    }

                    if (CleanupSafety.ShouldSkip(file, now, skipNewerThanMinutes))
                    {
                        skipped.Add(file.Path + " (recently modified or protected name)");
                        continue;
                    }

                    switch (TryDelete(file))
                    {
                        case DeleteOutcome.Deleted:
                            deleted++;
                            freed += file.SizeBytes;
                            foreach (var dir in CleanupRules.ParentDirectoriesInsideRoots(file.Path, roots))
                            {
                                touchedDirectories.Add(dir);
                            }

                            break;
                        case DeleteOutcome.Missing:
                            break;
                        case DeleteOutcome.Locked:
                            skipped.Add(file.Path + " (in use)");
                            break;
                        case DeleteOutcome.Failed:
                            errors.Add(file.Path);
                            break;
                    }
                }

                PruneEmptyDirectories(touchedDirectories, roots, id);
            }
        }
        finally
        {
            foreach (var service in stoppedServices)
            {
                progress?.Report(new ProgressUpdate("Resuming the " + service + " service"));
                StartService(service, errors);
            }
        }

        progress?.Report(new ProgressUpdate("Done", 100, $"{ByteSize.Format(freed)} freed"));
        _logger.LogInformation("Cleanup removed {Files} files ({Bytes}); skipped {Skipped}; errors {Errors}.", deleted, ByteSize.Format(freed), skipped.Count, errors.Count);
        return new CleanupRunResult(deleted, freed, skipped, errors, false);
    }

    private enum DeleteOutcome
    {
        Deleted,
        Missing,
        Locked,
        Failed,
    }

    private DeleteOutcome TryDelete(CleanupFile file)
    {
        try
        {
            if (!File.Exists(file.Path))
            {
                return DeleteOutcome.Missing;
            }

            var attributes = File.GetAttributes(file.Path);
            if (CleanupSafety.IsReparsePoint(attributes))
            {
                return DeleteOutcome.Locked;
            }

            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
            {
                File.SetAttributes(file.Path, FileAttributes.Normal);
            }

            File.Delete(file.Path);
            return DeleteOutcome.Deleted;
        }
        catch (FileNotFoundException)
        {
            return DeleteOutcome.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return DeleteOutcome.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return DeleteOutcome.Locked;
        }
        catch (IOException)
        {
            return DeleteOutcome.Locked;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}.", file.Path);
            return DeleteOutcome.Failed;
        }
    }

    private static void PruneEmptyDirectories(IEnumerable<string> directories, IReadOnlyList<string> roots, string categoryId)
    {
        foreach (var dir in CleanupRules.OrderForPruning(directories))
        {
            if (!CleanupSafety.IsPathAllowed(dir, roots) || !CleanupRules.MayPrune(dir, roots, categoryId))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(dir)
                    && !CleanupSafety.IsReparsePoint(File.GetAttributes(dir))
                    && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir, false);
                }
            }
            catch
            {
                // Leaving an empty folder behind is harmless.
            }
        }
    }

    /// <summary>
    /// Pauses a service for the cleanup. Returns true only when this call stopped a running service, so the caller
    /// resumes exactly those: a service the user had stopped (or that is Manual and idle) is left alone.
    /// </summary>
    private bool StopService(string name, List<string> errors)
    {
        try
        {
            string script = $"$s = Get-Service -Name '{name}' -ErrorAction Stop; if ($s.Status -ne 'Running') {{ '{CleanupRules.ServiceNotRunningMarker}' }} else {{ Stop-Service -Name '{name}' -Force -ErrorAction Stop; '{CleanupRules.ServicePausedMarker}' }}";
            var result = PowerShellRunner.RunAsync(script, TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            if (result.Success)
            {
                bool paused = CleanupRules.ServiceWasPaused(result.StdOut);
                if (!paused)
                {
                    _logger.LogInformation("Service {Service} was not running; it is left as it was.", name);
                }

                return paused;
            }

            _logger.LogWarning("Service {Service} could not be stopped: {Error}", name, result.StdErr.Trim());
            errors.Add($"The {name} service could not be paused; some update files may be in use.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Service {Service} could not be stopped.", name);
            errors.Add($"The {name} service could not be paused: {ex.Message}");
        }

        return false;
    }

    private void StartService(string name, List<string> errors)
    {
        try
        {
            var result = PowerShellRunner.RunAsync($"Start-Service -Name '{name}' -ErrorAction Stop", TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            if (!result.Success)
            {
                _logger.LogWarning("Service {Service} could not be restarted: {Error}", name, result.StdErr.Trim());
                errors.Add($"The {name} service did not restart; it starts again automatically with Windows.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Service {Service} could not be restarted.", name);
            errors.Add($"The {name} service did not restart: {ex.Message}");
        }
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
