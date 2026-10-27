using PcChecker.Core.Common;

namespace PcChecker.Core.LiveUsage;

/// <summary>
/// Sample data for the Live usage page: the demo gaming desktop (Core i7-13700K, 32 GB DDR5-6000, Samsung 990 PRO and a
/// WD 4 TB HDD, wired Ethernet plus Wi-Fi, GeForce RTX 4070) with readings that move believably. Every series comes from
/// a fixed seed, so screenshots look the same on every run.
/// </summary>
public sealed class DemoLiveUsageService : ILiveUsageService
{
    private const int LogicalProcessors = 24;
    private const double BaseSpeedMhz = 3400;
    private const string GpuLuid = "0x00000000_0x0000D1C5";
    private const string EthernetId = "{6F1C2A1E-4E2B-4D8B-9A3C-2B9D7C1E0A11}";
    private const string WifiId = "{A3D1E5F2-7C44-4B1A-8E2F-5D6C9B0A3E22}";
    private static readonly long UsableMemory = DemoData.TotalMemoryBytes - 410L * ByteSize.Megabyte;
    private static readonly long CommitLimit = UsableMemory + 5L * ByteSize.Gigabyte;

    private readonly object _gate = new();
    private readonly Random _random = DemoData.CreateRandom(4072);
    private readonly DateTimeOffset _bootTime = DateTimeOffset.Now.AddDays(-2).AddHours(-4).AddMinutes(-13);
    private readonly double[] _coreBias = new double[LogicalProcessors];
    private int _tick;
    private double _cpu = 9, _perf = 135, _inUse = 12.8, _disk0 = 3, _disk1 = 1, _receive = 2.5, _send = 0.4, _wifi = 0.6, _gpu3d = 14, _decode = 6;
    private int _spike;

    public DemoLiveUsageService()
    {
        var bias = DemoData.CreateRandom(4073);
        for (int i = 0; i < _coreBias.Length; i++)
        {
            // Performance cores (the first 16 logical processors) carry more of the load than the efficient cores.
            _coreBias[i] = (i < 16 ? 1.25 : 0.55) * (0.7 + bias.NextDouble() * 0.6);
        }
    }

