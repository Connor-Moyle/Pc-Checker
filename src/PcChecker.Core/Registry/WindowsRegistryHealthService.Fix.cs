using System.Text;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Restore;
using PcChecker.Core.Startup;

namespace PcChecker.Core.RegistryHealth;

/// <summary>The fix flow: re-check → back up → write backup file → re-check → delete, all inside the change guard.</summary>
public sealed partial class WindowsRegistryHealthService
{
    public async Task<RegistryFixResult> FixAsync(IReadOnlyList<RegistryIssue> issues, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var messages = new List<string>();
        var fixable = new List<RegistryIssue>();
        int skipped = 0;

        foreach (var issue in issues.DistinctBy(i => i.Id))
        {
            if (!issue.SafeToFix)
            {
                skipped++;
                messages.Add($"Left alone (report only): {issue.FullKeyPath}");
            }
            else if (!IsChangeAllowed(issue))
            {
                skipped++;
                messages.Add($"Left alone (protected location): {issue.FullKeyPath}");
            }
            else
            {
                fixable.Add(issue);
            }
        }

        if (fixable.Count == 0)
        {
            return new RegistryFixResult(0, skipped, 0, null, ["Nothing was changed.", .. messages]);
        }

        if (!PlatformGuard.IsWindows)
        {
            messages.Insert(0, "Registry fixes are only possible on Windows.");
            return new RegistryFixResult(0, skipped + fixable.Count, 0, null, messages);
        }

        // Restore point ON for this module regardless of the global default: the .reg backup is the first undo, the
        // restore point the second.
        var options = new ChangeOptions(CreateBefore: true, CreateAfter: null, ModuleName, RegistryIssueRules.Summarize(fixable),
            Reversible: true, UndoHint);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string description = $"Remove {fixable.Count} leftover registry {(fixable.Count == 1 ? "entry" : "entries")}";
            var guarded = await _guard.RunGuardedAsync(description, options,
                ct => Task.Run(() => FixCoreAsync(fixable, progress, ct), ct), progress, cancellationToken).ConfigureAwait(false);

            var value = guarded.Value ?? new RegistryFixResult(0, 0, fixable.Count, null, []);
            var all = new List<string> { guarded.Message };
            all.AddRange(value.Messages);
            all.AddRange(messages);
            return value with { Skipped = value.Skipped + skipped, Messages = all };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OperationResult<RegistryFixResult>> FixCoreAsync(IReadOnlyList<RegistryIssue> issues, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var access = new RegistryAccess();
        var checks = new RegistryChecks(access, PathProbe.CreateSystem());
        var messages = new List<string>();
        var sections = new List<string>();
        var backedUp = new List<(RegistryIssue Issue, string? Fingerprint)>();
        int skipped = 0;

        // 1. Re-check and back up each entry. Anything that changed since the scan, or cannot be backed up, is skipped.
        for (int i = 0; i < issues.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var issue = issues[i];
            progress?.Report(new ProgressUpdate("Backing up", 50.0 * i / issues.Count, issue.FullKeyPath));

            if (!StillBroken(checks, issue))
            {
                skipped++;
                messages.Add($"Skipped, changed since the scan: {issue.FullKeyPath}{ValueSuffix(issue)}");
                continue;
            }

            var (text, fingerprint) = await BackupAsync(access, issue, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                skipped++;
                messages.Add($"Skipped, could not be backed up: {issue.FullKeyPath}{ValueSuffix(issue)}");
                continue;
            }

            sections.Add(text);
            backedUp.Add((issue, fingerprint));
        }

        if (backedUp.Count == 0)
        {
            var none = new RegistryFixResult(0, skipped, 0, null, messages);
            return OperationResult<RegistryFixResult>.Ok(none, "Nothing was changed.");
        }

        // 2. Write the backup (one .reg + a JSON sidecar). If that fails nothing is deleted.
        RegistryBackup backup;
        try
        {
            backup = WriteBackup(sections, backedUp.Select(b => b.Issue).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Registry backup could not be written; nothing was removed.");
            var failed = new RegistryFixResult(0, skipped + backedUp.Count, 0, null, messages);
            return new OperationResult<RegistryFixResult>(false, $"The backup could not be saved ({ex.Message}), so nothing was removed.", failed, ex);
        }

        // 3. Re-check once more right before deleting, then delete.
        int fixedCount = 0;
        int failedCount = 0;
        for (int i = 0; i < backedUp.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (issue, fingerprint) = backedUp[i];
            progress?.Report(new ProgressUpdate("Removing", 50 + 50.0 * i / backedUp.Count, issue.FullKeyPath));

            if (!IsChangeAllowed(issue) || !StillBroken(checks, issue) || !SameValue(access, issue, fingerprint))
            {
                skipped++;
                messages.Add($"Skipped, changed while fixing: {issue.FullKeyPath}{ValueSuffix(issue)}");
                continue;
            }

            var error = Delete(access, issue);
            if (error is null)
            {
                fixedCount++;
                _logger.LogInformation("Registry leftover removed: {Key}{Value} (backup {Backup})", issue.FullKeyPath, ValueSuffix(issue), backup.Id);
                await JournalAsync(issue, "Succeeded: removed", backup.Id).ConfigureAwait(false);
            }
            else
            {
                failedCount++;
                messages.Add($"Could not remove {issue.FullKeyPath}{ValueSuffix(issue)}: {error}");
                _logger.LogWarning("Registry leftover not removed: {Key}{Value}: {Error}", issue.FullKeyPath, ValueSuffix(issue), error);
                await JournalAsync(issue, "Failed: " + error, backup.Id).ConfigureAwait(false);
            }
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var result = new RegistryFixResult(fixedCount, skipped, failedCount, backup.Id, messages);
        return new OperationResult<RegistryFixResult>(failedCount == 0 || fixedCount > 0, RegistryIssueRules.DescribeFix(result), result);
    }

    /// <summary>One journal record per removed entry, so the Action Journal shows exactly what changed and where the backup is.</summary>
    private async Task JournalAsync(RegistryIssue issue, string outcome, string backupId)
    {
        try
        {
            await _journal.RecordAsync(ActionRecord.Create(ModuleName,
                issue.FixDeletesKey ? "Remove leftover registry key" : "Remove leftover registry value",
                issue.FullKeyPath + ValueSuffix(issue), outcome,
                before: issue.Problem, after: outcome.StartsWith("Succeeded", StringComparison.Ordinal) ? "Removed" : null,
                reversible: true, undoHint: $"{UndoHint} ({backupId})")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the registry fix to the action journal.");
        }
    }

    private static bool IsChangeAllowed(RegistryIssue issue) => issue.FixDeletesKey
        ? ProtectedRegistryPaths.CanDeleteKey(issue.Hive, issue.KeyPath)
        : ProtectedRegistryPaths.CanDeleteValue(issue.Hive, issue.KeyPath, issue.ValueName);

    private static bool StillBroken(RegistryChecks checks, RegistryIssue issue)
    {
        var again = checks.Evaluate(new RegistryCandidate(issue.Category, issue.Hive, issue.KeyPath, issue.ValueName));
        return again is not null &&
               again.Id == issue.Id &&
               string.Equals(again.MissingTarget, issue.MissingTarget, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameValue(RegistryAccess access, RegistryIssue issue, string? fingerprint)
    {
        if (issue.FixDeletesKey)
        {
            return access.KeyExists(issue.Hive, issue.KeyPath);
        }

        using var key = access.Open(issue.Hive, issue.KeyPath);
        return key is not null && RegistryAccess.Fingerprint(key, issue.ValueName) == fingerprint;
    }

    private async Task<(string? Text, string? Fingerprint)> BackupAsync(RegistryAccess access, RegistryIssue issue, CancellationToken cancellationToken)
    {
        if (!issue.FixDeletesKey)
        {
            // A single value: write exactly that value (reg export cannot export one value).
            using var key = access.Open(issue.Hive, issue.KeyPath);
            if (key is null || issue.ValueName is null || !RegistryAccess.ValueNames(key).Contains(issue.ValueName, StringComparer.OrdinalIgnoreCase))
            {
                return (null, null);
            }

            var value = new RegExportValue(issue.ValueName, RegistryAccess.Kind(key, issue.ValueName), RegistryAccess.Raw(key, issue.ValueName));
            return (RegExportBuilder.Build([new RegExportKey(issue.FullKeyPath, [value])]), RegistryAccess.Fingerprint(key, issue.ValueName));
        }

        // A whole key: reg.exe exports every value type and sub-key exactly.
        string temp = Path.Combine(Path.GetTempPath(), $"pcchecker-reg-{Guid.NewGuid():N}.reg");
        try
        {
            var output = await CommandRunner.RunAsync(RegExe(), $"export \"{issue.Hive}\\{issue.KeyPath}\" \"{temp}\" /y /reg:64",
                TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
            if (output.Success && File.Exists(temp))
            {
                var text = await File.ReadAllTextAsync(temp, cancellationToken).ConfigureAwait(false);
                if (RegExportBuilder.KeyNames(text).Count > 0)
                {
                    return (text, null);
                }
            }

            _logger.LogDebug("reg export failed for {Key} ({Output}); falling back to a value-by-value backup.", issue.FullKeyPath, output.FirstLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "reg export output could not be read for {Key}.", issue.FullKeyPath);
        }
        finally
        {
            TryDelete(temp);
        }

        try
        {
            var tree = access.ReadTree(issue.Hive, issue.KeyPath);
            return tree.Count > 0 ? (RegExportBuilder.Build(tree), null) : (null, null);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Key {Key} could not be backed up.", issue.FullKeyPath);
            return (null, null);
        }
    }

    private RegistryBackup WriteBackup(IReadOnlyList<string> sections, IReadOnlyList<RegistryIssue> issues)
    {
        Directory.CreateDirectory(_backupDirectory);
        var created = DateTimeOffset.Now;
        string id = RegistryBackupCatalog.NewId(created);
        for (int n = 2; File.Exists(Path.Combine(_backupDirectory, id + RegistryBackupCatalog.RegExtension)); n++)
        {
            id = RegistryBackupCatalog.NewId(created) + "-" + n;
        }

        string regPath = Path.Combine(_backupDirectory, id + RegistryBackupCatalog.RegExtension);
        string summary = RegistryIssueRules.Summarize(issues);

        // UTF-16 with a byte-order mark, like regedit and reg export write it.
        File.WriteAllText(regPath, RegExportBuilder.Merge(sections), Encoding.Unicode);
        var manifest = new RegistryBackupManifest(id, created, issues.Count, summary, issues);
        File.WriteAllText(Path.ChangeExtension(regPath, RegistryBackupCatalog.ManifestExtension), RegistryBackupCatalog.SerializeManifest(manifest));
        _logger.LogInformation("Registry backup {Id} written with {Count} entries: {File}", id, issues.Count, regPath);
        return new RegistryBackup(id, created, regPath, issues.Count, summary);
    }

    private static string? Delete(RegistryAccess access, RegistryIssue issue)
    {
        try
        {
            if (issue.FixDeletesKey)
            {
                int cut = issue.KeyPath.LastIndexOf('\\');
                if (cut <= 0)
                {
                    return "not a removable key";
                }

                using var parent = access.Open(issue.Hive, issue.KeyPath[..cut], writable: true);
                if (parent is null)
                {
                    return "the key could not be opened for writing";
                }

                parent.DeleteSubKeyTree(issue.KeyPath[(cut + 1)..], throwOnMissingSubKey: false);
            }
            else
            {
                using var key = access.Open(issue.Hive, issue.KeyPath, writable: true);
                if (key is null)
                {
                    return "the key could not be opened for writing";
                }

                key.DeleteValue(issue.ValueName ?? string.Empty, throwOnMissingValue: false);
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            return ex.Message;
        }
    }

    private static string ValueSuffix(RegistryIssue issue) => issue.FixDeletesKey || issue.ValueName is null ? string.Empty : $" → {issue.ValueName}";

    private static void TryDelete(string path)
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
            // A leftover temp file is harmless.
        }
    }
}
