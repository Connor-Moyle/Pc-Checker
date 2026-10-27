using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Memory;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Processes;

/// <summary>
/// Windows implementation of <see cref="IProcessService"/>: a Toolhelp snapshot for the list, limited-rights process
/// handles for CPU time, I/O and the private working set, the "GPU Engine" performance counters for GPU use, and
/// cached Authenticode checks for publishers. Ending a process and changing its priority run through
/// <see cref="IChangeGuard"/> (journaled, no restore point: neither touches the system configuration).
/// </summary>
public sealed partial class WindowsProcessService : IProcessService, IDisposable
{
    private const string Module = "Processes";
    private const string NotOnWindows = "The process list is only available on Windows.";
    private const int SignatureChecksPerBatch = 40;
    private const int MaxGpuFailures = 3;

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsProcessService> _logger;
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly ProcessRateTracker _rates = new();
    private readonly SignatureCache _signatures = new(AuthenticodeVerifier.Verify);
    private readonly Dictionary<string, (string? Path, string? User)> _identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _accounts = new(StringComparer.OrdinalIgnoreCase);
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

        var now = DateTimeOffset.Now;
        var counters = new List<ProcessCounters>(entries.Count);
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
            var (path, user) = Identity(key, entry.Pid);
            counters.Add(new ProcessCounters(entry.Pid, entry.ParentPid, entry.Name, path, user, start,
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

        var processes = counters.Select(c =>
        {
            var r = rates.GetValueOrDefault(c.Key);
            double? gpuPercent = gpu is null ? null : gpu.GetValueOrDefault(c.Pid);
            return new ProcessInfo(c.Pid, c.ParentPid, c.Name, c.Path, c.User, c.StartTime, Math.Round(r.CpuPercent, 1), c.PrivateBytes,
                r.ReadBytesPerSecond, r.WriteBytesPerSecond, gpuPercent is null ? null : Math.Round(gpuPercent.Value, 1), c.Priority, _signatures.Get(c.Path));
        }).ToList();

        QueueSignatureChecks(processes);
        return new ProcessSnapshot(now, processes, Math.Round(totalCpu, 1), used, total, gpu is not null, warnings);
    }

    /// <summary>Path and account of one run of a program; read once per process and cached.</summary>
    private (string? Path, string? User) Identity(string key, int pid)
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

        var identity = (path, user);
        _identities[key] = identity;
        return identity;
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

    /// <summary>GPU use per process id, or null when the GPU Engine counters are not available.</summary>
    private IReadOnlyDictionary<int, double>? ReadGpu()
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
            return ProcessMath.SumGpuByPid(values);
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
}
