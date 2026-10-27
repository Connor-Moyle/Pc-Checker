using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Optimization;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Windows implementation of <see cref="ITempFileCleaner"/>. Every real run goes through <see cref="IChangeGuard"/>;
/// files are deleted one by one inside the category roots only, locked files are counted as skipped, empty folders are
/// pruned bottom-up, the Recycle Bin is emptied through the shell and the Windows Update and Font Cache services are
/// paused while their files are removed. The scan can be minutes or hours old, so each file is checked again at delete
/// time: it must still match one of the category's folders and patterns and not be excluded, no folder between the root
/// and the file may have become a junction or symlink, the handle that deletes the file must still resolve inside the
/// root, and its current last write time decides whether it is too recent to remove. Categories whose app is running or
/// whose preconditions fail (a pending restart, an install) are skipped, never failed. Command categories (DISM, Disk
/// Cleanup handlers, restore points, the clipboard) each run as their own guarded change.
/// </summary>
public sealed class WindowsTempFileCleaner : ITempFileCleaner
{
    private const string Module = "Cleanup";
    private const int ProgressEveryFiles = 200;

    private readonly IChangeGuard _guard;
    private readonly ISettingsService _settings;
    private readonly ILogService _logService;
    private readonly ICleanupCommandService _commands;
    private readonly ILogger<WindowsTempFileCleaner> _logger;

