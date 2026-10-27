using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Processes;

/// <summary>End task, end process tree, priority and the shell links.</summary>
public sealed partial class WindowsProcessService
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    public Task<OperationResult> EndProcessAsync(ProcessInfo process, bool includeChildren, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        if (Refusal(process) is { } refusal)
        {
            return Task.FromResult(OperationResult.Fail(refusal));
        }

        string target = $"{process.Name} (PID {process.Pid})";
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, target, Reversible: false,
            UndoHint: "Ending a program cannot be undone. Start it again from the Start menu or its shortcut; unsaved work in it is lost.");
        string description = includeChildren ? $"End process tree {target}" : $"End process {target}";
        return _guard.RunGuardedAsync(description, options, ct => Task.Run(() => EndCore(process, includeChildren, ct), ct), null, cancellationToken);
    }

    public Task<OperationResult> SetPriorityAsync(ProcessInfo process, ProcessPriority priority, bool allowRealtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        if (Refusal(process) is { } refusal)
        {
            return Task.FromResult(OperationResult.Fail(refusal));
        }

        if (!PriorityRules.IsAllowed(priority, allowRealtime))
        {
            return Task.FromResult(OperationResult.Fail("Realtime priority is only offered in Advanced mode."));
        }

        string label = PriorityRules.Label(priority);
        string before = PriorityRules.Label(process.Priority);
        string target = $"{process.Name} (PID {process.Pid})";
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, target, Reversible: true,
            UndoHint: $"Set the priority back to {before} on the Processes page. It also goes back to normal when the program restarts.");

        return _guard.RunGuardedAsync($"Set priority of {target} to {label}", options, _ => Task.Run(() =>
        {
            var (ok, error, previous, applied) = ProcessNative.SetPriority(process.Pid, process.StartTime!.Value.ToFileTime(), PriorityRules.ToPriorityClass(priority));
            if (ok)
            {
                string was = PriorityRules.Label(PriorityRules.FromPriorityClass(previous) ?? process.Priority);
                return OperationResult.Ok(PriorityRules.ResultMessage(process.Name, priority, PriorityRules.FromPriorityClass(applied), was));
            }

            return OperationResult.Fail(error switch
            {
                5 => $"Windows refused to change the priority of {process.Name}. It is protected or belongs to another account.",
                87 => $"{process.Name} is no longer running.",
                _ => $"Could not change the priority of {process.Name} (error {error}).",
            });
        }), null, cancellationToken);
    }

    public OperationResult OpenFileLocation(ProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!ProcessSuspicionRules.IsFullPath(process.Path) || !File.Exists(process.Path))
        {
            return OperationResult.Fail($"The program file of {process.Name} is not known or no longer exists.");
        }

        // Explorer only understands /select with the path glued on as one argument, /select,"C:\path\file.exe", which
        // ArgumentList cannot produce (it would quote the whole token). The path is a fully qualified existing file, and
        // Windows file names cannot contain a quote, so it cannot break out of the quotes.
        return ShellLauncher.Open("explorer.exe", $"/select,\"{process.Path}\"");
    }

    public OperationResult SearchOnline(ProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return ShellLauncher.Open(ProcessText.SearchUrl(process.Name));
    }

    /// <summary>Why the process may not be touched, or null.</summary>
    private string? Refusal(ProcessInfo process)
    {
        if (CriticalProcessRules.Reason(process.Name, process.Pid, OwnProcessId) is { } reason)
        {
            return reason;
        }

        if (process.StartTime is null)
        {
            return $"PC Checker cannot tell {process.Name} apart from a newer process with the same ID, so it leaves it alone. Use Task Manager if you need to end it.";
        }

        // The name list covers the well-known ones; Windows also flags others (such as some svchost hosts) as critical.
        return ProcessNative.IsCriticalProcess(process.Pid) == true ? CriticalProcessRules.MarkedCriticalReason : null;
    }

    private OperationResult EndCore(ProcessInfo process, bool includeChildren, CancellationToken cancellationToken)
    {
        var targets = new List<ProcessLink>();
        var skipped = new List<string>();
        if (includeChildren)
        {
            var links = ProcessNative.EnumerateProcesses()
                .Where(e => e.Pid != CriticalProcessRules.IdlePid)
                .Select(e => new ProcessLink(e.Pid, e.ParentPid, ProcessNative.TryGetStartTime(e.Pid), e.Name))
                .ToList();
            foreach (var child in ProcessTree.Descendants(process.Pid, process.StartTime, links))
            {
                if (CriticalProcessRules.IsCritical(child.Name, child.Pid, OwnProcessId))
                {
                    skipped.Add(child.Name);
                }
                else
                {
                    targets.Add(child);
                }
            }
        }

        targets.Add(new ProcessLink(process.Pid, process.ParentPid, process.StartTime, process.Name));
        int ended = 0;
        var problems = new List<string>();
        ProcessNative.EndOutcome rootOutcome = ProcessNative.EndOutcome.Failed;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, error) = ProcessNative.Terminate(target.Pid, target.StartTime!.Value.ToFileTime(), ExitWait);
            if (target.Pid == process.Pid)
            {
                rootOutcome = outcome;
                ended += outcome is ProcessNative.EndOutcome.Ended or ProcessNative.EndOutcome.StillRunning ? 1 : 0;
                continue;
            }

            switch (outcome)
            {
                case ProcessNative.EndOutcome.Ended:
                    ended++;
                    break;
                case ProcessNative.EndOutcome.AlreadyExited:
                    break;
                case ProcessNative.EndOutcome.StillRunning:
                    ended++;
                    problems.Add($"{target.Name} is still closing");
                    break;
                case ProcessNative.EndOutcome.AccessDenied:
                    problems.Add($"Windows refused to end {target.Name} (PID {target.Pid})");
                    break;
                case ProcessNative.EndOutcome.Critical:
                    skipped.Add(target.Name);
                    break;
                default:
                    problems.Add($"{target.Name} (PID {target.Pid}) could not be ended (error {error})");
                    break;
            }
        }

        _logger.LogInformation("Ended {Count} process(es) for {Name} ({Pid}); problems: {Problems}", ended, process.Name, process.Pid, problems.Count);
        string notes = (skipped.Count > 0 ? $" Left running because Windows needs them: {string.Join(", ", skipped.Distinct())}." : string.Empty)
                       + (problems.Count > 0 ? " " + string.Join(". ", problems) + "." : string.Empty);

        return rootOutcome switch
        {
            ProcessNative.EndOutcome.Ended or ProcessNative.EndOutcome.StillRunning =>
                OperationResult.Ok((includeChildren && ended > 1 ? $"{process.Name} and {ended - 1} process(es) it started were ended." : $"{process.Name} was ended.") + notes),
            ProcessNative.EndOutcome.AlreadyExited => OperationResult.Ok($"{process.Name} had already closed." + notes),
            ProcessNative.EndOutcome.AccessDenied => OperationResult.Fail($"Windows refused to end {process.Name}. It is protected or belongs to another account." + notes),
            ProcessNative.EndOutcome.Critical => OperationResult.Fail(CriticalProcessRules.MarkedCriticalReason + notes),
            _ => OperationResult.Fail($"{process.Name} could not be ended." + notes),
        };
    }
}
