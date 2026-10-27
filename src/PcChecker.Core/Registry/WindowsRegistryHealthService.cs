using System.Diagnostics;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Restore;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Startup;

namespace PcChecker.Core.RegistryHealth;

/// <summary>
/// Windows implementation: read-only scans through <see cref="RegistryChecks"/> on a background thread, and fixes that
/// back up every key first (<c>reg export</c>, or a value-by-value .reg when that is not possible), re-check each entry
/// right before removing it, and run inside the change guard with a restore point.
/// </summary>
public sealed partial class WindowsRegistryHealthService : IRegistryHealthService
{
    public const string ModuleName = "Registry";
    public const string UndoHint = "Registry page → Backups → Restore";

    private readonly IChangeGuard _guard;
    private readonly IActionJournal _journal;
    private readonly ILogger<WindowsRegistryHealthService> _logger;
    private readonly string _backupDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsRegistryHealthService(IChangeGuard guard, IActionJournal journal, ILogger<WindowsRegistryHealthService> logger)
        : this(guard, journal, logger, RegistryBackupCatalog.DefaultDirectory(Environment.GetEnvironmentVariable))
    {
    }

    internal WindowsRegistryHealthService(IChangeGuard guard, IActionJournal journal, ILogger<WindowsRegistryHealthService> logger, string backupDirectory)
    {
        _guard = guard;
        _journal = journal;
        _logger = logger;
        _backupDirectory = backupDirectory;
    }

    public IReadOnlyList<RegistryIssueCategory> Categories { get; } = Enum.GetValues<RegistryIssueCategory>();

