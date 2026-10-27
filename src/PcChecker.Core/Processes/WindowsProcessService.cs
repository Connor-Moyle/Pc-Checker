using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Memory;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Processes;

/// <summary>
/// Windows implementation of <see cref="IProcessService"/>: a Toolhelp snapshot for the list, limited-rights process
/// handles for CPU time, I/O, the private working set and efficiency mode, one NtQuerySystemInformation call for
/// threads, handles, sessions and suspended processes, the window list for Apps and "Not responding", the TCP and UDP
/// tables for connections, the "GPU Engine" performance counters for GPU use, and cached Authenticode checks for
/// publishers. Every action runs through <see cref="IChangeGuard"/> (journaled, no restore point: none of them touches
/// the system configuration).
/// </summary>
public sealed partial class WindowsProcessService : IProcessService, IDisposable
{
    private const string Module = "Processes";
    private const string NotOnWindows = "The process list is only available on Windows.";
    private const int SignatureChecksPerBatch = 40;
    private const int DescriptionsPerSample = 40;
    private const int MaxGpuFailures = 3;

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsProcessService> _logger;
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly ProcessRateTracker _rates = new();
    private readonly HungWindowTracker _hung = new();
    private readonly SignatureCache _signatures = new(AuthenticodeVerifier.Verify);
    private readonly Dictionary<string, Identity> _identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposed = new();
    private Dictionary<string, CounterSample> _gpuPrevious = new(StringComparer.Ordinal);
    private PerformanceCounterCategory? _gpuCategory;
    private bool _gpuUnavailable;
    private int _gpuFailures;
    private (long Idle, long Kernel, long User)? _systemTimes;
    private int _signatureWorkerRunning;
    private int _resetRequested;

    public WindowsProcessService(IChangeGuard guard, ILogger<WindowsProcessService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public int OwnProcessId { get; } = Environment.ProcessId;

    public int LogicalProcessorCount { get; } = Environment.ProcessorCount;

    public void ResetRates() => Interlocked.Exchange(ref _resetRequested, 1);

    public async Task<ProcessSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return ProcessSnapshot.Empty with { Timestamp = DateTimeOffset.Now, Warnings = [NotOnWindows] };
        }

        await _sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Sample(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    private ProcessSnapshot Sample(CancellationToken cancellationToken)
    {
        // Runs under the sample gate, so the reset never races a reading in progress.
        if (Interlocked.Exchange(ref _resetRequested, 0) == 1)
        {
            _rates.Reset();
            _gpuPrevious = new Dictionary<string, CounterSample>(StringComparer.Ordinal);
            _systemTimes = null;
        }

        var warnings = new List<string>();
        var entries = ProcessNative.EnumerateProcesses();
        if (entries.Count == 0)
        {
            warnings.Add("Windows did not return the list of running processes. Try again in a moment.");
        }

        var system = ProcessNative.ReadSystemDetails();
        var windows = ProcessWindows.Enumerate();
        var connections = ProcessConnections.Count();
        var now = DateTimeOffset.Now;
        var counters = new List<ProcessCounters>(entries.Count);
        var efficiency = new Dictionary<string, bool>(StringComparer.Ordinal);
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Pid == CriticalProcessRules.IdlePid)
            {
                continue;
            }

            var reading = ProcessNative.Read(entry.Pid);
            var start = reading.Ok ? ProcessNative.FromFileTime(reading.CreationFileTime) : null;
            string key = ProcessKey.For(entry.Pid, start);
            liveKeys.Add(key);
            efficiency[key] = reading.Efficiency;
            var identity = IdentityOf(key, entry.Pid);
            counters.Add(new ProcessCounters(entry.Pid, entry.ParentPid, entry.Name, identity.Path, identity.User, start,
                TimeSpan.FromTicks(reading.CpuTicks), reading.PrivateBytes, reading.ReadBytes, reading.WriteBytes,
                reading.Ok ? PriorityRules.FromPriorityClass(reading.PriorityClass) : null));
        }

        foreach (string stale in _identities.Keys.Where(k => !liveKeys.Contains(k)).ToList())
        {
            _identities.Remove(stale);
        }

        var rates = _rates.Update(counters, now, Environment.ProcessorCount);
        var gpu = ReadGpu();
        double totalCpu = ReadSystemCpu() ?? Math.Min(100, rates.Values.Sum(r => r.CpuPercent));
        long used = 0, total = 0;
        if (MemoryNative.TryGetMemoryStatus(out long totalBytes, out long availableBytes))
        {
            total = totalBytes;
            used = Math.Max(0, totalBytes - availableBytes);
        }

        var hungReadings = counters.Where(c => windows.TryGetValue(c.Pid, out var w) && w.MarkedHung)
            .ToDictionary(c => c.Key, c => windows[c.Pid].PingTimedOut, StringComparer.Ordinal);
        var confirmedHung = _hung.Update(hungReadings);
        int descriptionBudget = DescriptionsPerSample;

