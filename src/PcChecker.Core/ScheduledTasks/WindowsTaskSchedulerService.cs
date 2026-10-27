using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;
using PcChecker.Core.Restore;
using PcChecker.Core.Startup;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// <see cref="ITaskSchedulerService"/> on Windows. The list comes from the Task Scheduler 2.0 COM API (late bound, the
/// same "Schedule.Service" object Task Scheduler itself uses): state, last and next run and last result as numbers and
/// dates, and each task's XML definition, which <see cref="TaskDefinitionParser"/> reads. Nothing depends on the
/// Windows display language. When the COM API is not available, <c>schtasks /Query /XML ONE</c> gives the definitions
/// without live state. Run, end, delete and restore use schtasks with fixed arguments and exit codes; turning a task
/// off or on uses the COM API, which needs no saved password.
/// </summary>
public sealed partial class WindowsTaskSchedulerService : ITaskSchedulerService
{
    private const string Module = "Scheduled tasks";
    private const int TaskEnumHidden = 1;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsTaskSchedulerService> _logger;
    private readonly ConcurrentDictionary<string, SignatureInfo> _signatures = new(StringComparer.OrdinalIgnoreCase);

    public WindowsTaskSchedulerService(IChangeGuard guard, ILogger<WindowsTaskSchedulerService> logger)
    {
        _guard = guard;
        _logger = logger;
        BackupDirectory = TaskBackupNaming.DefaultDirectory(Environment.GetEnvironmentVariable);
    }

    /// <summary>Where deleted tasks' definitions are saved.</summary>
    public string BackupDirectory { get; }

