using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.Logging;
using PcChecker.Core.Optimization;
using PcChecker.Core.RegistryHealth;
using PcChecker.Core.Restore;
using PcChecker.Core.Storage;

namespace PcChecker.Core.Leftovers;

/// <summary>
/// Removal: every item is checked again right before it is touched. Scheduled tasks are exported to XML (sealed) and
/// deleted with schtasks; services are deleted with sc.exe only while their program is missing and they are stopped;
/// registry keys are backed up into one sealed .reg backup (the Registry module's format) before any is removed;
/// folders and shortcuts go to the Recycle Bin. All of it runs inside the change guard with a restore point first.
/// </summary>
public sealed partial class WindowsLeftoverService
{
    /// <summary>sc.exe: the service is marked for deletion and goes once its last handle closes (at the latest after a restart).</summary>
    private const int ErrorServiceMarkedForDelete = 1072;

    /// <summary>sc.exe: the service does not exist (any more).</summary>
    private const int ErrorServiceDoesNotExist = 1060;

    public async Task<OperationResult<LeftoverRemovalResult>> RemoveAsync(IReadOnlyList<LeftoverItem> items, nint ownerWindow,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var plan = items.DistinctBy(i => i.Id).ToList();
        if (plan.Count == 0)
        {
            return OperationResult<LeftoverRemovalResult>.Fail("Nothing was selected.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult<LeftoverRemovalResult>.Fail("Leftovers can only be removed on Windows.");
        }

        var roots = Roots();
        var startMenu = StartMenuFolders();
        var refused = new List<LeftoverItemOutcome>();
        var allowed = new List<LeftoverItem>();
        foreach (var item in plan)
        {
            var why = WhyRefused(item, roots, startMenu);
            if (why is null)
            {
                allowed.Add(item);
            }
            else
            {
                refused.Add(new LeftoverItemOutcome(item, false, "Kept: " + why));
            }
        }

        if (allowed.Count == 0)
        {
            var none = new LeftoverRemovalResult(refused, []);
            return new OperationResult<LeftoverRemovalResult>(false, "Nothing could be removed.", none);
        }

        // Deleting a service cannot be undone from PC Checker, so a batch with services does not start without a restore point.
        var options = new ChangeOptions(CreateBefore: true, CreateAfter: null, ModuleName, LeftoverRules.Summarize(allowed), Reversible: true, UndoHint)
        {
            OnRestorePointFailed = allowed.Any(i => i.Kind == LeftoverKind.Service) ? RestorePointFailedBehavior.Abort : RestorePointFailedBehavior.Continue,
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guarded = await _guard.RunGuardedAsync($"Remove {LeftoverRules.Summarize(allowed)} left by removed programs", options,
                ct => RemoveCoreAsync(allowed, roots, startMenu, ownerWindow, progress, ct), progress, cancellationToken).ConfigureAwait(false);

            var value = guarded.Value ?? new LeftoverRemovalResult(
                allowed.Select(i => new LeftoverItemOutcome(i, false, guarded.Error is null
                    ? "Not started: " + guarded.Message
                    : "Stopped by an unexpected error. Check the Action Journal and the restore list.")).ToList(), []);
            var combined = value with { Outcomes = [.. value.Outcomes, .. refused] };
            string message = guarded.Value is null ? guarded.Message : LeftoverRules.DescribeResult(combined);
            return new OperationResult<LeftoverRemovalResult>(combined.Removed > 0, message, combined, guarded.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Why an item must be kept whatever its state on disk, or null.</summary>
    private static string? WhyRefused(LeftoverItem item, IReadOnlyList<LeftoverRootFolder> roots, IReadOnlyList<string> startMenu)
    {
        if (!item.CanRemove)
        {
            return item.Note ?? "it can only be reported.";
        }

        return item.Kind switch
        {
            LeftoverKind.Folder or LeftoverKind.EmptyFolder => LeftoverPathRules.WhyFolderRefused(item.Location, roots),
            LeftoverKind.Shortcut => LeftoverPathRules.WhyShortcutRefused(item.Location, startMenu),
            LeftoverKind.RegistryKey => LeftoverRules.SplitKey(item.Location) is { } key ? LeftoverRules.WhyKeyRefused(key.Hive, key.Path) : "The key path is not valid.",
            LeftoverKind.ScheduledTask => LeftoverBackupNaming.IsRemovableTaskPath(item.Location) ? null : "Tasks in Windows' own folders are never removed.",
            LeftoverKind.Service => string.IsNullOrWhiteSpace(item.Location) || item.Location.IndexOfAny(['\\', '/', '"']) >= 0 ? "The service name is not valid." : null,
            _ => "Unknown kind of item.",
        };
    }

    private async Task<OperationResult<LeftoverRemovalResult>> RemoveCoreAsync(IReadOnlyList<LeftoverItem> items, IReadOnlyList<LeftoverRootFolder> roots,
        IReadOnlyList<string> startMenu, nint ownerWindow, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        var outcomes = new List<LeftoverItemOutcome>();
        var entries = new List<LeftoverRestoreEntry>();
        var probe = PathProbe.CreateSystem();
        int done = 0;
        void Report(LeftoverItem item) => progress?.Report(new ProgressUpdate("Removing leftovers", 5 + 90.0 * done++ / items.Count, item.Name));

        foreach (var task in items.Where(i => i.Kind == LeftoverKind.ScheduledTask))
        {
            Report(task);
            var (outcome, entry) = ct.IsCancellationRequested ? (Stopped(task), null) : await RemoveTaskAsync(task, probe, ct).ConfigureAwait(false);
            outcomes.Add(outcome);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        foreach (var service in items.Where(i => i.Kind == LeftoverKind.Service))
        {
            Report(service);
            outcomes.Add(ct.IsCancellationRequested ? Stopped(service) : await RemoveServiceAsync(service, probe, ct).ConfigureAwait(false));
        }

        var keys = items.Where(i => i.Kind == LeftoverKind.RegistryKey).ToList();
        if (keys.Count > 0)
        {
            foreach (var key in keys)
            {
                Report(key);
            }

            var (keyOutcomes, keyEntry) = ct.IsCancellationRequested ? (keys.Select(Stopped).ToList(), null) : await RemoveKeysAsync(keys, ct).ConfigureAwait(false);
            outcomes.AddRange(keyOutcomes);
            if (keyEntry is not null)
            {
                entries.Add(keyEntry);
            }
        }

        var recycle = items.Where(i => i.Kind is LeftoverKind.Folder or LeftoverKind.EmptyFolder or LeftoverKind.Shortcut).ToList();
        if (recycle.Count > 0)
        {
            outcomes.AddRange(await RunOnStaThread(() => RecycleItems(recycle, roots, startMenu, probe, ownerWindow, Report, ct)).ConfigureAwait(false));
        }

        if (entries.Count > 0)
        {
            AppendRestoreEntries(entries);
        }

        foreach (var outcome in outcomes)
        {
            await JournalAsync(outcome).ConfigureAwait(false);
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var result = new LeftoverRemovalResult(outcomes, entries);
        return new OperationResult<LeftoverRemovalResult>(result.Removed > 0, LeftoverRules.DescribeResult(result), result);
    }

    private static LeftoverItemOutcome Stopped(LeftoverItem item) => new(item, false, "Not removed: stopped before this item.");

    private async Task<(LeftoverItemOutcome Outcome, LeftoverRestoreEntry? Entry)> RemoveTaskAsync(LeftoverItem item, PathProbe probe, CancellationToken ct)
    {
        string file = Path.Combine(WindowsDirectory, "System32", "Tasks", item.Location.TrimStart('\\'));
        byte[] xml;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                return (new LeftoverItemOutcome(item, false, "Not removed: the task no longer exists."), null);
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxTaskFileBytes)
            {
                return (new LeftoverItemOutcome(item, false, "Kept: the task definition is not a plain file."), null);
            }

            xml = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new LeftoverItemOutcome(item, false, "Kept: the task could not be read (" + ex.Message + ")."), null);
        }

        var target = LeftoverRules.MissingTaskTarget(LeftoverTaskXml.ReadActions(LeftoverTaskXml.Decode(xml)), c => RegistryPathTools.ExtractPath(c, _environment),
            p => RegistryPathTools.Evaluate(p, probe));
        if (target is null)
        {
            return (new LeftoverItemOutcome(item, false, "Kept: the task changed since the scan and no longer points only at missing files."), null);
        }

        // 1. Export: the exact definition, sealed in HKLM so the restore can trust it.
        string id;
        string backupFile;
        var created = DateTimeOffset.Now;
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            id = LeftoverBackupNaming.Unique(LeftoverBackupNaming.TaskBackupId(created, item.Location),
                candidate => File.Exists(Path.Combine(BackupDirectory, candidate + LeftoverBackupNaming.TaskExtension)));
            backupFile = Path.Combine(BackupDirectory, id + LeftoverBackupNaming.TaskExtension);
            await using (var stream = new FileStream(backupFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(xml, ct).ConfigureAwait(false);
            }

            try
            {
                LeftoverBackupSeals.Save(id, LeftoverBackupNaming.Seal(xml, item.Location));
            }
            catch (IOException)
            {
                TryDeleteFile(backupFile);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new LeftoverItemOutcome(item, false, "Kept: the task could not be exported first (" + ex.Message + ")."), null);
        }

        // 2. Delete.
        var output = await RunToolAsync(Path.Combine(WindowsDirectory, "System32", "schtasks.exe"), ["/Delete", "/TN", item.Location, "/F"], ct).ConfigureAwait(false);
        if (output.ExitCode != 0 || File.Exists(file))
        {
            TryDeleteFile(backupFile);
            return (new LeftoverItemOutcome(item, false, $"Not removed: Task Scheduler refused ({Describe(output)})."), null);
        }

        _logger.LogInformation("Leftover task {Task} deleted; exported to {File}.", item.Location, backupFile);
        var entry = new LeftoverRestoreEntry(Guid.NewGuid().ToString("N"), LeftoverRestoreKind.ScheduledTask, created, item.Name, item.Location, id);
        return (new LeftoverItemOutcome(item, true, $"Exported to {Path.GetFileName(backupFile)} and deleted. It can be put back from the restore list."), entry);
    }

    private async Task<LeftoverItemOutcome> RemoveServiceAsync(LeftoverItem item, PathProbe probe, CancellationToken ct)
    {
        var access = new RegistryAccess();
        string? image;
        using (var key = access.Open("HKLM", ServicesPath + "\\" + item.Location))
        {
            if (key is null)
            {
                return new LeftoverItemOutcome(item, false, "Not removed: the service no longer exists.");
            }

            image = RegistryPathTools.ResolveServiceImagePath(RegistryAccess.Text(key, "ImagePath"), _environment);
        }

        if (image is null || LeftoverRules.IsWindowsImage(image, WindowsDirectory) || RegistryPathTools.Evaluate(image, probe) != TargetState.Missing)
        {
            return new LeftoverItemOutcome(item, false, "Kept: its program file exists again or cannot be checked.");
        }

        var state = QueryServiceState(item.Location);
        if (!LeftoverRules.CanDeleteService(imageMissing: true, state))
        {
            return new LeftoverItemOutcome(item, false, state is null
                ? "Kept: Windows did not report whether it is stopped."
                : $"Kept: it is {state.ToLowerInvariant()}; only stopped services are deleted.");
        }

        var output = await RunToolAsync(Path.Combine(WindowsDirectory, "System32", "sc.exe"), ["delete", item.Location], ct).ConfigureAwait(false);
        if (output.ExitCode is 0 or ErrorServiceMarkedForDelete)
        {
            _logger.LogInformation("Leftover service {Service} deleted (program {Image} was missing).", item.Location, image);
            return new LeftoverItemOutcome(item, true, output.ExitCode == 0
                ? "Deleted. The restore point created first can bring it back."
                : "Marked for deletion; Windows removes it after the next restart.");
        }

        return new LeftoverItemOutcome(item, false, output.ExitCode == ErrorServiceDoesNotExist
            ? "Not removed: the service no longer exists."
            : $"Not removed: Windows refused ({Describe(output)}).");
    }

    private async Task<(List<LeftoverItemOutcome> Outcomes, LeftoverRestoreEntry? Entry)> RemoveKeysAsync(IReadOnlyList<LeftoverItem> keys, CancellationToken ct)
    {
        var access = new RegistryAccess();
        var outcomes = new List<LeftoverItemOutcome>();
        var sections = new List<string>();
        var backedUp = new List<(LeftoverItem Item, string Hive, string Path)>();

        // 1. Back up every key; one that cannot be backed up completely is kept.
        foreach (var item in keys)
        {
            if (LeftoverRules.SplitKey(item.Location) is not { } key || LeftoverRules.WhyKeyRefused(key.Hive, key.Path) is not null)
            {
                outcomes.Add(new LeftoverItemOutcome(item, false, "Kept: the key is protected."));
                continue;
            }

            if (!access.KeyExists(key.Hive, key.Path))
            {
                outcomes.Add(new LeftoverItemOutcome(item, false, "Not removed: the key no longer exists."));
                continue;
            }

            var text = await RegistryBackupStore.ExportKeyAsync(access, key.Hive, key.Path, _logger, ct).ConfigureAwait(false);
            if (text is null)
            {
                outcomes.Add(new LeftoverItemOutcome(item, false, "Kept: the key could not be backed up completely."));
                continue;
            }

            sections.Add(text);
            backedUp.Add((item, key.Hive, key.Path));
        }

        if (backedUp.Count == 0)
        {
            return (outcomes, null);
        }

        // 2. Write one sealed backup in the Registry module's format; if that fails nothing is removed.
        RegistryBackup backup;
        var issues = backedUp.Select(b => new RegistryIssue("leftover:" + b.Hive + "\\" + b.Path, RegistryIssueCategory.UninstallEntries,
            b.Hive, b.Path, null, b.Item.Reason, null, RiskLevel.Moderate, SafeToFix: true, FixDeletesKey: true, AdvancedOnly: false)).ToList();
        try
        {
            string summary = "Leftover program settings: " + string.Join(", ", backedUp.Select(b => b.Item.Name).Distinct().Take(6)) +
                             (backedUp.Count > 6 ? $" and {backedUp.Count - 6} more" : string.Empty);
            backup = RegistryBackupStore.Write(RegistryBackupDirectory, sections, issues, summary, _logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Leftover registry backup could not be written; no key was removed.");
            outcomes.AddRange(backedUp.Select(b => new LeftoverItemOutcome(b.Item, false, "Kept: the backup could not be saved (" + ex.Message + ").")));
            return (outcomes, null);
        }

        // 3. Remove, checking each key once more right before.
        var removed = new List<string>();
        foreach (var (item, hive, path) in backedUp)
        {
            if (ct.IsCancellationRequested)
            {
                outcomes.Add(Stopped(item));
                continue;
            }

            int cut = path.LastIndexOf('\\');
            if (LeftoverRules.WhyKeyRefused(hive, path) is not null || cut <= 0)
            {
                outcomes.Add(new LeftoverItemOutcome(item, false, "Kept: the key is protected."));
                continue;
            }

            try
            {
                using var parent = access.Open(hive, path[..cut], writable: true);
                if (parent is null)
                {
                    outcomes.Add(new LeftoverItemOutcome(item, false, "Not removed: the key could not be opened for writing."));
                    continue;
                }

                parent.DeleteSubKeyTree(path[(cut + 1)..], throwOnMissingSubKey: false);
                removed.Add(item.Location);
                outcomes.Add(new LeftoverItemOutcome(item, true, $"Backed up to {backup.Id} and removed. It can be put back from the restore list."));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
            {
                outcomes.Add(new LeftoverItemOutcome(item, false, "Not removed: " + ex.Message));
            }
        }

        var entry = removed.Count == 0
            ? null
            : new LeftoverRestoreEntry(Guid.NewGuid().ToString("N"), LeftoverRestoreKind.RegistryKeys, backup.Created,
                removed.Count == 1 ? "1 registry key" : $"{removed.Count} registry keys", string.Join("; ", removed), backup.Id);
        return (outcomes, entry);
    }

    private List<LeftoverItemOutcome> RecycleItems(IReadOnlyList<LeftoverItem> items, IReadOnlyList<LeftoverRootFolder> roots, IReadOnlyList<string> startMenu,
        PathProbe probe, nint ownerWindow, Action<LeftoverItem> report, CancellationToken ct)
    {
        var outcomes = new List<LeftoverItemOutcome>();
        ShortcutResolver? resolver = null;
        foreach (var item in items)
        {
            report(item);
            if (ct.IsCancellationRequested)
            {
                outcomes.Add(Stopped(item));
                continue;
            }

            string? refusal = item.Kind == LeftoverKind.Shortcut
                ? CheckShortcut(item, startMenu, probe, resolver ??= ShortcutResolver.TryCreate())
                : CheckFolder(item, roots, ct);
            if (refusal is not null)
            {
                outcomes.Add(new LeftoverItemOutcome(item, false, refusal));
                continue;
            }

            var result = DiskSpaceNativeMethods.MoveToRecycleBin(item.Location, ownerWindow);
            bool gone = !Directory.Exists(item.Location) && !File.Exists(item.Location);
            outcomes.Add(result.Success && gone
                ? new LeftoverItemOutcome(item, true, "Moved to the Recycle Bin.")
                : new LeftoverItemOutcome(item, false, "Not moved: " + (result.Success ? "Windows reported success but it is still there." : result.Message)));
        }

        return outcomes;
    }

    /// <summary>Why a folder must be kept now (it may have changed since the scan), or null.</summary>
    private string? CheckFolder(LeftoverItem item, IReadOnlyList<LeftoverRootFolder> roots, CancellationToken ct)
    {
        var path = item.Location;
        var why = LeftoverPathRules.WhyFolderRefused(path, roots);
        if (why is not null)
        {
            return "Kept: " + why;
        }

        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
            {
                return "Not moved: it no longer exists.";
            }

            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            {
                return "Kept: only drives inside this PC have a Recycle Bin.";
            }

            var root = LeftoverPathRules.RootOf(path, roots)!;
            var ancestors = LeftoverPathRules.FoldersBetween(root.Path, path).Select(a => new DirectoryInfo(a).Attributes).ToList();
            var attributeReason = LeftoverPathRules.WhyRefusedByAttributes(dir.Attributes, ancestors);
            if (attributeReason is not null)
            {
                return "Kept: " + attributeReason;
            }

            if (item.Kind == LeftoverKind.EmptyFolder)
            {
                var stats = Measure(dir, ct);
                if (!stats.Complete || stats.Files > 0)
                {
                    return "Kept: it is no longer empty.";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return "Kept: it could not be checked (" + ex.Message + ").";
        }

        return null;
    }

    private string? CheckShortcut(LeftoverItem item, IReadOnlyList<string> startMenu, PathProbe probe, ShortcutResolver? resolver)
    {
        var why = LeftoverPathRules.WhyShortcutRefused(item.Location, startMenu);
        if (why is not null)
        {
            return "Kept: " + why;
        }

        try
        {
            var file = new FileInfo(item.Location);
            if (!file.Exists)
            {
                return "Not moved: it no longer exists.";
            }

            var folder = startMenu.First(f => DiskSpacePaths.IsSameOrInside(item.Location, f));
            var ancestors = LeftoverPathRules.FoldersBetween(folder, item.Location).Select(a => new DirectoryInfo(a).Attributes).ToList();
            var attributeReason = LeftoverPathRules.WhyRefusedByAttributes(file.Attributes, ancestors);
            if (attributeReason is not null)
            {
                return "Kept: " + attributeReason;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return "Kept: it could not be checked (" + ex.Message + ").";
        }

        var target = resolver?.Target(item.Location);
        return target is not null && RegistryPathTools.Evaluate(target, probe) == TargetState.Missing
            ? null
            : "Kept: its target exists again or cannot be checked.";
    }

    private async Task JournalAsync(LeftoverItemOutcome outcome)
    {
        var item = outcome.Item;
        string action = item.Kind switch
        {
            LeftoverKind.Folder or LeftoverKind.EmptyFolder => "Move leftover folder to the Recycle Bin",
            LeftoverKind.Shortcut => "Move leftover shortcut to the Recycle Bin",
            LeftoverKind.RegistryKey => "Remove leftover registry key",
            LeftoverKind.Service => "Delete leftover service",
            LeftoverKind.ScheduledTask => "Delete leftover scheduled task",
            _ => "Remove leftover",
        };

        try
        {
            await _journal.RecordAsync(ActionRecord.Create(ModuleName, action, item.Location,
                (outcome.Success ? "Succeeded: " : "Kept: ") + outcome.Message,
                before: item.Target is null ? item.Reason : item.Reason + " Program: " + item.Target,
                after: outcome.Success ? "Removed" : null,
                reversible: outcome.Success && item.Kind != LeftoverKind.Service,
                undoHint: item.Kind == LeftoverKind.Service ? "Use the restore point created before the removal." : UndoHint)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the leftover removal to the action journal.");
        }
    }

    /// <summary>Runs a Windows tool with its arguments passed one by one (no quoting by hand) and returns its exit code and output.</summary>
    private static async Task<Startup.CommandOutput> RunToolAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = StorageCommandRunner.OemEncoding(),
            StandardErrorEncoding = StorageCommandRunner.OemEncoding(),
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return new Startup.CommandOutput(-1, string.Empty, $"{Path.GetFileName(fileName)} did not start.", false);
            }

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }

                return new Startup.CommandOutput(-2, string.Empty, $"{Path.GetFileName(fileName)} did not answer in time.", true);
            }

            return new Startup.CommandOutput(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new Startup.CommandOutput(-1, string.Empty, ex.Message, false);
        }
    }

    private static string Describe(Startup.CommandOutput output) =>
        output.TimedOut ? "no answer in time" : string.IsNullOrWhiteSpace(output.FirstLine) ? $"exit code {output.ExitCode}" : output.FirstLine;

    private static Task<List<LeftoverItemOutcome>> RunOnStaThread(Func<List<LeftoverItemOutcome>> work)
    {
        var done = new TaskCompletionSource<List<LeftoverItemOutcome>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "PC Checker leftovers recycle",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray backup file without a restore entry is harmless.
        }
    }
}
