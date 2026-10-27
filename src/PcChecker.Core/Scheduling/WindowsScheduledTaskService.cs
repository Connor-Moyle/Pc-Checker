using System.Security.AccessControl;
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

        string xml;
        try
        {
            xml = JobTaskXml.Build(schedule, ExecutablePath!, CurrentUserId(), DateOnly.FromDateTime(DateTime.Now));
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
            var output = await RegisterFromXmlAsync(name, xml, ct).ConfigureAwait(false);
            return output.Success
                ? OperationResult.Ok($"Windows will run \"{ScheduledJobIds.DisplayName(schedule.Job)}\" {ScheduleRules.DescribeInline(schedule)}, even while PC Checker is closed or the PC is on battery.")
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
            var output = await RegisterFromXmlAsync(SchtasksCommandBuilder.LogonTaskName, xml, ct).ConfigureAwait(false);
            return output.Success
                ? OperationResult.Ok(startInTray
                    ? "PC Checker will start in the tray when you sign in, without a UAC prompt."
                    : "PC Checker will open when you sign in, without a UAC prompt.")
                : OperationResult.Fail("Task Scheduler refused the logon task: " + Describe(output));
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

    /// <summary>
    /// Registers a task from its XML definition. The file lives in a folder only administrators can write to (see
    /// <see cref="ProtectedTaskFile"/>), so nobody can swap the definition before schtasks reads it.
    /// </summary>
    private async Task<CommandOutput> RegisterFromXmlAsync(string taskName, string xml, CancellationToken cancellationToken)
    {
        try
        {
            return await ProtectedTaskFile.UseAsync(xml,
                (path, ct) => RunAsync(SchtasksCommandBuilder.CreateFromXml(taskName, path), ct),
                ex => _logger.LogDebug(ex, "Could not remove the temporary task definition folder."),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            _logger.LogWarning(ex, "Could not prepare a protected task definition for {Task}.", taskName);
            return new CommandOutput(-1, string.Empty, "Could not prepare the task definition in a protected folder: " + ex.Message, false);
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
