using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Gaming;

/// <summary>
/// Polls the foreground window every 500 ms (<c>GetForegroundWindow</c> + <c>GetWindowThreadProcessId</c>), resolves
/// the process name, exe path, title and whether the window covers its monitor, and raises
/// <see cref="ForegroundChanged"/> when that changes. Every Win32 call is guarded; a failed poll is skipped.
/// </summary>
public sealed class WindowsForegroundAppTracker : IForegroundAppTracker, IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CpuSampleTime = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<WindowsForegroundAppTracker> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, (string Name, string? Path)> _processCache = [];
    private readonly int _ownProcessId = Environment.ProcessId;
    private Timer? _timer;
    private int _references;
    private int _polling;
    private int _lastPid;
    private bool _lastFullscreen;

    public WindowsForegroundAppTracker(ILogger<WindowsForegroundAppTracker> logger)
    {
        _logger = logger;
    }

    public ForegroundApp? Current { get; private set; }

    public event EventHandler<ForegroundApp>? ForegroundChanged;

    public void Start()
    {
        if (!PlatformGuard.IsWindows)
        {
            return;
        }

        lock (_gate)
        {
            _references++;
            if (_timer is null)
            {
                _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
                _logger.LogDebug("Foreground tracking started.");
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _references = Math.Max(0, _references - 1);
            if (_references == 0 && _timer is not null)
            {
                _timer.Dispose();
                _timer = null;
                _logger.LogDebug("Foreground tracking stopped.");
            }
        }
    }

    public Task<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken cancellationToken) =>
        Task.Run(() => CollectRunningAppsAsync(cancellationToken), cancellationToken);

    public bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // Access denied to a protected process: it exists.
            return true;
        }
    }

    public IDisposable WatchProcessExit(int processId, Action onExited)
    {
        ArgumentNullException.ThrowIfNull(onExited);
        return new ExitWatch(processId, onExited, _logger);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _references = 0;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            IntPtr hwnd = GamingNativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            GamingNativeMethods.GetWindowThreadProcessId(hwnd, out uint rawPid);
            int pid = (int)rawPid;
            if (pid == 0)
            {
                return;
            }

            bool fullscreen = GamingNativeMethods.TryIsFullscreen(hwnd);
            if (pid == _lastPid && fullscreen == _lastFullscreen)
            {
                return;
            }

            _lastPid = pid;
            _lastFullscreen = fullscreen;
            var (name, path) = DescribeProcess(pid);
            if (GameProfileMatcher.IsIgnoredProcess(name, pid, _ownProcessId))
            {
                return;
            }

            var app = new ForegroundApp(pid, name, path, GamingNativeMethods.TryGetWindowTitle(hwnd), fullscreen);
            Current = app;
            _logger.LogDebug("Foreground app: {Process} (pid {Pid}, full screen {Fullscreen}).", name, pid, fullscreen);
            ForegroundChanged?.Invoke(this, app);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Foreground poll failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private (string Name, string? Path) DescribeProcess(int pid)
    {
        if (_processCache.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        string name = string.Empty;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch
        {
            // Exited or not accessible: fall back to the path below.
        }

        string? path = GamingNativeMethods.TryGetProcessPath(pid);
        if (name.Length == 0 && path is not null)
        {
            name = GameProfileMatcher.NormalizeExeName(path);
        }

        if (_processCache.Count > 256)
        {
            _processCache.Clear();
        }

        _processCache[pid] = (name, path);
        return (name, path);
    }

    private async Task<IReadOnlyList<RunningApp>> CollectRunningAppsAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<(Process Process, TimeSpan Cpu)>();
        var gpuCounters = new List<(int Pid, PerformanceCounter Counter)>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                if (!TryAddCandidate(process, candidates))
                {
                    process.Dispose();
                }
            }

            var pids = candidates.Select(c => c.Process.Id).ToHashSet();
            OpenGpuCounters(pids, gpuCounters);
            var stopwatch = Stopwatch.StartNew();
            await Task.Delay(CpuSampleTime, cancellationToken).ConfigureAwait(false);
            var elapsed = stopwatch.Elapsed;

            var gpuByPid = ReadGpuCounters(gpuCounters);
            var apps = new List<RunningApp>();
            foreach (var (process, cpuBefore) in candidates)
            {
                try
                {
                    process.Refresh();
                    double cpu = ProcessMetrics.CpuPercent(cpuBefore, SafeCpu(process), elapsed, Environment.ProcessorCount);
                    double? gpu = gpuByPid.TryGetValue(process.Id, out var g) ? Math.Clamp(g, 0, 100) : (gpuCounters.Count > 0 ? 0 : null);
                    apps.Add(new RunningApp(process.Id, process.ProcessName, GamingNativeMethods.TryGetProcessPath(process.Id),
                        process.MainWindowTitle, Math.Round(cpu, 1), gpu is null ? null : Math.Round(gpu.Value, 1), SafeWorkingSet(process)));
                }
                catch
                {
                    // The process exited while we were measuring.
                }
            }

            return ProcessMetrics.Rank(apps);
        }
        finally
        {
            foreach (var (process, _) in candidates)
            {
                process.Dispose();
            }

            foreach (var (_, counter) in gpuCounters)
            {
                counter.Dispose();
            }
        }
    }

    private bool TryAddCandidate(Process process, List<(Process, TimeSpan)> candidates)
    {
        try
        {
            if (process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle)
                || GameProfileMatcher.IsIgnoredProcess(process.ProcessName, process.Id, _ownProcessId))
            {
                return false;
            }

            candidates.Add((process, SafeCpu(process)));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OpenGpuCounters(HashSet<int> pids, List<(int, PerformanceCounter)> counters)
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in category.GetInstanceNames())
            {
                if (counters.Count >= 256)
                {
                    break;
                }

                if (!ProcessMetrics.IsGpu3DEngine(instance) || !ProcessMetrics.TryParseGpuEnginePid(instance, out int pid) || !pids.Contains(pid))
                {
                    continue;
                }

                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                counter.NextValue();
                counters.Add((pid, counter));
            }
        }
        catch (Exception ex)
        {
            // Older drivers or disabled performance counters: the list is then sorted by CPU use only.
            _logger.LogDebug(ex, "GPU engine counters are not available.");
        }
    }

    private static Dictionary<int, double> ReadGpuCounters(List<(int Pid, PerformanceCounter Counter)> counters)
    {
        var result = new Dictionary<int, double>();
        foreach (var (pid, counter) in counters)
        {
            try
            {
                result[pid] = result.GetValueOrDefault(pid) + counter.NextValue();
            }
            catch
            {
                // Instance went away.
            }
        }

        return result;
    }

    private static TimeSpan SafeCpu(Process process)
    {
        try
        {
            return process.TotalProcessorTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static long SafeWorkingSet(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Process exit watch: the Exited event when Windows allows it, otherwise a 2-second poll.</summary>
    private sealed class ExitWatch : IDisposable
    {
        private readonly int _processId;
        private readonly Action _onExited;
        private Process? _process;
        private Timer? _poll;
        private int _fired;

        public ExitWatch(int processId, Action onExited, ILogger logger)
        {
            _processId = processId;
            _onExited = onExited;
            try
            {
                _process = Process.GetProcessById(processId);
                _process.EnableRaisingEvents = true;
                _process.Exited += OnExited;
                if (_process.HasExited)
                {
                    Fire();
                }
            }
            catch (ArgumentException)
            {
                // Already gone.
                Fire();
            }
            catch (Exception ex)
            {
                // Protected processes (some anti-cheat) refuse the wait handle; poll instead.
                logger.LogDebug(ex, "Exit event unavailable for pid {Pid}; polling instead.", processId);
                _process?.Dispose();
                _process = null;
                _poll = new Timer(_ => Poll(), null, ExitPollInterval, ExitPollInterval);
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _fired, 1);
            _poll?.Dispose();
            if (_process is not null)
            {
                try
                {
                    _process.Exited -= OnExited;
                    _process.Dispose();
                }
                catch
                {
                    // Nothing to release.
                }
            }
        }

        private void OnExited(object? sender, EventArgs e) => Fire();

        private void Poll()
        {
            try
            {
                using var process = Process.GetProcessById(_processId);
                if (!process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                // Not running any more.
            }
            catch
            {
                return;
            }

            Fire();
        }

        private void Fire()
        {
            if (Interlocked.Exchange(ref _fired, 1) == 1)
            {
                return;
            }

            _poll?.Dispose();
            try
            {
                _onExited();
            }
            catch
            {
                // The callback logs its own problems.
            }
        }
    }
}