        var processes = counters.Select(c =>
        {
            var r = rates.GetValueOrDefault(c.Key);
            var identity = _identities.GetValueOrDefault(c.Key);
            var details = system.GetValueOrDefault(c.Pid);
            bool hasDetails = system.ContainsKey(c.Pid);
            var window = windows.GetValueOrDefault(c.Pid);
            (double Percent, string Engine)? engine = gpu is not null && gpu.TryGetValue(c.Pid, out var e) ? e : null;
            double? gpuPercent = gpu is null ? null : Math.Round(engine?.Percent ?? 0, 1);
            var state = ProcessStatusRules.Decide(hasDetails && details.Suspended, window.MarkedHung, window.PingTimedOut, confirmedHung.Contains(c.Key));
            return new ProcessInfo(c.Pid, c.ParentPid, c.Name, c.Path, c.User, c.StartTime, Math.Round(r.CpuPercent, 1), c.PrivateBytes,
                r.ReadBytesPerSecond, r.WriteBytesPerSecond, gpuPercent, c.Priority, _signatures.Get(c.Path))
            {
                SessionId = hasDetails ? details.Session : null,
                ThreadCount = hasDetails ? details.Threads : null,
                HandleCount = hasDetails ? details.Handles : null,
                Architecture = identity?.Static.Architecture ?? ProcessArchitecture.Unknown,
                IsElevated = identity?.Static.Elevated,
                CommandLine = identity?.Static.CommandLine,
                Description = DescriptionOf(c.Path, ref descriptionBudget),
                State = state,
                EfficiencyMode = efficiency.GetValueOrDefault(c.Key),
                ConnectionCount = connections is null ? null : connections.GetValueOrDefault(c.Pid),
                GpuEngine = engine?.Engine,
                WindowHandle = window.Handle,
                WindowTitle = window.Handle == 0 ? null : window.Title,
            };
        }).ToList();

        QueueSignatureChecks(processes);
        return new ProcessSnapshot(now, processes, Math.Round(totalCpu, 1), used, total, gpu is not null, warnings)
        {
            ConnectionsAvailable = connections is not null,
        };
    }

    /// <summary>Path, account, architecture, elevation and command line of one run of a program; read once per process and cached.</summary>
    private Identity IdentityOf(string key, int pid)
    {
        if (_identities.TryGetValue(key, out var known))
        {
            return known;
        }

        var (path, sid) = ProcessNative.ReadIdentity(pid);
        string? user = null;
        if (sid is not null && !_accounts.TryGetValue(sid, out user))
        {
            user = ProcessNative.TryTranslateSid(sid);
            _accounts[sid] = user;
        }

        var identity = new Identity(path, user, ProcessNative.ReadStaticDetails(pid));
        _identities[key] = identity;
        return identity;
    }

    /// <summary>The file description from the version information, read for a few new files per refresh and cached.</summary>
    private string? DescriptionOf(string? path, ref int budget)
    {
        if (!ProcessSuspicionRules.IsFullPath(path))
        {
            return null;
        }

        if (_descriptions.TryGetValue(path, out var known))
        {
            return known;
        }

        if (budget <= 0)
        {
            return null;
        }

        budget--;
        string? description = null;
        try
        {
            description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the version information of {Path}.", path);
        }

        description = string.IsNullOrWhiteSpace(description) ? null : description;
        _descriptions[path] = description;
        return description;
    }

    /// <summary>Checks signatures of new program files in the background, busiest processes first.</summary>
    private void QueueSignatureChecks(IReadOnlyList<ProcessInfo> processes)
    {
        var pending = processes.Where(p => p.Signature.State == SignatureState.Pending)
            .OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.PrivateBytes)
            .Select(p => p.Path).ToList();
        if (pending.Count == 0 || Interlocked.Exchange(ref _signatureWorkerRunning, 1) == 1)
        {
            return;
        }

        var token = _disposed.Token;
        _ = Task.Run(() =>
        {
            try
            {
                _signatures.CheckPending(pending, SignatureChecksPerBatch, token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Signature checks failed.");
            }
            finally
            {
                Volatile.Write(ref _signatureWorkerRunning, 0);
            }
        }, token);
    }

    /// <summary>Busiest GPU engine per process id, or null when the GPU Engine counters are not available.</summary>
    private IReadOnlyDictionary<int, (double Percent, string Engine)>? ReadGpu()
    {
        if (_gpuUnavailable)
        {
            return null;
        }

        try
        {
            _gpuCategory ??= new PerformanceCounterCategory("GPU Engine");
            var data = _gpuCategory.ReadCategory();
            var utilization = data["Utilization Percentage"];
            var current = new Dictionary<string, CounterSample>(StringComparer.Ordinal);
            var values = new List<KeyValuePair<string, double>>();
            if (utilization is not null)
            {
                foreach (InstanceData instance in utilization.Values)
                {
                    current[instance.InstanceName] = instance.Sample;
                    if (_gpuPrevious.TryGetValue(instance.InstanceName, out var previous))
                    {
                        values.Add(new(instance.InstanceName, CounterSample.Calculate(previous, instance.Sample)));
                    }
                }
            }

            _gpuPrevious = current;
            _gpuFailures = 0;
            return ProcessMath.BusiestGpuEngineByPid(values);
        }
        catch (Exception ex)
        {
            // Missing category (old driver, counters disabled) or a transient read failure; give up after a few in a row.
            if (ex is InvalidOperationException || ++_gpuFailures >= MaxGpuFailures)
            {
                _gpuUnavailable = true;
                _logger.LogInformation(ex, "GPU engine counters are not available; the GPU column stays empty.");
            }

            return null;
        }
    }

    private double? ReadSystemCpu()
    {
        if (!ProcessNative.TryGetSystemTimes(out long idle, out long kernel, out long user))
        {
            return null;
        }

        var previous = _systemTimes;
        _systemTimes = (idle, kernel, user);
        return previous is { } p ? ProcessMath.SystemCpuPercent(idle - p.Idle, kernel - p.Kernel, user - p.User) : null;
    }

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
        _sampleGate.Dispose();
    }

    private sealed record Identity(string? Path, string? User, ProcessNative.StaticDetails Static);
}