    public async Task<RegistryScanResult> ScanAsync(IEnumerable<RegistryIssueCategory> categories, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var wanted = (categories ?? Categories).Distinct().ToList();
        if (wanted.Count == 0)
        {
            wanted = Categories.ToList();
        }

        if (!PlatformGuard.IsWindows)
        {
            return new RegistryScanResult([], RegistryIssueRules.CountByCategory([], wanted), 0, TimeSpan.Zero,
                ["The registry can only be checked on Windows."], DateTimeOffset.Now);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Scan(wanted, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RegistryScanResult Scan(IReadOnlyList<RegistryIssueCategory> categories, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var access = new RegistryAccess();
        var checks = new RegistryChecks(access, PathProbe.CreateSystem());
        var issues = new List<RegistryIssue>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < categories.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = categories[i];
            var info = RegistryIssueRules.Info(category);
            progress?.Report(new ProgressUpdate(info.Name, 100.0 * i / categories.Count, $"{issues.Count} found so far"));
            int deniedBefore = access.AccessDenied;
            int found = 0;

            try
            {
                foreach (var candidate in checks.Enumerate(category, cancellationToken))
                {
                    RegistryIssue? issue;
                    try
                    {
                        issue = checks.Evaluate(candidate);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Registry check skipped {Hive}\\{Key}", candidate.Hive, candidate.KeyPath);
                        continue;
                    }

                    if (issue is not null && seen.Add(issue.Id))
                    {
                        issues.Add(issue);
                        found++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Registry category {Category} could not be checked completely.", category);
                warnings.Add($"{info.Name}: the check stopped early ({ex.Message}).");
            }

            int denied = access.AccessDenied - deniedBefore;
            if (denied > 0)
            {
                warnings.Add($"{info.Name}: Windows did not allow reading {denied} {(denied == 1 ? "key" : "keys")}; {(denied == 1 ? "it was" : "they were")} skipped.");
            }

            _logger.LogInformation("Registry check {Category}: {Found} leftover(s).", category, found);
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        stopwatch.Stop();
        var ordered = issues.OrderBy(i => i.Category).ThenBy(i => i.Hive).ThenBy(i => i.KeyPath, StringComparer.OrdinalIgnoreCase).ToList();
        _logger.LogInformation("Registry scan finished in {Seconds:N1} s: {Count} leftover(s) in {Keys} keys.", stopwatch.Elapsed.TotalSeconds, ordered.Count, access.KeysOpened);
        return new RegistryScanResult(ordered, RegistryIssueRules.CountByCategory(ordered, categories), access.KeysOpened, stopwatch.Elapsed, warnings, DateTimeOffset.Now);
    }

    public async Task<IReadOnlyList<RegistryBackup>> ListBackupsAsync(CancellationToken cancellationToken)
    {
        return await Task.Run<IReadOnlyList<RegistryBackup>>(() =>
        {
            try
            {
                if (!Directory.Exists(_backupDirectory))
                {
                    return [];
                }

                var list = new List<RegistryBackup>();
                foreach (var file in Directory.EnumerateFiles(_backupDirectory, RegistryBackupCatalog.FilePrefix + "*" + RegistryBackupCatalog.RegExtension))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (!RegistryBackupCatalog.IsValidId(id))
                    {
                        continue;
                    }

                    var sidecar = Path.ChangeExtension(file, RegistryBackupCatalog.ManifestExtension);
                    var manifest = File.Exists(sidecar) ? RegistryBackupCatalog.DeserializeManifest(SafeRead(sidecar)) : null;
                    list.Add(RegistryBackupCatalog.ToBackup(id, file, manifest, new DateTimeOffset(File.GetCreationTime(file))));
                }

                return list.OrderByDescending(b => b.Created).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Registry backups could not be listed.");
                return [];
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationResult> RestoreBackupAsync(string backupId, CancellationToken cancellationToken)
    {
        if (!RegistryBackupCatalog.IsValidId(backupId))
        {
            return Task.FromResult(OperationResult.Fail("That is not a PC Checker registry backup."));
        }

        string file = Path.Combine(_backupDirectory, backupId + RegistryBackupCatalog.RegExtension);
        if (!File.Exists(file))
        {
            return Task.FromResult(OperationResult.Fail("The backup file no longer exists."));
        }

        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Registry backups can only be restored on Windows."));
        }

        // The backup folder is in the user's profile, where any program can change it, while the import runs elevated.
        // Read both files once, check them against the seal kept in HKLM and against the entries they were made for,
        // and import exactly those bytes from a folder only administrators can write to.
        byte[] regFile;
        byte[] manifestFile;
        try
        {
            regFile = File.ReadAllBytes(file);
            manifestFile = File.ReadAllBytes(Path.ChangeExtension(file, RegistryBackupCatalog.ManifestExtension));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(OperationResult.Fail("The backup could not be read, so it was not imported: " + ex.Message, ex));
        }

        var manifest = RegistryBackupCatalog.DeserializeManifest(RegistryBackupCatalog.DecodeText(manifestFile));
        if (!RegistryBackupCatalog.SealMatches(regFile, manifestFile, RegistryBackupSeals.Read(backupId))
            || manifest is null || !string.Equals(manifest.Id, backupId, StringComparison.Ordinal))
        {
            _logger.LogWarning("Registry backup {Id} was not imported: it does not match the seal recorded when it was saved.", backupId);
            return Task.FromResult(OperationResult.Fail(
                "This backup was not imported because PC Checker cannot confirm it is unchanged since it was saved. Backups made by " +
                "older versions cannot be confirmed either. If you trust it, open the .reg file in Registry Editor to check it and import it there."));
        }

        var foreign = RegistryBackupCatalog.KeysOutsideBackup(RegistryBackupCatalog.DecodeText(regFile), manifest.Issues);
        if (foreign.Count > 0)
        {
            _logger.LogWarning("Registry backup {Id} was not imported: it contains {Key}, which is not one of its entries.", backupId, foreign[0]);
            return Task.FromResult(OperationResult.Fail($"This backup was not imported because it contains a key it was not made for ({foreign[0]})."));
        }

        var options = new ChangeOptions(CreateBefore: true, CreateAfter: null, ModuleName, file, Reversible: false,
            UndoHint: "Use the restore point created before the restore.");
        return _guard.RunGuardedAsync($"Restore registry backup {backupId}", options, async ct =>
        {
            CommandOutput output;
            try
            {
                output = await ProtectedTaskFile.UseFolderAsync("PcChecker-reg-", "registry import", async (folder, c) =>
                {
                    string verified = Path.Combine(folder, "restore.reg");
                    await File.WriteAllBytesAsync(verified, regFile, c).ConfigureAwait(false);
                    return await CommandRunner.RunAsync(RegExe(), $"import \"{verified}\" /reg:64", TimeSpan.FromMinutes(2), c).ConfigureAwait(false);
                }, ex => _logger.LogDebug(ex, "Could not remove the temporary registry import folder."), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
            {
                return OperationResult.Fail("The backup could not be prepared in a protected folder, so it was not imported: " + ex.Message, ex);
            }

            if (!output.Success)
            {
                return OperationResult.Fail($"Windows could not import the backup: {output.FirstLine}");
            }

            _logger.LogInformation("Registry backup {Id} imported.", backupId);
            return OperationResult.Ok("The backed-up entries are back in the registry. Scan again to see them.");
        }, null, cancellationToken);
    }

    public Task<OperationResult> DeleteBackupAsync(string backupId, CancellationToken cancellationToken)
    {
        if (!RegistryBackupCatalog.IsValidId(backupId))
        {
            return Task.FromResult(OperationResult.Fail("That is not a PC Checker registry backup."));
        }

        string file = Path.Combine(_backupDirectory, backupId + RegistryBackupCatalog.RegExtension);
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, ModuleName, file, Reversible: false, UndoHint: null);
        return _guard.RunGuardedAsync($"Delete registry backup {backupId}", options, _ =>
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                var sidecar = Path.ChangeExtension(file, RegistryBackupCatalog.ManifestExtension);
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }

                if (PlatformGuard.IsWindows)
                {
                    RegistryBackupSeals.Remove(backupId);
                }

                return Task.FromResult(OperationResult.Ok("Backup deleted."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(OperationResult.Fail($"The backup could not be deleted: {ex.Message}", ex));
            }
        }, null, cancellationToken);
    }

    private static string RegExe() => RegistryBackupStore.RegExe();

    private static string? SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
