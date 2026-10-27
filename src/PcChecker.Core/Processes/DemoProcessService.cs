using PcChecker.Core.Common;

namespace PcChecker.Core.Processes;

/// <summary>
/// Demo process list: the sample gaming desktop with a game running, Steam, Discord, Chrome with several tabs, a
/// frozen OBS Studio ("Not responding"), a suspended Spotify and the usual background and Windows processes, plus one
/// unsigned helper in a Temp folder so the "look closer" flag shows. Values wobble on every refresh. Every action
/// changes only the in-memory list.
/// </summary>
public sealed class DemoProcessService : IProcessService
{
    private const string DemoWindows = @"C:\Windows";

    private readonly object _gate = new();
    private readonly Random _random = DemoData.CreateRandom(4071);
    private readonly DateTimeOffset _boot = DateTimeOffset.Now.AddHours(-6).AddMinutes(-12);
    private readonly List<DemoProcessTemplate> _processes;

    public DemoProcessService()
    {
        _processes = DemoProcessData.Build(_boot);
    }

    public int OwnProcessId => DemoProcessData.OwnPid;

    public int LogicalProcessorCount => 24;

    public async Task<ProcessSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 30, 120).ConfigureAwait(false);
        lock (_gate)
        {
            var list = _processes.Select(Wobble).ToList();
            double total = Math.Min(100, list.Sum(p => p.CpuPercent) + DemoData.Jitter(_random, 1.5, 0.5));
            long used = (long)(DemoData.Jitter(_random, 14.6, 0.15) * ByteSize.Gigabyte);
            return new ProcessSnapshot(DateTimeOffset.Now, list, Math.Round(total, 1), used, DemoData.TotalMemoryBytes, true, [])
            {
                ConnectionsAvailable = true,
            };
        }
    }

    /// <summary>Demo values are generated fresh on every sample, so there is no baseline to forget.</summary>
    public void ResetRates()
    {
    }

    public async Task<OperationResult> EndProcessAsync(ProcessInfo process, bool includeChildren, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (CriticalProcessRules.Reason(process.Name, process.Pid, OwnProcessId) is { } reason)
        {
            return OperationResult.Fail(reason);
        }

        await DemoData.DelayAsync(cancellationToken, 150, 400).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_processes.Any(p => p.Pid == process.Pid))
            {
                return OperationResult.Ok($"{process.Name} had already closed (demo).");
            }

            int removed = Remove(process.Pid, includeChildren);
            return OperationResult.Ok(removed > 1
                ? $"{process.Name} and {removed - 1} process(es) it started were ended (demo)."
                : $"{process.Name} was ended (demo).");
        }
    }

    public async Task<OperationResult> SetPriorityAsync(ProcessInfo process, ProcessPriority priority, bool allowRealtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (CriticalProcessRules.Reason(process.Name, process.Pid, OwnProcessId) is { } reason)
        {
            return OperationResult.Fail(reason);
        }

        if (!PriorityRules.IsAllowed(priority, allowRealtime))
        {
            return OperationResult.Fail("Realtime priority is only offered in Advanced mode.");
        }

        await DemoData.DelayAsync(cancellationToken, 100, 250).ConfigureAwait(false);
        return Change(process, t =>
        {
            string was = PriorityRules.Label(t.Priority);
            t.Priority = priority;
            return $"{process.Name} now runs at {PriorityRules.Label(priority)} priority (was {was}) (demo).";
        });
    }

    public OperationResult OpenFileLocation(ProcessInfo process) =>
        OperationResult.Ok($"Would open the folder of {process?.Name} (demo).");

    public OperationResult SearchOnline(ProcessInfo process) =>
        OperationResult.Ok($"Would search the web for {ProcessCatalog.Normalize(process?.Name)}.exe (demo).");

    public async Task<OperationResult<bool>> CloseWindowsAsync(ProcessInfo process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (CriticalProcessRules.Reason(process.Name, process.Pid, OwnProcessId) is { } reason)
        {
            return OperationResult<bool>.Fail(reason);
        }

        await DemoData.DelayAsync(cancellationToken, 300, 600).ConfigureAwait(false);
        lock (_gate)
        {
            var target = _processes.FirstOrDefault(p => p.Pid == process.Pid);
            if (target?.Window is null)
            {
                return OperationResult<bool>.Ok(false, $"{process.Name} has no window to close (demo).");
            }

            // A frozen or paused program cannot react to the close request.
            if (target.State != ProcessRunState.Running)
            {
                return OperationResult<bool>.Ok(false, $"{process.Name} did not react (demo).");
            }

            Remove(process.Pid, includeChildren: true);
            return OperationResult<bool>.Ok(true, $"{process.Name} was closed (demo).");
        }
    }

    public Task<OperationResult> SuspendAsync(ProcessInfo process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (ProcessSafetyRules.SuspendRefusal(process, DemoWindows, OwnProcessId) is { } reason)
        {
            return Task.FromResult(OperationResult.Fail(reason));
        }

        return Task.FromResult(Change(process, t =>
        {
            t.State = ProcessRunState.Suspended;
            return $"{process.Name} is paused (demo).";
        }));
    }

    public Task<OperationResult> ResumeAsync(ProcessInfo process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (ProcessSafetyRules.ResumeRefusal(process, OwnProcessId) is { } reason)
        {
            return Task.FromResult(OperationResult.Fail(reason));
        }

        return Task.FromResult(Change(process, t =>
        {
            t.State = ProcessRunState.Running;
            return $"{process.Name} runs again (demo).";
        }));
    }

    public Task<OperationResult> SetEfficiencyModeAsync(ProcessInfo process, bool on, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (ProcessSafetyRules.EfficiencyRefusal(process, DemoWindows, OwnProcessId) is { } reason)
        {
            return Task.FromResult(OperationResult.Fail(reason));
        }

        return Task.FromResult(Change(process, t =>
        {
            t.Efficiency = on;
            t.Priority = EfficiencyModeRules.Request(on).Priority;
            return on ? $"{process.Name} now runs in efficiency mode (demo)." : $"Efficiency mode is off for {process.Name} (demo).";
        }));
    }

    public ulong? GetAffinity(ProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            return _processes.FirstOrDefault(p => p.Pid == process.Pid)?.Affinity ?? AffinityMath.FullMask(LogicalProcessorCount);
        }
    }

    public Task<OperationResult> SetAffinityAsync(ProcessInfo process, ulong mask, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        string? refusal = CriticalProcessRules.Reason(process.Name, process.Pid, OwnProcessId) ?? AffinityMath.Validate(mask, LogicalProcessorCount);
        if (refusal is not null)
        {
            return Task.FromResult(OperationResult.Fail(refusal));
        }

        return Task.FromResult(Change(process, t =>
        {
            t.Affinity = mask;
            return $"{process.Name} now runs on {AffinityMath.Describe(mask, LogicalProcessorCount)} (demo).";
        }));
    }

    public OperationResult ShowWindow(ProcessInfo process, WindowCommand command)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.HasWindow)
        {
            return OperationResult.Fail($"{process.Name} has no window to show.");
        }

        string what = command switch
        {
            WindowCommand.Minimize => "minimize",
            WindowCommand.Maximize => "maximize",
            _ => "switch to",
        };
        return OperationResult.Ok($"Would {what} {process.WindowTitle ?? process.Name} (demo).");
    }

    public OperationResult ShowProperties(ProcessInfo process) =>
        OperationResult.Ok($"Would show the file properties of {process?.Name} (demo).");

    public async Task<OperationResult<string>> CreateDumpAsync(ProcessInfo process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (ProcessSafetyRules.DumpRefusal(process, OwnProcessId) is { } reason)
        {
            return OperationResult<string>.Fail(reason);
        }

        await DemoData.DelayAsync(cancellationToken, 600, 1200).ConfigureAwait(false);
        string path = CommandLineText.DumpFilePath(@"C:\Users\Alex\AppData\Local\Temp", process.Name, _ => false);
        return OperationResult<string>.Ok(path, $"Would save the memory of {process.Name} to {path} (demo).");
    }

    public OperationResult ShowInFolder(string path) => OperationResult.Ok($"Would show {path} in File Explorer (demo).");

    public async Task<OperationResult> RestartAsync(ProcessInfo process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (ProcessSafetyRules.RestartRefusal(process, DemoWindows, OwnProcessId, DemoProcessData.User) is { } reason)
        {
            return OperationResult.Fail(reason);
        }

        await DemoData.DelayAsync(cancellationToken, 400, 800).ConfigureAwait(false);
        return Change(process, t =>
        {
            t.State = ProcessRunState.Running;
            return $"{process.Name} was restarted (demo).";
        });
    }

    public async Task<OperationResult> RunNewTaskAsync(string command, bool asAdministrator, CancellationToken cancellationToken)
    {
        var parsed = CommandLineText.ParseRunCommand(command, _ => false);
        if (parsed is null)
        {
            return OperationResult.Fail("Type the name of a program, folder, document or web address.");
        }

        await DemoData.DelayAsync(cancellationToken, 150, 300).ConfigureAwait(false);
        return OperationResult.Ok(asAdministrator
            ? $"Would start {parsed.Target} with administrator rights (demo)."
            : $"Would start {parsed.Target} (demo).");
    }

    private OperationResult Change(ProcessInfo process, Func<DemoProcessTemplate, string> change)
    {
        lock (_gate)
        {
            var target = _processes.FirstOrDefault(p => p.Pid == process.Pid);
            return target is null ? OperationResult.Fail($"{process.Name} is no longer running (demo).") : OperationResult.Ok(change(target));
        }
    }

    /// <summary>Removes a process (and, with <paramref name="includeChildren"/>, what it started); returns how many went.</summary>
    private int Remove(int pid, bool includeChildren)
    {
        var root = _processes.First(p => p.Pid == pid);
        var removed = new HashSet<int> { root.Pid };
        if (includeChildren)
        {
            var links = _processes.Select(p => new ProcessLink(p.Pid, p.ParentPid, p.Start, p.Name));
            foreach (var child in ProcessTree.Descendants(root.Pid, root.Start, links))
            {
                if (!CriticalProcessRules.IsCritical(child.Name, child.Pid, OwnProcessId))
                {
                    removed.Add(child.Pid);
                }
            }
        }

        _processes.RemoveAll(p => removed.Contains(p.Pid));
        return removed.Count;
    }

    private ProcessInfo Wobble(DemoProcessTemplate t)
    {
        bool paused = t.State == ProcessRunState.Suspended;
        double cpu = t.Cpu <= 0 || paused ? 0 : Math.Max(0, DemoData.Jitter(_random, t.Cpu, Math.Max(0.1, t.Cpu * 0.25)));
        long memory = (long)(Math.Max(0.1, DemoData.Jitter(_random, t.MemoryMb, t.MemoryMb * 0.02)) * ByteSize.Megabyte);
        double read = t.ReadKbps <= 0 || paused ? 0 : Math.Max(0, DemoData.Jitter(_random, t.ReadKbps, t.ReadKbps * 0.5)) * 1024;
        double write = t.WriteKbps <= 0 || paused ? 0 : Math.Max(0, DemoData.Jitter(_random, t.WriteKbps, t.WriteKbps * 0.5)) * 1024;
        double gpu = t.Gpu <= 0 || paused ? 0 : Math.Clamp(DemoData.Jitter(_random, t.Gpu, Math.Max(0.5, t.Gpu * 0.08)), 0, 100);
        var signature = t.Path is null ? SignatureInfo.Unknown : new SignatureInfo(t.Signature, t.Signature == SignatureState.Signed ? t.Publisher : null);
        return new ProcessInfo(t.Pid, t.ParentPid, t.Name, t.Path, t.User, t.Start, Math.Round(cpu, 1), memory, read, write,
            Math.Round(gpu, 1), t.Priority, signature)
        {
            SessionId = t.Session,
            ThreadCount = t.Threads,
            HandleCount = t.Handles,
            Architecture = t.Architecture,
            IsElevated = t.Elevated,
            CommandLine = t.CommandLine,
            Description = t.Description,
            State = t.State,
            EfficiencyMode = t.Efficiency,
            ConnectionCount = t.Connections,
            GpuEngine = gpu >= 0.1 ? t.GpuEngine ?? "GPU 0 - 3D" : null,
            WindowHandle = t.Window is null ? 0 : 0x10000 + t.Pid,
            WindowTitle = t.Window,
        };
    }
}