    public WindowsTempFileCleaner(IChangeGuard guard, ISettingsService settings, ILogService logService, ICleanupCommandService commands, ILogger<WindowsTempFileCleaner> logger)
    {
        _guard = guard;
        _settings = settings;
        _logService = logService;
        _commands = commands;
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

        var fileCategories = selected.Where(c => c.Category.Kind != CleanupKind.Command).ToList();
        var commandCategories = selected.Where(c => c.Category.Kind == CleanupKind.Command).ToList();
        var parts = new List<CleanupRunResult>();

        if (fileCategories.Count > 0)
        {
            var categoryNames = string.Join(", ", fileCategories.Select(c => c.Category.Name));
            var options = new ChangeOptions(
                CreateBefore: CleanupRules.IsSafeOnly(fileCategories.Select(c => c.Category)) ? false : null,
                CreateAfter: false,
                Module: Module,
                Target: categoryNames,
                Reversible: false,
                UndoHint: "Deleted temporary files cannot be restored; Windows and programs recreate them as needed.");

            var guarded = await _guard.RunGuardedAsync(
                $"Clean temporary files ({fileCategories.Count} categories)",
                options,
                ct => Task.Run(() => OperationResult<CleanupRunResult>.Ok(CleanCore(fileCategories, progress, ct), "Cleanup finished."), ct),
                progress,
                cancellationToken).ConfigureAwait(false);
            parts.Add(guarded.Value ?? new CleanupRunResult(0, 0, [], [guarded.Message], false));
        }

        if (commandCategories.Count > 0)
        {
            parts.Add(await RunCommandsAsync(commandCategories, progress, cancellationToken).ConfigureAwait(false));
        }

        return Merge(parts, dryRun: false);
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

    private static CleanupRunResult Merge(IReadOnlyList<CleanupRunResult> parts, bool dryRun) =>
        new(parts.Sum(p => p.DeletedFiles), parts.Sum(p => p.FreedBytes), parts.SelectMany(p => p.Skipped).ToList(), parts.SelectMany(p => p.Errors).ToList(), dryRun)
        {
            Messages = parts.SelectMany(p => p.Messages).ToList(),
        };

    private static CleanupRunResult DryRun(List<CategoryScanResult> selected, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        int files = 0;
        long bytes = 0;
        var skipped = new List<string>();
        var messages = new List<string>();
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

            if (category.BlockedReason is { } blocked)
            {
                skipped.Add($"{category.Category.Name}: {CleanupRules.NoteText(blocked)}");
                continue;
            }

            if (category.Category.Kind == CleanupKind.Command)
            {
                messages.Add(category.SizeKnown
                    ? $"{category.Category.Name}: Windows would remove about {ByteSize.Format(category.TotalBytes)}."
                    : $"{category.Category.Name}: Windows would run this; the size is only known afterwards.");
                bytes += category.TotalBytes;
                continue;
            }

            files += category.FileCount;
            bytes += category.TotalBytes;
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        return new CleanupRunResult(files, bytes, skipped, [], true) { Messages = messages };
    }

    /// <summary>
    /// Runs the command categories one by one, each as its own guarded change (confirmation already given, restore
    /// point per the settings unless the category is Safe, one Action Journal entry each). Disk Cleanup handlers of
    /// several categories share one cleanmgr run. A category whose preconditions fail now is skipped.
    /// </summary>
    private async Task<CleanupRunResult> RunCommandsAsync(List<CategoryScanResult> categories, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var env = CleanupEnvironment.Capture([SafeLogDirectory(), SafeSettingsDirectory()]);
        var conditions = env.ReadConditions();
        var running = CleanupEnvironment.RunningProcesses();
        var skipped = new List<string>();
        var errors = new List<string>();
        var messages = new List<string>();
        long freed = 0;

        var runnable = new List<CategoryScanResult>();
        foreach (var item in categories)
        {
            var reason = CleanupRules.AppRunningReason(item.Category.Id, running) ?? CleanupRules.BlockedReason(item.Category, conditions);
            if (reason is not null)
            {
                skipped.Add($"{item.Category.Name}: {reason}");
            }
            else
            {
                runnable.Add(item);
            }
        }

        // One cleanmgr /sagerun for every selected Disk Cleanup handler.
        var handlerItems = runnable.Where(c => c.Category.Command == CleanupCommand.DiskCleanupHandlers).ToList();
        var batches = runnable.Where(c => c.Category.Command != CleanupCommand.DiskCleanupHandlers)
            .Select(c => (Category: c.Category, Items: new List<CategoryScanResult> { c }))
            .ToList();
        if (handlerItems.Count > 0)
        {
            var combined = new CleanupCategory("diskcleanup.batch", "Windows Disk Cleanup items",
                string.Join(", ", handlerItems.Select(h => h.Category.Name)), handlerItems.Max(h => h.Category.Risk), [], ["*"], false, false)
            {
                Kind = CleanupKind.Command,
                Command = CleanupCommand.DiskCleanupHandlers,
                DiskCleanupHandlers = handlerItems.SelectMany(h => h.Category.DiskCleanupHandlers).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Requires = CleanupRequirement.Elevated,
            };
            batches.Insert(0, (combined, handlerItems));
        }

        foreach (var (category, items) in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var names = string.Join(", ", items.Select(i => i.Category.Name));
            var options = new ChangeOptions(
                CreateBefore: category.Risk == RiskLevel.Safe ? false : null,
                CreateAfter: false,
                Module: Module,
                Target: names,
                Reversible: false,
                UndoHint: "Windows removed these files itself; they cannot be restored.");
            // What the tool really freed is read from the system drive's free space, not taken from the scan estimate
            // (a Disk Cleanup handler may remove less than was measured, or nothing at all).
            long? freeBefore = SystemDriveFreeBytes();
            var result = await _guard.RunGuardedAsync(category.Name, options, ct => _commands.RunAsync(category, progress, ct), progress, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                messages.Add(result.Message);
                freed += CleanupRules.FreedByCommand(freeBefore, SystemDriveFreeBytes());
            }
            else
            {
                errors.Add($"{names}: {result.Message}");
            }
        }

        return new CleanupRunResult(0, freed, skipped, errors, false) { Messages = messages };
    }

    private static long? SystemDriveFreeBytes()
    {
        try
        {
            var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            return new DriveInfo(drive + "\\").AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private CleanupRunResult CleanCore(List<CategoryScanResult> selected, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var env = CleanupEnvironment.Capture([SafeLogDirectory(), SafeSettingsDirectory()]);
        var running = CleanupEnvironment.RunningProcesses();
        var conditions = env.ReadConditions();
        int skipNewerThanMinutes = Math.Max(0, _settings.Current.Cleanup.SkipFilesNewerThanMinutes);
        var now = DateTime.Now;
        var finalRoots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        int deleted = 0;
        long freed = 0;
        var skipped = new List<string>();
        var errors = new List<string>();

        // Only categories that may run now pause services.
        var runnable = selected
            .Where(c => CleanupRules.AppRunningReason(c.Category.Id, running) is null && CleanupRules.BlockedReason(c.Category, conditions) is null)
            .Select(c => c.Category.Id)
            .ToList();
        var services = CleanupRules.ServicesToStop(runnable);
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

                var reason = CleanupRules.AppRunningReason(id, running) ?? CleanupRules.BlockedReason(category.Category, conditions);
                if (reason is not null)
                {
                    skipped.Add($"{category.Category.Name}: {reason}");
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

                var resolved = env.Resolve(category.Category);
                var roots = resolved.Roots;
                int effectiveSkip = CleanupRules.EffectiveSkipMinutes(category.Category, skipNewerThanMinutes);
                var touchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in category.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((++processed % ProgressEveryFiles) == 0)
                    {
                        progress?.Report(new ProgressUpdate("Cleaning " + category.Category.Name, 100.0 * processed / totalFiles, $"{ByteSize.Format(freed)} freed"));
                    }

                    if (!CleanupSafety.IsPathAllowed(file.Path, roots) || env.IsProtected(file.Path)
                        || !CleanupRules.MatchesEntry(file.Path, resolved.Entries)
                        || CleanupSafety.IsExcluded(file.Path, resolved.ExcludedPaths, category.Category.ExcludePatterns))
                    {
                        skipped.Add(file.Path + " (outside the allowed folders)");
                        continue;
                    }

                    if (CleanupSafety.ShouldSkip(file, now, effectiveSkip))
                    {
                        skipped.Add(file.Path + " (recently modified or protected name)");
                        continue;
                    }

                    switch (TryDelete(file, roots, env, resolved, category.Category, finalRoots, effectiveSkip, out long removedBytes))
                    {
                        case DeleteOutcome.Deleted:
                            deleted++;
                            freed += removedBytes;
                            foreach (var dir in CleanupRules.ParentDirectoriesInsideRoots(file.Path, roots))
                            {
                                touchedDirectories.Add(dir);
                            }

                            break;
                        case DeleteOutcome.Missing:
                            break;
                        case DeleteOutcome.Recent:
                            skipped.Add(file.Path + " (recently modified or protected name)");
                            break;
                        case DeleteOutcome.Outside:
                            skipped.Add(file.Path + " (outside the allowed folders)");
                            break;
                        case DeleteOutcome.Locked:
                            skipped.Add(file.Path + " (in use)");
                            break;
                        case DeleteOutcome.Failed:
                            errors.Add(file.Path);
                            break;
                    }
                }

                PruneEmptyDirectories(touchedDirectories, roots, resolved.ExcludedPaths, id);
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

        /// <summary>Changed since the scan and now newer than the "skip recently modified files" limit.</summary>
        Recent,

        /// <summary>A folder on the way became a junction or symlink, or the file now resolves outside its root.</summary>
        Outside,
    }

    /// <summary>
    /// Deletes one file from the scan after checking it again as it is now: every folder from the root down must still
    /// be a plain folder, the file is opened without following a reparse point, the opened file must really live inside
    /// the root, and its current timestamp must be older than the skip limit. The file is then deleted through that same
    /// handle, so nothing can be swapped in between the checks and the delete. <paramref name="removedBytes"/> is the
    /// size at delete time.
    /// </summary>
    private DeleteOutcome TryDelete(CleanupFile file, IReadOnlyList<string> roots, CleanupEnvironment env, ResolvedRoots resolved, CleanupCategory category, Dictionary<string, string?> finalRoots,
        int skipNewerThanMinutes, out long removedBytes)
    {
        removedBytes = 0;
        try
        {
            var root = CleanupSafety.RootOf(file.Path, roots);
            if (root is null)
            {
                return DeleteOutcome.Outside;
            }

            foreach (var folder in CleanupSafety.FoldersFromRoot(file.Path, root))
            {
                if (!Directory.Exists(folder))
                {
                    return DeleteOutcome.Missing;
                }

                if (CleanupSafety.IsReparsePoint(File.GetAttributes(folder)))
                {
                    return DeleteOutcome.Outside;
                }
            }

            if (!finalRoots.TryGetValue(root, out var finalRoot))
            {
                finalRoot = CleanupNativeMethods.FinalDirectoryPath(root);
                finalRoots[root] = finalRoot;
            }

            if (finalRoot is null)
            {
                return DeleteOutcome.Failed;
            }

            using var handle = CleanupNativeMethods.OpenForDelete(file.Path, out bool missing);
            if (handle is null)
            {
                return missing ? DeleteOutcome.Missing : DeleteOutcome.Locked;
            }

            var facts = CleanupNativeMethods.Describe(handle);
            var finalPath = CleanupNativeMethods.FinalPath(handle);
            if (facts is not { } current || finalPath is null)
            {
                return DeleteOutcome.Failed;
            }

            if (CleanupSafety.IsReparsePoint(current.Attributes) || (current.Attributes & FileAttributes.Directory) != 0)
            {
                return DeleteOutcome.Locked;
            }

            if (!CleanupSafety.IsPathAllowed(finalPath, [finalRoot]) || env.IsProtected(finalPath) || CleanupSafety.IsExcluded(finalPath, resolved.ExcludedPaths, category.ExcludePatterns))
            {
                return DeleteOutcome.Outside;
            }

            if (CleanupSafety.ShouldSkip(new CleanupFile(file.Path, current.Size, current.LastWriteUtc), DateTime.UtcNow, skipNewerThanMinutes))
            {
                return DeleteOutcome.Recent;
            }

            if ((current.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0
                && !CleanupNativeMethods.ClearAttributes(handle))
            {
                return DeleteOutcome.Locked;
            }

            if (!CleanupNativeMethods.MarkForDeletion(handle))
            {
                return DeleteOutcome.Locked;
            }

            removedBytes = Math.Max(0, current.Size);
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

    private static void PruneEmptyDirectories(IEnumerable<string> directories, IReadOnlyList<string> roots, IReadOnlyList<string> excludedPaths, string categoryId)
    {
        foreach (var dir in CleanupRules.OrderForPruning(directories))
        {
            if (!CleanupSafety.IsPathAllowed(dir, roots) || !CleanupRules.MayPrune(dir, roots, categoryId) || CleanupSafety.IsExcluded(dir, excludedPaths, null))
            {
                continue;
            }

            try
            {
                var root = CleanupSafety.RootOf(dir, roots);
                if (root is not null
                    && Directory.Exists(dir)
                    && !CleanupSafety.IsReparsePoint(File.GetAttributes(dir))
                    && CleanupSafety.FoldersFromRoot(dir, root).All(f => !CleanupSafety.IsReparsePoint(File.GetAttributes(f)))
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
