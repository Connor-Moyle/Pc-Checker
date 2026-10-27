using PcChecker.Core.LiveUsage;
using PcChecker.Core.Sensors;
using Xunit;

namespace PcChecker.Core.Tests;

public class LiveUsageTests
{
    [Fact]
    public void Sample_ring_keeps_the_newest_readings_and_pads_the_front_for_the_graph()
    {
        var ring = new SampleRing(3);
        Assert.Equal(0, ring.Latest);
        ring.Add(1);
        ring.Add(double.NaN);
        Assert.Equal([double.NaN, 1, 0], ring.ToPaddedArray());

        ring.Add(5);
        ring.Add(7);
        Assert.Equal(3, ring.Count);
        Assert.Equal(0, ring[0]);
        Assert.Equal(7, ring.Latest);
        Assert.Equal(7, ring.Max());
        Assert.Equal([0, 5, 7], ring.ToPaddedArray());

        ring.Clear();
        Assert.Equal(0, ring.Count);
        Assert.All(ring.ToPaddedArray(), v => Assert.True(double.IsNaN(v)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SampleRing(0));
    }

    [Fact]
    public void Units_are_formatted_like_task_manager()
    {
        Assert.Equal("0 %", LiveUsageFormat.Percent(double.NaN));
        Assert.Equal("4.5 %", LiveUsageFormat.Percent(4.49));
        Assert.Equal("100 %", LiveUsageFormat.Percent(130));
        Assert.Equal("800 MHz", LiveUsageFormat.Speed(800));
        Assert.Equal("4.62 GHz", LiveUsageFormat.Speed(4620));
        Assert.Equal("—", LiveUsageFormat.Speed(null));
        Assert.Equal("0 KB/s", LiveUsageFormat.BytesPerSecond(0));
        Assert.Equal("512 KB/s", LiveUsageFormat.BytesPerSecond(512 * 1024));
        Assert.Equal("12.4 MB/s", LiveUsageFormat.BytesPerSecond(12.4 * 1024 * 1024));
        Assert.Equal("1.25 GB/s", LiveUsageFormat.BytesPerSecond(1.25 * 1024 * 1024 * 1024));
        Assert.Equal("0 Kbps", LiveUsageFormat.BitsPerSecond(-5));
        Assert.Equal("8.0 Kbps", LiveUsageFormat.BitsPerSecond(8000));
        Assert.Equal("120 Mbps", LiveUsageFormat.BitsPerSecond(120_000_000));
        Assert.Equal("2.5 Gbps", LiveUsageFormat.BitsPerSecond(2_500_000_000));
        Assert.Equal("2:04:13:09", LiveUsageFormat.UpTime(new TimeSpan(2, 4, 13, 9)));
        Assert.Equal("0.3 ms", LiveUsageFormat.Milliseconds(0.3));
        Assert.Equal("37 ms", LiveUsageFormat.Milliseconds(37.2));
        Assert.Equal("12.0/24.0 GB", LiveUsageFormat.UsedOfTotal(12L << 30, 24L << 30));
        Assert.Equal("0.5/1.00 GB", LiveUsageFormat.UsedOfTotal(512L << 20, 1L << 30));
        Assert.Equal("300 B", LiveUsageFormat.UsedOfTotal(300, 0));
    }

    [Fact]
    public void Disk_and_adapter_titles_read_like_task_manager()
    {
        var disk = new DiskFacts(0, "Samsung SSD 990 PRO 2TB", ["C:", "D:"], DiskMediaKind.Nvme, 2_000_000_000_000, null, true, true, "0 C: D:");
        Assert.Equal("Disk 0 (C: D:)", LiveUsageFormat.DiskTitle(disk));
        Assert.Equal("Disk 3", LiveUsageFormat.DiskTitle(disk with { Index = 3, DriveLetters = [] }));
        Assert.Equal("SSD (NVMe)", LiveUsageFormat.MediaLabel(DiskMediaKind.Nvme));
        Assert.Equal("HDD", LiveUsageFormat.MediaLabel(DiskMediaKind.Hdd));

        var wifi = new NetworkFacts("id", "", "Intel Wi-Fi 6E AX211", NetworkKind.WiFi, "802.11ax", [], [], "Home", 80, null);
        Assert.Equal("Wi-Fi", LiveUsageFormat.NetworkLabel(wifi));
        Assert.Equal("Ethernet 2", LiveUsageFormat.NetworkLabel(wifi with { ConnectionName = "Ethernet 2", Kind = NetworkKind.Ethernet }));
    }

    [Theory]
    [InlineData(135.0, 3400.0, 4590.0)]
    [InlineData(100.0, 2900.0, 2900.0)]
    [InlineData(900.0, 1000.0, 4000.0)]
    public void Current_speed_is_performance_times_base_speed(double performance, double baseMhz, double expected)
    {
        Assert.Equal(expected, LiveUsageMath.CurrentSpeedMhz(performance, baseMhz)!.Value, 3);
    }

    [Fact]
    public void Current_speed_is_unknown_without_both_parts()
    {
        Assert.Null(LiveUsageMath.CurrentSpeedMhz(null, 3400));
        Assert.Null(LiveUsageMath.CurrentSpeedMhz(120, 0));
        Assert.Null(LiveUsageMath.CurrentSpeedMhz(double.NaN, 3400));
    }

    [Theory]
    [InlineData(97.0, 3.0)]
    [InlineData(120.0, 0.0)]
    [InlineData(-4.0, 100.0)]
    [InlineData(double.NaN, 0.0)]
    public void Disk_active_time_is_clamped(double idle, double expected)
    {
        Assert.Equal(expected, LiveUsageMath.DiskActivePercent(idle), 6);
    }

    [Fact]
    public void Byte_counter_rates_ignore_resets_and_empty_intervals()
    {
        Assert.Equal(500, LiveUsageMath.RatePerSecond(1000, 2000, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, LiveUsageMath.RatePerSecond(5000, 100, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, LiveUsageMath.RatePerSecond(0, 100, TimeSpan.Zero));
        Assert.Equal(2.5, LiveUsageMath.ResponseMs(0.0025), 6);
        Assert.Equal(0, LiveUsageMath.ResponseMs(-1));
    }

    [Fact]
    public void Memory_composition_adds_up_and_never_overflows()
    {
        long gb = 1L << 30;
        long inUse = LiveUsageMath.InUseBytes(32 * gb, 1 * gb, 11 * gb, 8 * gb);
        Assert.Equal(12 * gb, inUse);
        var parts = LiveUsageMath.Compose(32 * gb, inUse, 1 * gb, 11 * gb, 8 * gb);
        Assert.Equal(12.0 / 32, parts.InUse, 6);
        Assert.Equal(1.0, parts.InUse + parts.Modified + parts.Standby + parts.Free, 6);

        // Counters read a moment apart can add up to more than the total: scale down instead of overflowing.
        var over = LiveUsageMath.Compose(10, 6, 2, 4, 2);
        Assert.Equal(1.0, over.InUse + over.Modified + over.Standby + over.Free, 6);
        Assert.Equal(6.0 / 14, over.InUse, 6);

        Assert.Equal(new MemoryComposition(0, 0, 0, 1), LiveUsageMath.Compose(0, 0, 0, 0, 0));
        Assert.Equal(0, LiveUsageMath.InUseBytes(10, 6, 6, 6));
    }

    [Fact]
    public void Physical_disk_instances_give_the_number_and_drive_letters()
    {
        Assert.True(CounterInstanceParser.TryParsePhysicalDisk("0 C: D:", out int index, out var letters));
        Assert.Equal(0, index);
        Assert.Equal(["C:", "D:"], letters);

        Assert.True(CounterInstanceParser.TryParsePhysicalDisk("2", out index, out letters));
        Assert.Equal(2, index);
        Assert.Empty(letters);

        Assert.True(CounterInstanceParser.TryParsePhysicalDisk("1 e:", out _, out letters));
        Assert.Equal(["E:"], letters);
        Assert.False(CounterInstanceParser.TryParsePhysicalDisk("_Total", out _, out _));
        Assert.False(CounterInstanceParser.TryParsePhysicalDisk(null, out _, out _));
    }

    [Fact]
    public void Processor_instances_are_ordered_by_group_and_number_without_totals()
    {
        Assert.True(CounterInstanceParser.TryParseProcessor("1,3", out int group, out int number));
        Assert.Equal((1, 3), (group, number));
        Assert.True(CounterInstanceParser.TryParseProcessor("7", out group, out number));
        Assert.Equal((0, 7), (group, number));
        Assert.False(CounterInstanceParser.TryParseProcessor("0,_Total", out _, out _));
        Assert.False(CounterInstanceParser.TryParseProcessor("_Total", out _, out _));

        var ordered = CounterInstanceParser.OrderProcessors(
        [
            new("0,10", 10), new("_Total", 99), new("0,2", 2), new("1,0", 64), new("0,_Total", 50), new("0,0", 0),
        ]);
        Assert.Equal([0, 2, 10, 64], ordered);
    }

    [Fact]
    public void Gpu_engine_instances_are_parsed_including_types_with_underscores()
    {
        Assert.True(CounterInstanceParser.TryParseGpuEngine("pid_1234_luid_0x00000000_0x0000d1c5_phys_0_eng_3_engtype_3D", out var engine));
        Assert.Equal(1234, engine.ProcessId);
        Assert.Equal("0x00000000_0x0000D1C5", engine.Luid);
        Assert.Equal(0, engine.Physical);
        Assert.Equal(3, engine.Engine);
        Assert.Equal("3D", engine.EngineType);

        Assert.True(CounterInstanceParser.TryParseGpuEngine("pid_88_luid_0x00000000_0x0000D1C5_phys_0_eng_9_engtype_Compute_0", out engine));
        Assert.Equal("Compute_0", engine.EngineType);

        Assert.False(CounterInstanceParser.TryParseGpuEngine("pid_1_luid_0x0_0x1_phys_0", out _));
        Assert.False(CounterInstanceParser.TryParseGpuEngine("not a counter", out _));
        Assert.False(CounterInstanceParser.TryParseGpuEngine("", out _));
    }

    [Fact]
    public void Gpu_adapter_instances_and_luids_compare_regardless_of_case_and_padding()
    {
        Assert.True(CounterInstanceParser.TryParseGpuAdapter("luid_0x00000000_0x0000d1c5_phys_0", out string luid));
        Assert.Equal("0x00000000_0x0000D1C5", luid);
        Assert.False(CounterInstanceParser.TryParseGpuAdapter("phys_0", out _));
        Assert.Equal(luid, CounterInstanceParser.FormatLuid(0, 0xD1C5));
        Assert.Equal("0xFFFFFFFF_0x00000001", CounterInstanceParser.FormatLuid(-1, 1));
        Assert.Equal(luid, CounterInstanceParser.NormalizeLuid("0x0_0xd1c5"));
    }

    [Fact]
    public void Gpu_engines_add_processes_and_show_the_busiest_engine_of_each_type()
    {
        var values = new List<KeyValuePair<string, double>>
        {
            new("pid_100_luid_0x00000000_0x0000D1C5_phys_0_eng_0_engtype_3D", 30),
            new("pid_200_luid_0x00000000_0x0000D1C5_phys_0_eng_0_engtype_3D", 25),
            new("pid_100_luid_0x00000000_0x0000D1C5_phys_0_eng_1_engtype_Copy", 4),
            new("pid_300_luid_0x00000000_0x0000D1C5_phys_0_eng_2_engtype_Copy", 9),
            new("pid_300_luid_0x00000000_0x0000D1C5_phys_0_eng_5_engtype_VideoDecode", 80),
            new("pid_301_luid_0x00000000_0x0000D1C5_phys_0_eng_5_engtype_VideoDecode", 60),
            new("pid_400_luid_0x00000000_0x00011111_phys_0_eng_0_engtype_3D", 7),
            new("garbage", 50),
        };

        var result = LiveUsageMath.AggregateGpuEngines(values);
        var main = result["0x00000000_0x0000D1C5"];
        Assert.Equal(55, main["3D"]);
        Assert.Equal(9, main["Copy"]);
        Assert.Equal(100, main["VideoDecode"]);
        Assert.Equal(100, LiveUsageMath.GpuUtilization(main));
        Assert.Equal(7, result["0x00000000_0x00011111"]["3D"]);
        Assert.Equal(0, LiveUsageMath.GpuUtilization(null));
    }

    [Fact]
    public void Graphed_engines_prefer_task_manager_order_then_the_busiest()
    {
        var chosen = LiveUsageMath.ChooseEngines(["Compute_0", "VideoDecode", "3D", "Security", "Copy"],
            new Dictionary<string, double> { ["Security"] = 5, ["Compute_0"] = 1 });
        Assert.Equal(["3D", "Copy", "VideoDecode", "Security"], chosen);
        Assert.Equal(["Cuda"], LiveUsageMath.ChooseEngines(["Cuda"]));
        Assert.Equal("Video Decode", CounterInstanceParser.EngineDisplayName("VideoDecode"));
        Assert.Equal("Compute 0", CounterInstanceParser.EngineDisplayName("Compute_0"));
        Assert.Equal("3D", CounterInstanceParser.EngineDisplayName("3D"));
    }

    [Theory]
    [InlineData(0.0, 100_000.0, 100_000.0)]
    [InlineData(130_000.0, 100_000.0, 200_000.0)]
    [InlineData(4_100_000.0, 100_000.0, 5_000_000.0)]
    [InlineData(7_000_000.0, 100_000.0, 10_000_000.0)]
    public void Throughput_graphs_scale_in_readable_steps(double max, double floor, double expected)
    {
        Assert.Equal(expected, LiveUsageMath.NiceScale(max, floor), 3);
    }

    [Fact]
    public void Gpu_temperature_comes_from_the_matching_sensor()
    {
        SensorReading Temp(string hardware, string name, double value) =>
            new($"{hardware}/{name}", hardware, HardwareKind.Gpu, SensorKind.Temperature, name, value, null, null, "°C");

        var readings = new List<SensorReading>
        {
            Temp("NVIDIA GeForce RTX 4070", "GPU Hot Spot", 61),
            Temp("NVIDIA GeForce RTX 4070", "GPU Core", 48),
            Temp("Intel UHD Graphics 770", "GPU Core", 40),
            new("cpu", "Intel Core i7", HardwareKind.Cpu, SensorKind.Temperature, "Package", 55, null, null, "°C"),
        };

        Assert.Equal(48, LiveUsageMath.PickGpuTemperature("NVIDIA GeForce RTX 4070", readings));
        Assert.Equal(40, LiveUsageMath.PickGpuTemperature("Intel(R) UHD Graphics 770", readings));
        Assert.Null(LiveUsageMath.PickGpuTemperature("AMD Radeon RX 7800 XT", readings));
        Assert.Null(LiveUsageMath.PickGpuTemperature("Anything", null));

        // A single card whose sensor name differs from the Windows name still gets its temperature.
        var single = new List<SensorReading> { Temp("GeForce RTX 3060 Laptop", "GPU Core", 52) };
        Assert.Equal(52, LiveUsageMath.PickGpuTemperature("NVIDIA RTX 3060 Mobile", single, onlyGpu: true));
    }

    [Theory]
    [InlineData(4, 17, "Samsung", DiskMediaKind.Nvme)]
    [InlineData(4, 11, "Crucial MX500", DiskMediaKind.Ssd)]
    [InlineData(3, 11, "WDC WD40EZAZ", DiskMediaKind.Hdd)]
    [InlineData(0, 7, "SanDisk Ultra", DiskMediaKind.Removable)]
    [InlineData(0, 11, "KINGSTON SA400 SSD", DiskMediaKind.Ssd)]
    [InlineData(null, null, "Some disk", DiskMediaKind.Unknown)]
    public void Disk_types_are_classified(int? mediaType, int? busType, string model, DiskMediaKind expected)
    {
        Assert.Equal(expected, LiveUsageMath.ClassifyDisk(mediaType, busType, model));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    public void Processor_topology_counts_sockets_cores_threads_and_caches(int pointerSize)
    {
        var entries = new List<byte[]>
        {
            CpuTopologyParser.Entry(pointerSize, 0xFF, 3),
            CpuTopologyParser.Entry(pointerSize, 0b11, 0),
            CpuTopologyParser.Entry(pointerSize, 0b1100, 0),
            CpuTopologyParser.Entry(pointerSize, 0b10000, 0),
            CpuTopologyParser.Entry(pointerSize, 0b11, 2, 1, 48 * 1024, 2),
            CpuTopologyParser.Entry(pointerSize, 0b11, 2, 1, 32 * 1024, 1),
            CpuTopologyParser.Entry(pointerSize, 0b11, 2, 2, 2 * 1024 * 1024, 0),
            CpuTopologyParser.Entry(pointerSize, 0xFF, 2, 3, 30 * 1024 * 1024, 0),
            CpuTopologyParser.Entry(pointerSize, 0xFF, 1),
        };
        var buffer = entries.SelectMany(e => e).ToArray();

        var topology = CpuTopologyParser.Parse(buffer, pointerSize);
        Assert.Equal(1, topology.Sockets);
        Assert.Equal(3, topology.Cores);
        Assert.Equal(5, topology.LogicalProcessors);
        Assert.Equal(80 * 1024, topology.L1CacheBytes);
        Assert.Equal(2 * 1024 * 1024, topology.L2CacheBytes);
        Assert.Equal(30 * 1024 * 1024, topology.L3CacheBytes);

        // A truncated last entry is ignored rather than read past the end.
        Assert.Equal(topology, CpuTopologyParser.Parse(buffer.Concat(new byte[5]).ToArray(), pointerSize));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuTopologyParser.Parse(buffer, 2));
    }

    [Fact]
    public void Copied_details_line_up_and_skip_empty_values()
    {
        string text = LiveUsageText.BuildCopyText("CPU", "Intel Core i7-13700K",
        [
            new("Utilization", "12 %"),
            new("Base speed", "3.40 GHz"),
            new("L1 cache", ""),
        ]);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Assert.Equal("CPU", lines[0]);
        Assert.Equal("Intel Core i7-13700K", lines[1]);
        Assert.Equal(string.Empty, lines[2]);
        Assert.Equal(lines[3].IndexOf("12 %", StringComparison.Ordinal), lines[4].IndexOf("3.40 GHz", StringComparison.Ordinal));
        Assert.DoesNotContain("L1 cache", text, StringComparison.Ordinal);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public void Compact_window_stays_on_screen()
    {
        var area = (0d, 0d, 1920d, 1040d);
        Assert.Equal((100d, 200d, 300d), CompactWindowPlacement.Fit(100, 200, 300, 400, area));

        // First run: top-right corner with the default width.
        var (left, top, width) = CompactWindowPlacement.Fit(null, null, null, 400, area);
        Assert.Equal(CompactWindowPlacement.DefaultWidth, width);
        Assert.Equal(1920 - width - 16, left);
        Assert.Equal(16, top);

        // Remembered on a monitor that is gone: back to the corner.
        Assert.Equal((1920 - 300 - 16d, 16d, 300d), CompactWindowPlacement.Fit(3000, 100, 300, 400, area));
        Assert.Equal(CompactWindowPlacement.MaxWidth, CompactWindowPlacement.Fit(10, 10, 5000, 400, area).Width);
    }

    [Fact]
    public void Compact_window_keeps_a_position_on_a_second_monitor()
    {
        var primary = (0d, 0d, 1920d, 1040d);

        // Virtual screen with a monitor on the left (negative X) and one on the right of the primary.
        var virtualScreen = new[] { (-1920d, 0d, 5760d, 1080d) };
        Assert.Equal((-1500d, 100d, 300d), CompactWindowPlacement.Fit(-1500, 100, 300, 400, primary, virtualScreen));
        Assert.Equal((2500d, 300d, 300d), CompactWindowPlacement.Fit(2500, 300, 300, 400, primary, virtualScreen));

        // The second monitor has been unplugged: the virtual screen is the primary alone, so back to its corner.
        var primaryOnly = new[] { (0d, 0d, 1920d, 1080d) };
        Assert.Equal((1920 - 300 - 16d, 16d, 300d), CompactWindowPlacement.Fit(2500, 300, 300, 400, primary, primaryOnly));

        // Above the top of the screens is off screen too.
        Assert.Equal((1920 - 300 - 16d, 16d, 300d), CompactWindowPlacement.Fit(100, -300, 300, 400, primary, virtualScreen));
    }

    [Fact]
    public void Preferences_survive_a_restart_and_a_broken_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pcchecker-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LiveUsagePreferencesStore(dir);
            Assert.Equal(CpuGraphMode.Overall, store.Load().CpuGraph);
            Assert.True(store.Save(new LiveUsagePreferences { CpuGraph = CpuGraphMode.PerLogicalProcessor, ShowKernelTimes = true, SelectedResourceId = "disk:1", CompactLeft = 12, CompactTop = 34 }));

            var loaded = store.Load();
            Assert.Equal(CpuGraphMode.PerLogicalProcessor, loaded.CpuGraph);
            Assert.True(loaded.ShowKernelTimes);
            Assert.Equal("disk:1", loaded.SelectedResourceId);
            Assert.Equal(12, loaded.CompactLeft);

            File.WriteAllText(Path.Combine(dir, LiveUsagePreferencesStore.FileName), "{ not json");
            Assert.Equal(CpuGraphMode.Overall, store.Load().CpuGraph);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Demo_data_fills_every_graph_and_matches_its_inventory()
    {
        var demo = new DemoLiveUsageService();
        var inventory = await demo.GetInventoryAsync(CancellationToken.None);
        Assert.Equal(2, inventory.Disks.Count);
        Assert.Contains(inventory.Disks, d => d.Media == DiskMediaKind.Hdd);
        Assert.Contains(inventory.Disks, d => d.Media == DiskMediaKind.Nvme && d.IsSystemDisk);
        Assert.Contains(inventory.Networks, n => n.Kind == NetworkKind.WiFi && n.Ssid is not null);
        Assert.True(inventory.Cpu.LogicalProcessors > inventory.Cpu.Cores);

        var history = demo.Backfill(59);
        Assert.Equal(59, history.Count);
        Assert.True(history.Zip(history.Skip(1)).All(p => p.First.Timestamp < p.Second.Timestamp));
        var sample = await demo.SampleAsync(CancellationToken.None);
        Assert.Equal(inventory.Cpu.LogicalProcessors, sample.Cpu.LogicalPercents.Count);
        Assert.All(inventory.Disks, d => Assert.Contains(sample.Disks, s => s.Index == d.Index));
        Assert.All(inventory.Networks, n => Assert.Contains(sample.Networks, s => s.Id == n.Id));
        Assert.All(inventory.Gpus, g => Assert.Contains(sample.Gpus, s => s.Luid == g.Luid));
        Assert.All(history.Select(h => h.Cpu.UtilizationPercent), v => Assert.InRange(v, 0, 100));
        Assert.InRange(sample.Memory.UsedPercent, 1, 99);
        Assert.Contains(history, h => h.Cpu.UtilizationPercent > 1);

        // The same seed gives the same first minute, so screenshots match from run to run.
        var again = new DemoLiveUsageService().Backfill(59);
        Assert.Equal(history.Select(h => h.Cpu.UtilizationPercent), again.Select(h => h.Cpu.UtilizationPercent));
    }
}
