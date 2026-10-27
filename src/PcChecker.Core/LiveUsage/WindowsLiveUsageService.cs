using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Network;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.LiveUsage;

/// <summary>
/// Live usage on Windows. Rates come from performance counters added by their English names (so they work on every
/// display language): processor utility and performance, memory lists, PhysicalDisk, GPU Engine and GPU Adapter Memory.
/// Process, thread and handle counts and the commit charge come from <c>GetPerformanceInfo</c>, network throughput from
/// the adapters' byte counters, the Wi-Fi name and signal from <c>netsh wlan</c>, and the GPU temperature from the
/// sensor service when it is running. Static facts are in the Inventory part.
/// </summary>
public sealed partial class WindowsLiveUsageService : ILiveUsageService, IDisposable
{
    private static readonly TimeSpan CompressionInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WifiInterval = TimeSpan.FromSeconds(10);

    private readonly ISensorService _sensors;
    private readonly ILogger<WindowsLiveUsageService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (long Sent, long Received)> _lastBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _networkClock = new();
    private readonly Stopwatch _compressionClock = new();
    private readonly Stopwatch _wifiClock = new();

    private PdhQuery? _query;
    private PdhQuery? _slowQuery;
    private bool _countersFailed;
    private bool _needsBaseline = true;
    private Counters _c;
    private IntPtr _compressedCounter;
    private long? _compressedBytes;
    private LiveUsageInventory? _inventory;
    private List<NetworkInterface> _adapters = [];
    private IReadOnlyDictionary<string, WifiInterfaceInfo> _wifi = new Dictionary<string, WifiInterfaceInfo>();
    private Task? _wifiTask;

    public WindowsLiveUsageService(ISensorService sensors, ILogger<WindowsLiveUsageService> logger)
    {
        _sensors = sensors;
        _logger = logger;
    }

