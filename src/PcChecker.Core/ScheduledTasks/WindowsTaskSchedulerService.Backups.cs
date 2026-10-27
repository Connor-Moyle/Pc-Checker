using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Startup;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// Delete and restore: a task is deleted only after its exact definition is saved (sealed in HKLM), and a saved
/// definition is registered again through a folder only administrators can write to, never over an existing task.
/// </summary>
public sealed partial class WindowsTaskSchedulerService
{
    private static readonly object ListGate = new();

    private string ListPath => Path.Combine(BackupDirectory, TaskBackupNaming.ListFile);

    public async Task<OperationResult> DeleteAsync(ScheduledTaskInfo task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (TaskSafetyRules.DeleteRefusal(task) is { } refusal)
        {
            return OperationResult.Fail(refusal);
        }

        if (!TaskBackupNaming.IsRestorablePath(task.Path))
        {
            return OperationResult.Fail("This task's name cannot be saved safely, so it was not deleted. Turn it off instead.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return OperationResult.Fail("Scheduled tasks can only be changed on Windows.");
        }

        var options = new ChangeOptions(null, null, Module, task.Path, true,
            "Put it back from Apps & Drivers → Scheduled tasks → Deleted tasks → Restore.");
        return await _guard.RunGuardedAsync($"Delete scheduled task {task.Path} (definition saved first)", options, async ct =>
        {
            // 1. The exact current definition, read fresh.
            string xml;
            try
            {
                xml = await Task.Run(() => CurrentXml(task.Path), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return OperationResult.Fail("The task could not be read, so it was not deleted: " + ex.Message, ex);
            }

            var definition = TaskDefinitionParser.Parse(xml);
            if (definition is null)
            {
                return OperationResult.Fail("The task's definition could not be read, so it was not deleted.");
            }

            // 2. Save and seal.
            var created = DateTimeOffset.Now;
            var bytes = TaskBackupNaming.Encode(xml);
            string id;
            string file;
            try
            {
                Directory.CreateDirectory(BackupDirectory);
                id = Leftovers.LeftoverBackupNaming.Unique(TaskBackupNaming.BackupId(created, task.Path),
                    candidate => File.Exists(Path.Combine(BackupDirectory, candidate + TaskBackupNaming.Extension)));
                file = Path.Combine(BackupDirectory, id + TaskBackupNaming.Extension);
                await using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                }

                try
                {
                    TaskBackupSeals.Save(id, TaskBackupNaming.Seal(bytes, task.Path));
                }
                catch (IOException)
                {
                    TryDelete(file);
                    throw;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OperationResult.Fail("The task could not be saved first, so it was not deleted: " + ex.Message, ex);
            }

            // 3. Delete.
            var output = await SchtasksAsync(["/Delete", "/TN", task.Path, "/F"], ct).ConfigureAwait(false);
            if (!output.Success)
            {
                TryDelete(file);
                TaskBackupSeals.Delete(id);
                return OperationResult.Fail($"Task Scheduler could not delete {task.Name}: {Describe(output)}");
            }

            UpdateList(list => list.Prepend(new TaskBackupEntry(id, created, task.Path, definition.NeedsPassword)));
            _logger.LogInformation("Scheduled task {Task} deleted; definition saved as {File}.", task.Path, file);
            return OperationResult.Ok(definition.NeedsPassword
                ? $"{task.Name} was deleted. A copy was saved; it ran with a saved password, so putting it back needs that password."
                : $"{task.Name} was deleted. A copy was saved, so you can put it back under Deleted tasks.");
        }, null, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TaskBackupEntry>> GetBackupsAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<TaskBackupEntry>>(() => LoadList()
            .Where(e => File.Exists(Path.Combine(BackupDirectory, e.Id + TaskBackupNaming.Extension)))
            .ToList(), cancellationToken);

    public async Task<OperationResult> RestoreAsync(TaskBackupEntry backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult.Fail("Scheduled tasks can only be restored on Windows.");
        }

        // Only entries from our own list, with ids and paths we create.
        var entry = LoadList().FirstOrDefault(e => string.Equals(e.Id, backup.Id, StringComparison.Ordinal));
        if (entry is null || !TaskBackupNaming.IsValidId(entry.Id) || !TaskBackupNaming.IsRestorablePath(entry.TaskPath))
        {
            return OperationResult.Fail("That saved task no longer exists.");
        }

        string file = Path.Combine(BackupDirectory, entry.Id + TaskBackupNaming.Extension);
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail("The saved task could not be read: " + ex.Message, ex);
        }

        if (!TaskBackupNaming.SealMatches(bytes, entry.TaskPath, TaskBackupSeals.Read(entry.Id))
            || TaskDefinitionParser.Parse(Leftovers.LeftoverTaskXml.Decode(bytes)) is null)
        {
            _logger.LogWarning("Task backup {Id} was not imported: it does not match the seal recorded when it was saved.", entry.Id);
            return OperationResult.Fail("This saved task was not imported because PC Checker cannot confirm it is unchanged since it was saved.");
        }

        var exists = await SchtasksAsync(["/Query", "/TN", entry.TaskPath], cancellationToken).ConfigureAwait(false);
        if (exists.Success)
        {
            return OperationResult.Fail($"A task named {entry.TaskPath} exists already, so the saved copy was not imported over it.");
        }

        var options = new ChangeOptions(null, null, Module, entry.TaskPath, true, "Delete it again on the Scheduled tasks page.");
        return await _guard.RunGuardedAsync($"Restore scheduled task {entry.TaskPath}", options, async ct =>
        {
            CommandOutput output;
            try
            {
                output = await ProtectedTaskFile.UseFolderAsync("PcChecker-task-", "task definition", async (folder, c) =>
                {
                    string target = Path.Combine(folder, "task.xml");
                    await File.WriteAllBytesAsync(target, bytes, c).ConfigureAwait(false);
                    return await CommandRunner.RunAsync(SchtasksPath, ["/Create", "/TN", entry.TaskPath, "/XML", target], CommandTimeout, c).ConfigureAwait(false);
                }, ex => _logger.LogDebug(ex, "Could not remove the temporary task folder."), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
            {
                return OperationResult.Fail("The task could not be prepared in a protected folder, so it was not imported: " + ex.Message, ex);
            }

            if (!output.Success)
            {
                return OperationResult.Fail(entry.NeedsPassword
                    ? $"Task Scheduler could not import the task ({Describe(output)}). It ran with a saved password, which a copy cannot hold: " +
                      $"open Task Scheduler, choose Import Task, pick {file} and type the account's password."
                    : $"Task Scheduler could not import the task ({Describe(output)}).");
            }

            UpdateList(list => list.Where(e => e.Id != entry.Id));
            TryDelete(file);
            TaskBackupSeals.Delete(entry.Id);
            return OperationResult.Ok($"{entry.Name} is back in Task Scheduler.");
        }, null, cancellationToken).ConfigureAwait(false);
    }

    private static string CurrentXml(string taskPath)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false)
                   ?? throw new InvalidOperationException("Task Scheduler's COM object is not registered.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        dynamic registered = service.GetFolder("\\").GetTask(taskPath);
        return (string)registered.Xml;
    }

    private IReadOnlyList<TaskBackupEntry> LoadList()
    {
        lock (ListGate)
        {
            try
            {
                return File.Exists(ListPath) ? TaskBackupNaming.Deserialize(File.ReadAllText(ListPath)) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The list of deleted tasks could not be read.");
                return [];
            }
        }
    }

    private void UpdateList(Func<IEnumerable<TaskBackupEntry>, IEnumerable<TaskBackupEntry>> change)
    {
        lock (ListGate)
        {
            try
            {
                var current = File.Exists(ListPath) ? TaskBackupNaming.Deserialize(File.ReadAllText(ListPath)) : [];
                Directory.CreateDirectory(BackupDirectory);
                File.WriteAllText(ListPath, TaskBackupNaming.Serialize(change(current)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The list of deleted tasks could not be saved.");
            }
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {File}.", file);
        }
    }
}