    public async Task<LiveUsageInventory> GetInventoryAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        var cpu = new CpuFacts(DemoData.CpuName, BaseSpeedMhz, 1, 16, LogicalProcessors, true, 1408L * ByteSize.Kilobyte, 24L * ByteSize.Megabyte, 30L * ByteSize.Megabyte);
        var memory = new MemoryFacts(UsableMemory, DemoData.TotalMemoryBytes, 6000, 2, 4, "DIMM", "DDR5");
        var disks = new List<DiskFacts>
        {
            new(0, DemoData.NvmeModel, ["C:"], DiskMediaKind.Nvme, DemoData.NvmeBytes, 1_999_563_501_568, true, true, "0 C:"),
            new(1, DemoData.HddModel, ["D:"], DiskMediaKind.Hdd, DemoData.HddBytes, 4_000_650_883_072, false, false, "1 D:"),
        };
        var networks = new List<NetworkFacts>
        {
            new(EthernetId, "Ethernet", "Intel(R) Ethernet Controller I226-V", NetworkKind.Ethernet, "Ethernet",
                ["192.168.1.42"], ["fe80::5c1a:3b2f:9e41:7d20%12"], null, null, 2_500_000_000),
            new(WifiId, "Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz", NetworkKind.WiFi, "802.11ax",
                ["192.168.1.57"], ["fe80::a4c2:1d9e:6b03:42f1%18"], "HomeNetwork-5G", 78, 1_201_000_000),
        };
        var gpus = new List<GpuFacts>
        {
            new(0, DemoData.GpuName, GpuLuid, DemoData.GpuMemoryBytes, UsableMemory / 2, "32.0.15.6094", new DateTime(2024, 8, 14),
                "12 (FL 12.1)", "PCI bus 1, device 0, function 0"),
        };
        return new LiveUsageInventory(cpu, memory, disks, networks, gpus, []);
    }

    public async Task<LiveUsageSample> SampleAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return Next(DateTimeOffset.Now);
        }
    }

    public void ResetRates()
    {
    }

    public IReadOnlyList<LiveUsageSample> Backfill(int count)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var list = new List<LiveUsageSample>(Math.Max(0, count));
            for (int i = count; i > 0; i--)
            {
                list.Add(Next(now.AddSeconds(-i)));
            }

            return list;
        }
    }

    private LiveUsageSample Next(DateTimeOffset time)
    {
        _tick++;

        // Now and then something busy runs for a few seconds (a browser tab, a game shader compile).
        if (_spike == 0 && _random.NextDouble() < 0.04)
        {
            _spike = 3 + _random.Next(5);
        }

        double target = 8 + 4 * Math.Sin(_tick / 9.0) + (_spike > 0 ? 28 : 0);
        _spike = Math.Max(0, _spike - 1);
        _cpu = Walk(_cpu, target, 2.2, 1, 100);
        _perf = Walk(_perf, _cpu > 20 ? 158 : 132, 4, 100, 162);
        var logical = new double[LogicalProcessors];
        for (int i = 0; i < logical.Length; i++)
        {
            logical[i] = LiveUsageMath.ClampPercent(_cpu * _coreBias[i] + (_random.NextDouble() * 2 - 1) * 4);
        }

        var cpu = new CpuSample(Math.Round(_cpu, 1), Math.Round(_cpu * 0.32, 1), logical, LiveUsageMath.CurrentSpeedMhz(_perf, BaseSpeedMhz),
            287 + (int)(3 * Math.Sin(_tick / 13.0)), 3_948 + _random.Next(-25, 26), 131_420 + _random.Next(-300, 301), time - _bootTime);

        _inUse = Walk(_inUse, 12.8 + (_spike > 0 ? 0.6 : 0), 0.04, 11.5, 15);
        long inUse = (long)(_inUse * ByteSize.Gigabyte);
        long modified = (long)((205 + _random.Next(0, 20)) * (double)ByteSize.Megabyte);
        long standby = (long)(11.3 * ByteSize.Gigabyte) - _tick % 7 * ByteSize.Megabyte;
        long free = Math.Max(0, UsableMemory - inUse - modified - standby);
        var memory = new MemorySample(UsableMemory, inUse, standby + free, 420L * ByteSize.Megabyte + _random.Next(0, 8) * ByteSize.Megabyte,
            inUse + (long)(2.3 * ByteSize.Gigabyte), CommitLimit, standby + modified, 690L * ByteSize.Megabyte, 520L * ByteSize.Megabyte, modified, standby, free);

        _disk0 = Walk(_disk0, 2.5 + (_spike > 0 ? 18 : 0), 1.2, 0, 100);
        _disk1 = Walk(_disk1, _tick % 40 < 6 ? 9 : 0.4, 0.6, 0, 100);
        var disks = new List<DiskSample>
        {
            new(0, Math.Round(_disk0, 1), 0.2 + _random.NextDouble() * 0.4, _disk0 * 1.4 * ByteSize.Megabyte, (0.6 + _random.NextDouble()) * ByteSize.Megabyte * (1 + _disk0 / 10)),
            new(1, Math.Round(_disk1, 1), _disk1 > 1 ? 6 + _random.NextDouble() * 6 : 0, _disk1 * 0.9 * ByteSize.Megabyte, _disk1 * 0.3 * ByteSize.Megabyte),
        };

        _receive = Walk(_receive, 2.5 + 2 * Math.Sin(_tick / 6.0) + (_spike > 0 ? 9 : 0), 0.6, 0.05, 900);
        _send = Walk(_send, 0.35 + 0.2 * Math.Sin(_tick / 4.0), 0.08, 0.02, 400);
        _wifi = Walk(_wifi, 0.5 + 0.4 * Math.Cos(_tick / 8.0), 0.15, 0.01, 400);
        var networks = new List<NetworkSample>
        {
            new(EthernetId, _send * 1_000_000, _receive * 1_000_000),
            new(WifiId, _wifi * 120_000, _wifi * 1_000_000, "HomeNetwork-5G", 76 + _random.Next(0, 5)),
        };

        _gpu3d = Walk(_gpu3d, 14 + 6 * Math.Sin(_tick / 7.0) + (_spike > 0 ? 20 : 0), 2, 0, 100);
        _decode = Walk(_decode, 6 + 3 * Math.Sin(_tick / 5.0), 1, 0, 100);
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["3D"] = Math.Round(_gpu3d, 1),
            ["Copy"] = Math.Round(1.5 + _random.NextDouble() * 2.5, 1),
            ["VideoEncode"] = Math.Round(_random.NextDouble() * 0.8, 1),
            ["VideoDecode"] = Math.Round(_decode, 1),
            ["Compute_0"] = Math.Round(_random.NextDouble() * 1.5, 1),
        };
        var gpu = new GpuSample(GpuLuid, engines, LiveUsageMath.GpuUtilization(engines),
            (long)((2.1 + _gpu3d / 100) * ByteSize.Gigabyte), 210L * ByteSize.Megabyte + _random.Next(0, 10) * ByteSize.Megabyte,
            Math.Round(45 + _gpu3d / 6 + _random.NextDouble(), 0));

        return new LiveUsageSample(time, cpu, memory, disks, networks, [gpu], []);
    }

    /// <summary>Moves a value part of the way to its target with a little noise: smooth but alive.</summary>
    private double Walk(double value, double target, double noise, double min, double max) =>
        Math.Clamp(value + (target - value) * 0.25 + (_random.NextDouble() * 2 - 1) * noise, min, max);
}
