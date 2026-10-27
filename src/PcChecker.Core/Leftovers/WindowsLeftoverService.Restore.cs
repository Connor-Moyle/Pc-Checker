using System.Security.AccessControl;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.RegistryHealth;
using PcChecker.Core.Restore;
using PcChecker.Core.Scheduling;

namespace PcChecker.Core.Leftovers;

/// <summary>
/// The restore list: registry keys come back through the Registry module's sealed restore, scheduled tasks are
/// registered again from their exported XML once its seal checks out, through a folder only administrators can write
/// to and without replacing a task that exists under the same name.
/// </summary>
public sealed partial class WindowsLeftoverService
{
    private static readonly object RestoreListGate = new();

    private string RestoreListPath => Path.Combine(BackupDirectory, LeftoverBackupNaming.RestoreListFile);

    public Task<IReadOnlyList<LeftoverRestoreEntry>> ListRestoreEntriesAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<LeftoverRestoreEntry>>(() =>
        {
            // Entries whose backup was deleted (for example from the Registry page) can no longer be restored: hide them.
            return LoadRestoreList().Where(e => File.Exists(BackupFileOf(e))).ToList();
        }, cancellationToken);

    public async Task<OperationResult> RestoreAsync(string entryId, CancellationToken cancellationToken)
    {
        var entry = LoadRestoreList().FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
        if (entry is null)
        {
            return OperationResult.Fail("That restore entry no longer exists.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Leftovers can only be restored on Windows.");
        }

        var result = entry.Kind == LeftoverRestoreKind.RegistryKeys
            ? await _registry.RestoreBackupAsync(entry.BackupId, cancellationToken).ConfigureAwait(false)
            : await RestoreTaskAsync(entry, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            MarkRestored(entry.Id);
        }

        return result;
    }

    private async Task<OperationResult> RestoreTaskAsync(LeftoverRestoreEntry entry, CancellationToken cancellationToken)
    {
        if (!LeftoverBackupNaming.IsValidTaskBackupId(entry.BackupId) || !LeftoverBackupNaming.IsRemovableTaskPath(entry.Location))
        {
            return OperationResult.Fail("That is not a PC Checker task backup.");
        }

        byte[] xml;
        try
        {
            xml = await File.ReadAllBytesAsync(BackupFileOf(entry), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail("The task backup could not be read: " + ex.Message, ex);
        }

        // The backup sits in the user's profile and the import runs elevated: only a backup that still matches the seal
        // recorded in HKLM when it was saved, for this very task name, is registered.
        var actions = LeftoverTaskXml.ReadActions(LeftoverTaskXml.Decode(xml));
        if (!LeftoverBackupNaming.SealMatches(xml, entry.Location, LeftoverBackupSeals.Read(entry.BackupId)) || actions is null)
        {
            _logger.LogWarning("Task backup {Id} was not imported: it does not match the seal recorded when it was saved.", entry.BackupId);
            return OperationResult.Fail("This backup was not imported because PC Checker cannot confirm it is unchanged since it was saved.");
        }

        bool needsPassword = actions.NeedsPassword;
        string existing = Path.Combine(WindowsDirectory, "System32", "Tasks", entry.Location.TrimStart('\\'));
        if (File.Exists(existing))
        {
            return OperationResult.Fail($"A task named {entry.Location} exists already, so the backup was not imported over it.");
        }

        var options = new ChangeOptions(CreateBefore: null, CreateAfter: null, ModuleName, entry.Location, Reversible: true,
            UndoHint: "Delete the task again in Task Scheduler.");
        return await _guard.RunGuardedAsync($"Restore scheduled task {entry.Location}", options, async ct =>
        {
            Startup.CommandOutput output;
            try
            {
                output = await ProtectedTaskFile.UseFolderAsync("PcChecker-task-", "task definition", async (folder, c) =>
                {
                    string file = Path.Combine(folder, "task.xml");
                    await File.WriteAllBytesAsync(file, xml, c).ConfigureAwait(false);
                    return await RunToolAsync(Path.Combine(WindowsDirectory, "System32", "schtasks.exe"), ["/Create", "/TN", entry.Location, "/XML", file], c)
                        .ConfigureAwait(false);
                }, ex => _logger.LogDebug(ex, "Could not remove the temporary task folder."), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
            {
                return OperationResult.Fail("The task could not be prepared in a protected folder, so it was not imported: " + ex.Message, ex);
            }

            if (output.ExitCode != 0)
            {
                return OperationResult.Fail(needsPassword
                    ? $"Task Scheduler could not import the task ({Describe(output)}). It ran with a saved password, which a backup cannot hold: " +
                      $"open Task Scheduler, choose Import Task, pick {BackupFileOf(entry)} and type the account's password."
                    : $"Task Scheduler could not import the task ({Describe(output)}).");
            }

            _logger.LogInformation("Leftover task {Task} restored from {Id}.", entry.Location, entry.BackupId);
            return OperationResult.Ok($"The task {entry.Title} is back in Task Scheduler.");
        }, null, cancellationToken).ConfigureAwait(false);
    }

    private string BackupFileOf(LeftoverRestoreEntry entry) => entry.Kind == LeftoverRestoreKind.RegistryKeys
        ? Path.Combine(RegistryBackupDirectory, entry.BackupId + RegistryBackupCatalog.RegExtension)
        : Path.Combine(BackupDirectory, entry.BackupId + LeftoverBackupNaming.TaskExtension);

    private IReadOnlyList<LeftoverRestoreEntry> LoadRestoreList()
    {
        lock (RestoreListGate)
        {
            try
            {
                return File.Exists(RestoreListPath) ? LeftoverBackupNaming.DeserializeRestoreList(File.ReadAllText(RestoreListPath)) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The leftover restore list could not be read.");
                return [];
            }
        }
    }

    private void AppendRestoreEntries(IEnumerable<LeftoverRestoreEntry> entries) =>
        UpdateRestoreList(list => list.InsertRange(0, entries));

    private void MarkRestored(string entryId) =>
        UpdateRestoreList(list =>
        {
            int index = list.FindIndex(e => e.Id == entryId);
            if (index >= 0)
            {
                list[index] = list[index] with { Restored = DateTimeOffset.Now };
            }
        });

    private void UpdateRestoreList(Action<List<LeftoverRestoreEntry>> change)
    {
        lock (RestoreListGate)
        {
            try
            {
                var list = (File.Exists(RestoreListPath) ? LeftoverBackupNaming.DeserializeRestoreList(File.ReadAllText(RestoreListPath)) : []).ToList();
                change(list);
                Directory.CreateDirectory(BackupDirectory);
                var temp = RestoreListPath + ".tmp";
                File.WriteAllText(temp, LeftoverBackupNaming.SerializeRestoreList(list));
                File.Move(temp, RestoreListPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The backups themselves are on disk (and listed on the Registry page); only the list entry is missing.
                _logger.LogWarning(ex, "The leftover restore list could not be updated.");
            }
        }
    }
}
