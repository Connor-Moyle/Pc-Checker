using System.Text;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
using PcChecker.Core.Startup;

namespace PcChecker.Core.Scheduling;

/// <summary>
/// <see cref="IScheduledTaskService"/> on <c>schtasks.exe</c>. Creating and deleting tasks are recorded through the
/// change guard (no restore point: a scheduled task is not a system-file change and is removed with one click).
/// </summary>
public sealed class WindowsScheduledTaskService : IScheduledTaskService
{
    private const string Module = "Schedules";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsScheduledTaskService> _logger;

    public WindowsScheduledTaskService(IChangeGuard guard, ILogger<WindowsScheduledTaskService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public bool CanSchedule => PlatformGuard.IsWindows && SchtasksCommandBuilder.ValidateExecutable(ExecutablePath) is null;

    public string? UnavailableReason => !PlatformGuard.IsWindows
        ? "Scheduled tasks need Windows."
        : SchtasksCommandBuilder.ValidateExecutable(ExecutablePath);

    public string? ExecutablePath => Environment.ProcessPath;

    private static string SchtasksPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    public Task<ScheduledTaskStatus> GetStatusAsync(ScheduledJobKind job, CancellationToken cancellationToken) =>
        QueryAsync(ScheduledJobIds.TaskName(job), cancellationToken);

    public Task<ScheduledTaskStatus> GetLogonTaskStatusAsync(CancellationToken cancellationToken) =>
        QueryAsync(SchtasksCommandBuilder.LogonTaskName, cancellationToken);

    public async Task<OperationResult> CreateOrUpdateAsync(JobSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!CanSchedule)
        {
            return OperationResult.Fail(UnavailableReason ?? "Scheduled tasks are not available.");
        }

        if (ScheduleRules.Validate(schedule) is { } problem)
        {
            return OperationResult.Fail(problem);
        }

        SchtasksCommand command;
        try
        {
            command = SchtasksCommandBuilder.Create(schedule, ExecutablePath!);
        }
        catch (ArgumentException ex)
        {
            return OperationResult.Fail(ex.Message, ex);
        }

        var name = ScheduledJobIds.TaskName(schedule.Job);
        var description = $"Schedule \"{ScheduledJobIds.DisplayName(schedule.Job)}\": {ScheduleRules.Describe(schedule)}";
        var options = new ChangeOptions(false, false, Module, name, true,
            "Turn the schedule off in Settings → Schedules, or delete the task from the \"PC Checker\" folder in Task Scheduler.");
        return await _guard.RunGuardedAsync(description, options, async ct =>
        {
            var output = await RunAsync(command, ct).ConfigureAwait(false);
            return output.Success
                ? OperationResult.Ok($"Windows will run \"{ScheduledJobIds.DisplayName(schedule.Job)}\" {ScheduleRules.DescribeInline(schedule)}, even while PC Checker is closed.")
                : OperationResult.Fail("Task Scheduler refused the schedule: " + Describe(output));
        }, null, cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationResult> DeleteAsync(ScheduledJobKind job, CancellationToken cancellationToken) =>
        DeleteTaskAsync(ScheduledJobIds.TaskName(job), $"Remove the \"{ScheduledJobIds.DisplayName(job)}\" schedule", cancellationToken);

    public async Task<OperationResult> SetStartWithWindowsAsync(bool enabled, bool startInTray, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            return await DeleteTaskAsync(SchtasksCommandBuilder.LogonTaskName, "Stop starting PC Checker with Windows", cancellationToken).ConfigureAwait(false);
        }

        if (!CanSchedule)
        {
            return OperationResult.Fail(UnavailableReason ?? "Scheduled tasks are not available.");
        }

        string xml;
        try
        {
            xml = LogonTaskXml.Build(ExecutablePath!, startInTray, CurrentUserId());
        }
        catch (ArgumentException ex)
        {
            return OperationResult.Fail(ex.Message, ex);
        }

        var options = new ChangeOptions(false, false, Module, SchtasksCommandBuilder.LogonTaskName, true,
            "Turn off \"Start with Windows\" in Settings → Tray, or delete the \"Start\" task from the \"PC Checker\" folder in Task Scheduler.");
        return await _guard.RunGuardedAsync("Start PC Checker when you sign in to Windows", options, async ct =>
        {
            var xmlPath = Path.Combine(Path.GetTempPath(), "PcChecker-logon-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                await File.WriteAllTextAsync(xmlPath, xml, Encoding.Unicode, ct).ConfigureAwait(false);
                var output = await RunAsync(SchtasksCommandBuilder.CreateLogon(xmlPath), ct).ConfigureAwait(false);
                return output.Success
                    ? OperationResult.Ok(startInTray
                        ? "PC Checker will start in the tray when you sign in, without a UAC prompt."
                        : "PC Checker will open when you sign in, without a UAC prompt.")
                    : OperationResult.Fail("Task Scheduler refused the logon task: " + Describe(output));
            }
            finally
            {
                TryDelete(xmlPath);
            }
        }, null, cancellationToken).ConfigureAwait(false);
    }

    private static string? CurrentUserId()
    {
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user))
        {
            return null;
        }

        var domain = Environment.UserDomainName;
        return string.IsNullOrWhiteSpace(domain) ? user : domain + "\\" + user;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete the temporary task file {Path}.", path);
        }
    }

    private async Task<OperationResult> DeleteTaskAsync(string taskName, string description, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Scheduled tasks need Windows.");
        }

        var existing = await QueryAsync(taskName, cancellationToken).ConfigureAwait(false);
        if (!existing.Exists)
        {
            return OperationResult.Ok("There was no task to remove.");
        }

        var options = new ChangeOptions(false, false, Module, taskName, false, "Turn the option on again to recreate the task.");
        return await _guard.RunGuardedAsync(description, options, async ct =>
        {
            var output = await RunAsync(SchtasksCommandBuilder.Delete(taskName), ct).ConfigureAwait(false);
            return output.Success
                ? OperationResult.Ok("The scheduled task was removed.")
                : OperationResult.Fail("Task Scheduler could not remove the task: " + Describe(output));
        }, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScheduledTaskStatus> QueryAsync(string taskName, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return ScheduledTaskStatus.Missing(taskName);
        }

        try
        {
            var output = await RunAsync(SchtasksCommandBuilder.Query(taskName), cancellationToken).ConfigureAwait(false);
            if (!output.Success)
            {
                // Exit code 1 with "cannot find the file" is the normal answer for a task that does not exist.
                return ScheduledTaskStatus.Missing(taskName);
            }

            return SchtasksCsvParser.ParseQuery(taskName, output.StdOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not query scheduled task {Task}.", taskName);
            return ScheduledTaskStatus.Missing(taskName);
        }
    }

    private async Task<CommandOutput> RunAsync(SchtasksCommand command, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Running {Command}", command);
        var output = await CommandRunner.RunAsync(SchtasksPath, command.CommandLine, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (!output.Success && command.Arguments[0] != "/Query")
        {
            _logger.LogWarning("{Command} failed with exit code {Code}: {Error}", command, output.ExitCode, output.FirstLine);
        }

        return output;
    }

    private static string Describe(CommandOutput output) =>
        output.TimedOut ? "schtasks did not answer in time." : (string.IsNullOrWhiteSpace(output.FirstLine) ? $"exit code {output.ExitCode}." : output.FirstLine);
}