    public async Task<LiveUsageSample> SampleAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SampleCore(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ResetRates()
    {
        _needsBaseline = true;
        lock (_lastBytes)
        {
            _lastBytes.Clear();
        }
    }

    public IReadOnlyList<LiveUsageSample> Backfill(int count) => [];

    public void Dispose()
    {
        _query?.Dispose();
        _slowQuery?.Dispose();
        _query = _slowQuery = null;
    }

    private LiveUsageSample SampleCore(CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        EnsureCounters(warnings);
        if (_needsBaseline && _query is not null)
        {
            // Rates are measured between two readings; after a pause the last one is old, so take a fresh pair.
            _query.Collect();
            Thread.Sleep(250);
            _needsBaseline = false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _query?.Collect();
        var perf = ReadPerformanceInfo();
        var cpu = SampleCpu(perf);
        var memory = SampleMemory(perf);
        var disks = SampleDisks();
        var networks = SampleNetworks();
        var gpus = SampleGpus();
        if (_countersFailed)
        {
            warnings.Add("Windows performance counters are not available, so processor, disk and graphics use show 0. Running \"lodctr /R\" as administrator usually repairs them.");
        }

        return new LiveUsageSample(DateTimeOffset.Now, cpu, memory, disks, networks, gpus, warnings);
    }

    private void EnsureCounters(List<string> warnings)
    {
        if (_query is not null || _countersFailed)
        {
            return;
        }

        try
        {
            _query = PdhQuery.TryOpen();
            if (_query is null)
            {
                _countersFailed = true;
                return;
            }

            _c = new Counters
            {
                CpuTotal = _query.AddFirst(@"\Processor Information(_Total)\% Processor Utility", @"\Processor Information(_Total)\% Processor Time", @"\Processor(_Total)\% Processor Time"),
                CpuKernel = _query.AddFirst(@"\Processor Information(_Total)\% Privileged Utility", @"\Processor Information(_Total)\% Privileged Time", @"\Processor(_Total)\% Privileged Time"),
                CpuPerformance = _query.Add(@"\Processor Information(_Total)\% Processor Performance"),
                CpuEach = _query.AddFirst(@"\Processor Information(*)\% Processor Utility", @"\Processor Information(*)\% Processor Time", @"\Processor(*)\% Processor Time"),
                Modified = _query.Add(@"\Memory\Modified Page List Bytes"),
                StandbyReserve = _query.Add(@"\Memory\Standby Cache Reserve Bytes"),
                StandbyNormal = _query.Add(@"\Memory\Standby Cache Normal Priority Bytes"),
                StandbyCore = _query.Add(@"\Memory\Standby Cache Core Bytes"),
                Free = _query.Add(@"\Memory\Free & Zero Page List Bytes"),
                DiskIdle = _query.Add(@"\PhysicalDisk(*)\% Idle Time"),
                DiskResponse = _query.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer"),
                DiskRead = _query.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec"),
                DiskWrite = _query.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec"),
                GpuEngine = _query.Add(@"\GPU Engine(*)\Utilization Percentage"),
                GpuDedicated = _query.Add(@"\GPU Adapter Memory(*)\Dedicated Usage"),
                GpuShared = _query.Add(@"\GPU Adapter Memory(*)\Shared Usage"),
            };

            if (_c.CpuTotal == IntPtr.Zero && _c.DiskIdle == IntPtr.Zero)
            {
                _countersFailed = true;
            }

            _query.Collect();
            _needsBaseline = false;
            Thread.Sleep(250);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening the performance counters failed.");
            _countersFailed = true;
            warnings.Add("Windows performance counters could not be opened.");
        }
    }

    private CpuSample SampleCpu(PerformanceInfo? perf)
    {
        double total = 0, kernel = 0;
        double? performance = null;
        IReadOnlyList<double> logical = [];
        if (_query is not null)
        {
            total = LiveUsageMath.ClampPercent(_query.Read(_c.CpuTotal) ?? 0);
            kernel = Math.Min(total, LiveUsageMath.ClampPercent(_query.Read(_c.CpuKernel) ?? 0));
            performance = _query.Read(_c.CpuPerformance);
            logical = CounterInstanceParser.OrderProcessors(_query.ReadArray(_c.CpuEach)).Select(LiveUsageMath.ClampPercent).ToList();
        }

        double baseMhz = _inventory?.Cpu.BaseSpeedMhz ?? 0;
        return new CpuSample(total, kernel, logical, LiveUsageMath.CurrentSpeedMhz(performance, baseMhz),
            (int)(perf?.ProcessCount ?? 0), (int)(perf?.ThreadCount ?? 0), (int)(perf?.HandleCount ?? 0),
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private MemorySample SampleMemory(PerformanceInfo? perf)
    {
        long page = (long)(perf?.PageSize ?? 4096);
        long total = (long)(perf?.PhysicalTotal ?? 0) * page;
        long available = (long)(perf?.PhysicalAvailable ?? 0) * page;
        long committed = (long)(perf?.CommitTotal ?? 0) * page;
        long commitLimit = (long)(perf?.CommitLimit ?? 0) * page;
        long paged = (long)(perf?.KernelPaged ?? 0) * page;
        long nonPaged = (long)(perf?.KernelNonpaged ?? 0) * page;

        double? modified = _query?.Read(_c.Modified);
        double? free = _query?.Read(_c.Free);
        double standby = (_query?.Read(_c.StandbyReserve) ?? 0) + (_query?.Read(_c.StandbyNormal) ?? 0) + (_query?.Read(_c.StandbyCore) ?? 0);
        long inUse;
        long mod = 0, stand = 0, fr = 0;
        if (modified is { } m && free is { } f && total > 0)
        {
            mod = (long)m;
            stand = (long)standby;
            fr = (long)f;
            inUse = LiveUsageMath.InUseBytes(total, mod, stand, fr);
            available = stand + fr;
        }
        else
        {
            inUse = Math.Max(0, total - available);
            fr = available;
        }

        return new MemorySample(total, inUse, available, ReadCompressed(), committed, commitLimit, stand + mod, paged, nonPaged, mod, stand, fr);
    }

    /// <summary>The memory compression store's size, read every few seconds from a separate query (the Process object is costly).</summary>
    private long? ReadCompressed()
    {
        if (_compressionClock.IsRunning && _compressionClock.Elapsed < CompressionInterval)
        {
            return _compressedBytes;
        }

        _compressionClock.Restart();
        try
        {
            if (_slowQuery is null)
            {
                _slowQuery = PdhQuery.TryOpen();
                _compressedCounter = _slowQuery?.Add(@"\Process(Memory Compression)\Working Set") ?? IntPtr.Zero;
            }

            if (_slowQuery is not null && _compressedCounter != IntPtr.Zero && _slowQuery.Collect())
            {
                _compressedBytes = _slowQuery.Read(_compressedCounter) is { } bytes ? (long)bytes : null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the memory compression store failed.");
            _compressedBytes = null;
        }

        return _compressedBytes;
    }

    private List<DiskSample> SampleDisks()
    {
        var result = new List<DiskSample>();
        if (_query is null)
        {
            return result;
        }

        var response = ToDictionary(_query.ReadArray(_c.DiskResponse));
        var read = ToDictionary(_query.ReadArray(_c.DiskRead));
        var write = ToDictionary(_query.ReadArray(_c.DiskWrite));
        foreach (var (instance, idle) in _query.ReadArray(_c.DiskIdle))
        {
            if (!CounterInstanceParser.TryParsePhysicalDisk(instance, out int index, out _))
            {
                continue;
            }

            result.Add(new DiskSample(index, LiveUsageMath.DiskActivePercent(idle), LiveUsageMath.ResponseMs(response.GetValueOrDefault(instance)),
                Math.Max(0, read.GetValueOrDefault(instance)), Math.Max(0, write.GetValueOrDefault(instance))));
        }

        return result;
    }

    private List<NetworkSample> SampleNetworks()
    {
        RefreshWifiInBackground();
        var result = new List<NetworkSample>();
        double elapsed = _networkClock.IsRunning ? _networkClock.Elapsed.TotalSeconds : 0;
        _networkClock.Restart();
        foreach (var adapter in _adapters)
        {
            long sent, received;
            try
            {
                var stats = adapter.GetIPStatistics();
                sent = stats.BytesSent;
                received = stats.BytesReceived;
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                continue;
            }

            double send = 0, receive = 0;
            lock (_lastBytes)
            {
                if (_lastBytes.TryGetValue(adapter.Id, out var last) && elapsed > 0)
                {
                    var span = TimeSpan.FromSeconds(elapsed);
                    send = LiveUsageMath.RatePerSecond(last.Sent, sent, span) * 8;
                    receive = LiveUsageMath.RatePerSecond(last.Received, received, span) * 8;
                }

                _lastBytes[adapter.Id] = (sent, received);
            }

            _wifi.TryGetValue(adapter.Name, out var wifi);
            result.Add(new NetworkSample(adapter.Id, send, receive, wifi?.Ssid, wifi?.SignalPercent));
        }

        return result;
    }

    private void RefreshWifiInBackground()
    {
        if (!_adapters.Any(a => a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            || (_wifiClock.IsRunning && _wifiClock.Elapsed < WifiInterval)
            || _wifiTask is { IsCompleted: false })
        {
            return;
        }

        _wifiClock.Restart();
        _wifiTask = Task.Run(async () =>
        {
            try
            {
                _wifi = await ReadWifiAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reading the Wi-Fi connection failed.");
            }
        });
    }

    private static async Task<IReadOnlyDictionary<string, WifiInterfaceInfo>> ReadWifiAsync(CancellationToken cancellationToken)
    {
        var run = await Latency.LatencyProbes.RunAsync("netsh.exe", "wlan show interfaces", cancellationToken, 15).ConfigureAwait(false);
        var map = new Dictionary<string, WifiInterfaceInfo>(StringComparer.OrdinalIgnoreCase);
        if (run.ExitCode != 0)
        {
            return map;
        }

        foreach (var info in NetshWlanParser.ParseInterfaces(run.Output))
        {
            map[info.Name] = info;
        }

        return map;
    }

    private List<GpuSample> SampleGpus()
    {
        var result = new List<GpuSample>();
        if (_query is null)
        {
            return result;
        }

        var engines = LiveUsageMath.AggregateGpuEngines(_query.ReadArray(_c.GpuEngine));
        var dedicated = SumByAdapter(_query.ReadArray(_c.GpuDedicated));
        var shared = SumByAdapter(_query.ReadArray(_c.GpuShared));
        var gpus = _inventory?.Gpus ?? [];
        var readings = _sensors.Latest?.Readings;
        var luids = gpus.Select(g => g.Luid).Concat(engines.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string luid in luids)
        {
            var facts = gpus.FirstOrDefault(g => string.Equals(g.Luid, luid, StringComparison.OrdinalIgnoreCase));
            var types = engines.GetValueOrDefault(luid) ?? new Dictionary<string, double>();
            double? temperature = facts is null ? null : LiveUsageMath.PickGpuTemperature(facts.Name, readings, onlyGpu: gpus.Count == 1);
            result.Add(new GpuSample(luid, types, LiveUsageMath.GpuUtilization(types), (long)dedicated.GetValueOrDefault(luid), (long)shared.GetValueOrDefault(luid), temperature));
        }

        return result;
    }

    private static Dictionary<string, double> SumByAdapter(IEnumerable<KeyValuePair<string, double>> values)
    {
        var sums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in values)
        {
            if (CounterInstanceParser.TryParseGpuAdapter(instance, out string luid))
            {
                sums[luid] = sums.GetValueOrDefault(luid) + Math.Max(0, value);
            }
        }

        return sums;
    }

    private static Dictionary<string, double> ToDictionary(IEnumerable<KeyValuePair<string, double>> values)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            map[key] = value;
        }

        return map;
    }

    private PerformanceInfo? ReadPerformanceInfo()
    {
        try
        {
            var info = new PerformanceInfo { Size = (uint)Marshal.SizeOf<PerformanceInfo>() };
            return GetPerformanceInfo(ref info, info.Size) ? info : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "GetPerformanceInfo is not available.");
            return null;
        }
    }

    private struct Counters
    {
        public IntPtr CpuTotal, CpuKernel, CpuPerformance, CpuEach;
        public IntPtr Modified, StandbyReserve, StandbyNormal, StandbyCore, Free;
        public IntPtr DiskIdle, DiskResponse, DiskRead, DiskWrite;
        public IntPtr GpuEngine, GpuDedicated, GpuShared;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInfo
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInfo info, uint size);
}