    private static string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } w ? w : @"C:\Windows";

    private static string SchtasksPath => Path.Combine(WindowsDirectory, "System32", "schtasks.exe");

    public async Task<ScheduledTaskList> GetTasksAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return ScheduledTaskList.Empty("Scheduled tasks can only be read on Windows.");
        }

        ScheduledTaskList raw;
        try
        {
            raw = await Task.Run(() => ReadWithCom(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            _logger.LogWarning(ex, "The Task Scheduler COM API could not be read; falling back to schtasks.");
            raw = await ReadWithSchtasksAsync(cancellationToken).ConfigureAwait(false);
        }

        var completed = await Task.Run(() => Enrich(raw.Tasks, cancellationToken), cancellationToken).ConfigureAwait(false);
        return raw with { Tasks = completed };
    }

    private static ScheduledTaskList ReadWithCom(CancellationToken cancellationToken)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false)
                   ?? throw new InvalidOperationException("Task Scheduler's COM object is not registered.");
        dynamic service = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Task Scheduler's COM object could not be created.");
        service.Connect();

        var tasks = new List<ScheduledTaskInfo>();
        int unreadable = 0;
        var folders = new Stack<dynamic>();
        folders.Push(service.GetFolder("\\"));
        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dynamic folder = folders.Pop();
            try
            {
                dynamic collection = folder.GetTasks(TaskEnumHidden);
                int count = (int)collection.Count;
                for (int i = 1; i <= count; i++)
                {
                    object item = collection.Item(i);
                    if (ReadTask(item) is { } task)
                    {
                        tasks.Add(task);
                    }
                    else
                    {
                        unreadable++;
                    }
                }

                dynamic subfolders = folder.GetFolders(0);
                int folderCount = (int)subfolders.Count;
                for (int i = 1; i <= folderCount; i++)
                {
                    folders.Push(subfolders.Item(i));
                }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // A folder this account may not read (other users' private tasks): count it and carry on.
                unreadable++;
            }
        }

        return new ScheduledTaskList(tasks.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList(), unreadable, true,
            unreadable > 0 ? $"{unreadable} task(s) or folder(s) could not be read." : null);
    }

    private static ScheduledTaskInfo? ReadTask(object comTask)
    {
        dynamic task = comTask;
        try
        {
            string path = (string)task.Path;
            TaskDefinition definition;
            try
            {
                definition = TaskDefinitionParser.Parse((string)task.Xml) ?? TaskDefinition.Unreadable;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                definition = TaskDefinition.Unreadable;
            }

            var state = (TaskRunState)Get(() => (int)task.State, 0);
            DateTime lastRun = Get(() => (DateTime)task.LastRunTime, DateTime.MinValue);
            DateTime nextRun = Get(() => (DateTime)task.NextRunTime, DateTime.MinValue);
            int? lastResult = Get<int?>(() => (int)task.LastTaskResult, null);
            return new ScheduledTaskInfo(TaskPaths.Normalize(path), Enum.IsDefined(state) ? state : TaskRunState.Unknown,
                TaskAnalysis.RealDate(lastRun), lastResult, TaskAnalysis.RealDate(nextRun), definition);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    private static T Get<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return fallback;
        }
    }

    private async Task<ScheduledTaskList> ReadWithSchtasksAsync(CancellationToken cancellationToken)
    {
        var output = await CommandRunner.RunAsync(SchtasksPath, ["/Query", "/XML", "ONE"], TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        if (!output.Success)
        {
            return ScheduledTaskList.Empty("Task Scheduler could not be read: " + (output.TimedOut ? "it did not answer in time." : output.FirstLine));
        }

        var tasks = TaskDefinitionParser.ParseList(output.StdOut)
            .Select(t => new ScheduledTaskInfo(t.Path, t.Definition.Enabled ? TaskRunState.Unknown : TaskRunState.Disabled, null, null, null, t.Definition))
            .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ScheduledTaskList(tasks, 0, false, "Only the task definitions could be read, so state, last run and next run are not shown.");
    }

    /// <summary>Resolves each program, checks signatures (not for Windows' own upkeep tasks) and applies the rules.</summary>
    private IReadOnlyList<ScheduledTaskInfo> Enrich(IReadOnlyList<ScheduledTaskInfo> tasks, CancellationToken cancellationToken)
    {
        var resolved = tasks.Select(t =>
        {
            var program = TaskActionText.ProgramPath(t.Definition.FirstProgram?.Command, Environment.GetEnvironmentVariable, WindowsDirectory, SafeExists);
            bool? exists = program is null ? null : SafeExists(program);
            return (Task: t, Program: program, Exists: exists);
        }).ToList();

        var toCheck = resolved
            .Where(r => r.Exists == true && (!r.Task.IsWindowsTask || TaskSuspicionRules.WritableFolder(r.Program) is not null))
            .Select(r => r.Program!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !_signatures.ContainsKey(p))
            .ToList();
        Parallel.ForEach(toCheck, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            path => _signatures[path] = AuthenticodeVerifier.Verify(path));

        return resolved.Select(r => TaskAnalysis.Complete(r.Task, r.Program, r.Exists,
                r.Program is not null && _signatures.TryGetValue(r.Program, out var signature) ? signature : SignatureInfo.Unknown))
            .ToList();
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public Task<OperationResult> RunAsync(ScheduledTaskInfo task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (TaskSafetyRules.RunRefusal(task, advancedMode: true) is { } refusal)
        {
            return Task.FromResult(OperationResult.Fail(refusal));
        }

        var options = new ChangeOptions(false, false, Module, task.Path, false, "Use End on the Scheduled tasks page to stop it.");
        return _guard.RunGuardedAsync($"Run scheduled task {task.Path}", options, async ct =>
        {
            var output = await SchtasksAsync(["/Run", "/TN", task.Path], ct).ConfigureAwait(false);
            return output.Success
                ? OperationResult.Ok($"{task.Name} was started.")
                : OperationResult.Fail($"Task Scheduler could not start {task.Name}: {Describe(output)}");
        }, null, cancellationToken);
    }

    public Task<OperationResult> EndAsync(ScheduledTaskInfo task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var options = new ChangeOptions(false, false, Module, task.Path, false, "Use Run now on the Scheduled tasks page to start it again.");
        return _guard.RunGuardedAsync($"End scheduled task {task.Path}", options, async ct =>
        {
            var output = await SchtasksAsync(["/End", "/TN", task.Path], ct).ConfigureAwait(false);
            return output.Success
                ? OperationResult.Ok($"{task.Name} was stopped.")
                : OperationResult.Fail($"Task Scheduler could not stop {task.Name}: {Describe(output)}");
        }, null, cancellationToken);
    }

    public Task<OperationResult> SetEnabledAsync(ScheduledTaskInfo task, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsPcCheckerTask)
        {
            return Task.FromResult(OperationResult.Fail(TaskSafetyRules.OwnTaskReason));
        }

        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Scheduled tasks can only be changed on Windows."));
        }

        string verb = enabled ? "Turn on" : "Turn off";
        string undo = enabled
            ? "Turn it off again on the Scheduled tasks page (Apps & Drivers → Scheduled tasks)."
            : "Turn it on again on the Scheduled tasks page (Apps & Drivers → Scheduled tasks), or with Enable in Task Scheduler.";

        // Windows' own tasks are system configuration: a restore point comes first, whatever the setting says.
        var options = new ChangeOptions(task.IsWindowsTask ? true : null, null, Module, task.Path, true, undo);
        return _guard.RunGuardedAsync($"{verb} scheduled task {task.Path} (it was {(task.State == TaskRunState.Disabled ? "off" : "on")})", options,
            ct => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false)
                               ?? throw new InvalidOperationException("Task Scheduler's COM object is not registered.");
                    dynamic service = Activator.CreateInstance(type)!;
                    service.Connect();
                    dynamic registered = service.GetFolder("\\").GetTask(task.Path);
                    registered.Enabled = enabled;
                    return OperationResult.Ok(enabled ? $"{task.Name} is on again." : $"{task.Name} is turned off. It will not run until you turn it on.");
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    _logger.LogWarning(ex, "Could not change {Task}.", task.Path);
                    return OperationResult.Fail($"Task Scheduler refused the change: {ex.Message}", ex);
                }
            }, ct), null, cancellationToken);
    }

    private async Task<CommandOutput> SchtasksAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new CommandOutput(-1, string.Empty, "Scheduled tasks can only be changed on Windows.", false);
        }

        var output = await CommandRunner.RunAsync(SchtasksPath, arguments, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (!output.Success)
        {
            _logger.LogWarning("schtasks {Action} failed with exit code {Code}: {Error}", arguments[0], output.ExitCode, output.FirstLine);
        }

        return output;
    }

    private static string Describe(CommandOutput output) =>
        output.TimedOut ? "it did not answer in time." : (string.IsNullOrWhiteSpace(output.FirstLine) ? $"exit code {output.ExitCode}." : output.FirstLine);

    public OperationResult OpenFileLocation(ScheduledTaskInfo task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.ProgramPath is not { } path || task.ProgramExists != true)
        {
            return OperationResult.Fail($"The program of {task.Name} is not known or no longer exists.");
        }

        return ShellLauncher.Open(Path.Combine(WindowsDirectory, "explorer.exe"), $"/select,\"{path}\"");
    }

    public OperationResult SearchOnline(ScheduledTaskInfo task) => ShellLauncher.Open(TaskAnalysis.SearchUrl(task));

    public OperationResult OpenTaskScheduler() => ShellLauncher.Open(Path.Combine(WindowsDirectory, "System32", "taskschd.msc"));
}
