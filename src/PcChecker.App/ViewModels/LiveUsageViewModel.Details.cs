using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.LiveUsage;

namespace PcChecker.App.ViewModels;

/// <summary>The selected resource: its big graph (or one graph per processor or engine), extra graphs and details.</summary>
public sealed partial class LiveUsageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCpu))]
    [NotifyPropertyChangedFor(nameof(IsMemory))]
    [NotifyPropertyChangedFor(nameof(IsGpu))]
    [NotifyPropertyChangedFor(nameof(ShowMainGraph))]
    [NotifyPropertyChangedFor(nameof(ShowLogicalGraphs))]
    [NotifyCanExecuteChangedFor(nameof(CopyDetailsCommand))]
    private LiveResourceItem? _selectedResource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMainGraph))]
    [NotifyPropertyChangedFor(nameof(ShowLogicalGraphs))]
    private bool _perLogicalProcessor;

    [ObservableProperty]
    private bool _showKernelTimes;

    [ObservableProperty]
    private string _graphCaption = string.Empty;

    [ObservableProperty]
    private string _graphMaxText = "100 %";

    [ObservableProperty]
    private string? _graphLegend;

    [ObservableProperty]
    private string _graphDescription = string.Empty;

    [ObservableProperty]
    private double[]? _graphValues;

    [ObservableProperty]
    private double[]? _graphSecondValues;

    [ObservableProperty]
    private double[]? _graphShadeValues;

    [ObservableProperty]
    private double _graphMaximum = 100;

    [ObservableProperty]
    private int _logicalColumns = 4;

    [ObservableProperty]
    private GridLength _inUseShare = new(0, GridUnitType.Star);

    [ObservableProperty]
    private GridLength _modifiedShare = new(0, GridUnitType.Star);

    [ObservableProperty]
    private GridLength _standbyShare = new(0, GridUnitType.Star);

    [ObservableProperty]
    private GridLength _freeShare = new(1, GridUnitType.Star);

    [ObservableProperty]
    private string _compositionText = string.Empty;

    public bool IsCpu => SelectedResource?.Kind == LiveResourceKind.Cpu;

    public bool IsMemory => SelectedResource?.Kind == LiveResourceKind.Memory;

    public bool IsGpu => SelectedResource?.Kind == LiveResourceKind.Gpu;

    public bool ShowLogicalGraphs => IsCpu && PerLogicalProcessor;

    public bool ShowMainGraph => SelectedResource is not null && !ShowLogicalGraphs && !IsGpu;

    /// <summary>One graph per logical processor (CPU, when chosen).</summary>
    public ObservableCollection<LiveSmallGraph> LogicalGraphs { get; } = [];

    /// <summary>The four engine graphs of the selected GPU.</summary>
    public ObservableCollection<LiveSmallGraph> EngineGraphs { get; } = [];

    /// <summary>Disk transfer rate, or the GPU's dedicated and shared memory.</summary>
    public ObservableCollection<LiveSmallGraph> ExtraGraphs { get; } = [];

    /// <summary>The values that change every second, shown large.</summary>
    public ObservableCollection<LiveDetailRow> LiveRows { get; } = [];

    /// <summary>The fixed facts about the device.</summary>
    public ObservableCollection<LiveDetailRow> FactRows { get; } = [];

    partial void OnSelectedResourceChanged(LiveResourceItem? value)
    {
        if (value is not null && Preferences.SelectedResourceId != value.Id)
        {
            Preferences.SelectedResourceId = value.Id;
            SavePreferences();
        }

        LiveRows.Clear();
        FactRows.Clear();
        LogicalGraphs.Clear();
        EngineGraphs.Clear();
        ExtraGraphs.Clear();
        RefreshSelected();
    }

    partial void OnPerLogicalProcessorChanged(bool value)
    {
        Preferences.CpuGraph = value ? CpuGraphMode.PerLogicalProcessor : CpuGraphMode.Overall;
        SavePreferences();
        RefreshSelected();
    }

    partial void OnShowKernelTimesChanged(bool value)
    {
        Preferences.ShowKernelTimes = value;
        SavePreferences();
        RefreshSelected();
    }

    [RelayCommand]
    private void ShowOverallGraph() => PerLogicalProcessor = false;

    [RelayCommand]
    private void ShowLogicalProcessorGraphs() => PerLogicalProcessor = true;

    private bool CanCopy() => SelectedResource is not null;

    /// <summary>Copies the selected resource's details as text, like Ctrl+C in Task Manager.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void CopyDetails()
    {
        if (SelectedResource is not { } item)
        {
            return;
        }

        try
        {
            var rows = LiveRows.Concat(FactRows).Select(r => new KeyValuePair<string, string>(r.Label, r.Value));
            Clipboard.SetText(LiveUsageText.BuildCopyText(item.Title, item.DeviceName, rows));
            _notifications.Success(Loc.T("Copied"), Loc.T("The details are on the clipboard."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            _notifications.Warning(Loc.T("Could not copy"), ex.Message);
        }
    }

    /// <summary>Pushes the selected resource's history into the graphs and its current values into the details.</summary>
    private void RefreshSelected()
    {
        if (SelectedResource is not { } item || _last is not { } sample || _inventory is not { } inventory)
        {
            return;
        }

        GraphSecondValues = null;
        GraphShadeValues = null;
        GraphLegend = null;
        GraphMaximum = 100;
        GraphMaxText = "100 %";
        var rows = new List<(string Label, string Value, string? Hint, bool Live)>();
        switch (item.Kind)
        {
            case LiveResourceKind.Cpu:
                ShowCpu(item, sample.Cpu, inventory.Cpu, rows);
                break;
            case LiveResourceKind.Memory:
                ShowMemory(item, sample.Memory, inventory.Memory, rows);
                break;
            case LiveResourceKind.Disk:
                var disk = inventory.Disks.FirstOrDefault(d => "disk:" + d.Index.ToString(CultureInfo.InvariantCulture) == item.Id);
                ShowDisk(item, sample.Disks.FirstOrDefault(d => d.Index == disk?.Index), disk, rows);
                break;
            case LiveResourceKind.Network:
                var net = inventory.Networks.FirstOrDefault(n => "net:" + n.Id == item.Id);
                ShowNetwork(item, sample.Networks.FirstOrDefault(n => n.Id == net?.Id), net, rows);
                break;
            case LiveResourceKind.Gpu:
                var gpu = inventory.Gpus.FirstOrDefault(g => "gpu:" + g.Luid == item.Id);
                ShowGpu(item, sample.Gpus.FirstOrDefault(g => string.Equals(g.Luid, gpu?.Luid, StringComparison.OrdinalIgnoreCase)), gpu, rows);
                break;
        }

        GraphValues = item.Main.ToPaddedArray();
        GraphDescription = item.Title + ": " + item.ValueText;
        Fill(LiveRows, rows.Where(r => r.Live));
        Fill(FactRows, rows.Where(r => !r.Live));
    }

    private void ShowCpu(LiveResourceItem item, CpuSample cpu, CpuFacts facts, List<(string, string, string?, bool)> rows)
    {
        GraphCaption = Loc.T("% Utilization over 60 seconds");
        if (ShowKernelTimes)
        {
            GraphShadeValues = item.Second.ToPaddedArray();
            GraphLegend = Loc.T("The darker area is time spent in Windows itself (kernel time).");
        }

        if (PerLogicalProcessor)
        {
            LogicalColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(item.Logical.Count * 1.5)));
            SyncSmallGraphs(LogicalGraphs, item.Logical.Select((ring, i) => ($"cpu{i}", Loc.F("CPU {0}", i), ring, (SampleRing?)null, 100d, LiveUsageFormat.Percent(ring.Latest), "100 %")));
        }

        rows.Add((Loc.T("Utilization"), LiveUsageFormat.Percent(cpu.UtilizationPercent), Loc.T("How busy the processor is right now."), true));
        rows.Add((Loc.T("Speed"), LiveUsageFormat.Speed(cpu.SpeedMhz), Loc.T("The current clock speed; it rises above the base speed under load (turbo)."), true));
        rows.Add((Loc.T("Processes"), LiveUsageFormat.Count(cpu.Processes), Loc.T("Programs and background services running."), true));
        rows.Add((Loc.T("Threads"), LiveUsageFormat.Count(cpu.Threads), Loc.T("Separate lines of work inside those programs."), true));
        rows.Add((Loc.T("Handles"), LiveUsageFormat.Count(cpu.Handles), Loc.T("Files, settings and other items the programs have open."), true));
        rows.Add((Loc.T("Up time"), LiveUsageFormat.UpTime(cpu.UpTime), Loc.T("Days:hours:minutes:seconds since Windows started. Fast startup keeps counting across shutdowns."), true));
        rows.Add((Loc.T("Base speed"), LiveUsageFormat.Speed(facts.BaseSpeedMhz), null, false));
        rows.Add((Loc.T("Sockets"), facts.Sockets.ToString(CultureInfo.CurrentCulture), null, false));
        rows.Add((Loc.T("Cores"), facts.Cores.ToString(CultureInfo.CurrentCulture), null, false));
        rows.Add((Loc.T("Logical processors"), facts.LogicalProcessors.ToString(CultureInfo.CurrentCulture), Loc.T("Cores that run two threads at once count twice."), false));
        rows.Add((Loc.T("Virtualization"), facts.VirtualizationEnabled switch { true => Loc.T("Enabled"), false => Loc.T("Disabled"), _ => "—" },
            Loc.T("Needed for virtual machines, WSL and Windows Sandbox. Turned on in the BIOS."), false));
        rows.Add((Loc.T("L1 cache"), LiveUsageFormat.Bytes(facts.L1CacheBytes), null, false));
        rows.Add((Loc.T("L2 cache"), LiveUsageFormat.Bytes(facts.L2CacheBytes), null, false));
        rows.Add((Loc.T("L3 cache"), LiveUsageFormat.Bytes(facts.L3CacheBytes), null, false));
    }

    private void ShowMemory(LiveResourceItem item, MemorySample memory, MemoryFacts facts, List<(string, string, string?, bool)> rows)
    {
        GraphCaption = Loc.T("Memory usage over 60 seconds");
        GraphMaxText = LiveUsageFormat.Bytes(memory.TotalBytes);
        var parts = LiveUsageMath.Compose(memory.TotalBytes, memory.InUseBytes, memory.ModifiedBytes, memory.StandbyBytes, memory.FreeBytes);
        InUseShare = new GridLength(parts.InUse, GridUnitType.Star);
        ModifiedShare = new GridLength(parts.Modified, GridUnitType.Star);
        StandbyShare = new GridLength(parts.Standby, GridUnitType.Star);
        FreeShare = new GridLength(parts.Free, GridUnitType.Star);
        CompositionText = Loc.F("In use {0} · Modified {1} · Standby {2} · Free {3}",
            LiveUsageFormat.Bytes(memory.InUseBytes), LiveUsageFormat.Bytes(memory.ModifiedBytes), LiveUsageFormat.Bytes(memory.StandbyBytes), LiveUsageFormat.Bytes(memory.FreeBytes));

        string inUse = LiveUsageFormat.Bytes(memory.InUseBytes) + (memory.CompressedBytes is { } compressed ? " (" + LiveUsageFormat.Bytes(compressed) + ")" : string.Empty);
        rows.Add((Loc.T("In use (Compressed)"), inUse, Loc.T("Memory programs and Windows are using. In brackets: how much of it Windows has compressed to save space."), true));
        rows.Add((Loc.T("Available"), LiveUsageFormat.Bytes(memory.AvailableBytes), Loc.T("Free memory plus cached data that can be dropped at once."), true));
        rows.Add((Loc.T("Committed"), LiveUsageFormat.UsedOfTotal(memory.CommittedBytes, memory.CommitLimitBytes), Loc.T("Memory promised to programs, out of RAM plus the page file."), true));
        rows.Add((Loc.T("Cached"), LiveUsageFormat.Bytes(memory.CachedBytes), Loc.T("Recently used data kept in memory to start things faster."), true));
        rows.Add((Loc.T("Paged pool"), LiveUsageFormat.Bytes(memory.PagedPoolBytes), Loc.T("Windows and driver memory that can move to the page file."), true));
        rows.Add((Loc.T("Non-paged pool"), LiveUsageFormat.Bytes(memory.NonPagedPoolBytes), Loc.T("Windows and driver memory that must stay in RAM. A value that keeps growing points to a faulty driver."), true));
        rows.Add((Loc.T("Speed"), facts.SpeedMhz is { } mhz ? mhz.ToString(CultureInfo.CurrentCulture) + " MT/s" : "—", Loc.T("The speed the memory runs at now."), false));
        rows.Add((Loc.T("Slots used"), facts.SlotsUsed is { } used ? facts.SlotsTotal is { } total ? Loc.F("{0} of {1}", used, total) : used.ToString(CultureInfo.CurrentCulture) : "—", null, false));
        rows.Add((Loc.T("Form factor"), facts.FormFactor ?? "—", null, false));
        rows.Add((Loc.T("Type"), facts.MemoryType ?? "—", null, false));
        rows.Add((Loc.T("Hardware reserved"), LiveUsageFormat.Bytes(facts.HardwareReservedBytes), Loc.T("Memory set aside for the BIOS, built-in graphics and other devices."), false));
    }

    private void ShowDisk(LiveResourceItem item, DiskSample? disk, DiskFacts? facts, List<(string, string, string?, bool)> rows)
    {
        GraphCaption = Loc.T("Active time over 60 seconds");
        double top = LiveUsageMath.NiceScale(Math.Max(item.Second.Max(), item.Third.Max()), 1024 * 1024);
        SyncSmallGraphs(ExtraGraphs, [("rate", Loc.T("Disk transfer rate"), item.Second, item.Third, top,
            Loc.F("Read {0} · Write {1} (dashed)", LiveUsageFormat.BytesPerSecond(disk?.ReadBytesPerSecond ?? 0), LiveUsageFormat.BytesPerSecond(disk?.WriteBytesPerSecond ?? 0)),
            LiveUsageFormat.BytesPerSecond(top))]);
        rows.Add((Loc.T("Active time"), LiveUsageFormat.Percent(disk?.ActivePercent ?? 0), Loc.T("Share of time the disk was busy. Near 100 % means programs wait for it."), true));
        rows.Add((Loc.T("Average response time"), LiveUsageFormat.Milliseconds(disk?.AverageResponseMs ?? 0), Loc.T("How long each read or write took. Hard drives are slower than SSDs."), true));
        rows.Add((Loc.T("Read speed"), LiveUsageFormat.BytesPerSecond(disk?.ReadBytesPerSecond ?? 0), null, true));
        rows.Add((Loc.T("Write speed"), LiveUsageFormat.BytesPerSecond(disk?.WriteBytesPerSecond ?? 0), null, true));
        rows.Add((Loc.T("Capacity"), LiveUsageFormat.Bytes(facts?.CapacityBytes), null, false));
        rows.Add((Loc.T("Formatted"), LiveUsageFormat.Bytes(facts?.FormattedBytes), Loc.T("Space inside drive letters on this disk."), false));
        rows.Add((Loc.T("System disk"), facts?.IsSystemDisk == true ? Loc.T("Yes") : Loc.T("No"), Loc.T("Windows is installed on this disk."), false));
        rows.Add((Loc.T("Page file"), facts?.HasPageFile == true ? Loc.T("Yes") : Loc.T("No"), Loc.T("Windows uses this disk as extra memory when RAM runs low."), false));
        rows.Add((Loc.T("Type"), facts is null ? "—" : Loc.T(LiveUsageFormat.MediaLabel(facts.Media)), null, false));
        rows.Add((Loc.T("Model"), facts?.Model ?? "—", null, false));
    }

    private void ShowNetwork(LiveResourceItem item, NetworkSample? net, NetworkFacts? facts, List<(string, string, string?, bool)> rows)
    {
        GraphCaption = Loc.T("Throughput over 60 seconds");
        double top = LiveUsageMath.NiceScale(Math.Max(item.Main.Max(), item.Second.Max()), 100_000);
        GraphMaximum = top;
        GraphMaxText = LiveUsageFormat.BitsPerSecond(top);
        GraphSecondValues = item.Second.ToPaddedArray();
        GraphLegend = Loc.T("Solid line: received. Dashed line: sent.");
        rows.Add((Loc.T("Send"), LiveUsageFormat.BitsPerSecond(net?.SendBitsPerSecond ?? 0), null, true));
        rows.Add((Loc.T("Receive"), LiveUsageFormat.BitsPerSecond(net?.ReceiveBitsPerSecond ?? 0), null, true));
        if (facts?.Kind == NetworkKind.WiFi)
        {
            int? signal = net?.SignalPercent ?? facts.SignalPercent;
            rows.Add((Loc.T("Signal strength"), signal is { } s ? s.ToString(CultureInfo.CurrentCulture) + " %" : "—", Loc.T("Above 60 % is good; below 40 % expect slow or dropping Wi-Fi."), true));
            rows.Add((Loc.T("SSID"), net?.Ssid ?? facts.Ssid ?? "—", Loc.T("The name of the Wi-Fi network."), false));
        }

        rows.Add((Loc.T("Adapter name"), facts?.AdapterName ?? "—", null, false));
        rows.Add((Loc.T("Connection type"), facts?.ConnectionType ?? "—", null, false));
        rows.Add((Loc.T("Link speed"), facts?.LinkSpeedBitsPerSecond is { } speed ? LiveUsageFormat.BitsPerSecond(speed) : "—", Loc.T("The speed the adapter connected at, not your internet speed."), false));
        rows.Add((Loc.T("IPv4 address"), facts is null || facts.IPv4Addresses.Count == 0 ? "—" : string.Join(", ", facts.IPv4Addresses), null, false));
        rows.Add((Loc.T("IPv6 address"), facts is null || facts.IPv6Addresses.Count == 0 ? "—" : string.Join(", ", facts.IPv6Addresses), null, false));
    }

    private void ShowGpu(LiveResourceItem item, GpuSample? gpu, GpuFacts? facts, List<(string, string, string?, bool)> rows)
    {
        GraphCaption = Loc.T("Engine use over 60 seconds");
        var engines = LiveUsageMath.ChooseEngines(item.Engines.Keys, gpu?.EnginePercents);
        SyncSmallGraphs(EngineGraphs, engines.Select(e => (e, Loc.T(CounterInstanceParser.EngineDisplayName(e)), item.Engines[e], (SampleRing?)null, 100d,
            LiveUsageFormat.Percent(item.Engines[e].Latest), "100 %")));

        long dedicatedTotal = Math.Max(facts?.DedicatedMemoryBytes ?? 0, (long)item.Third.Max());
        long sharedTotal = Math.Max(facts?.SharedMemoryBytes ?? 0, (long)item.Fourth.Max());
        long dedicated = gpu?.DedicatedUsedBytes ?? 0, shared = gpu?.SharedUsedBytes ?? 0;
        SyncSmallGraphs(ExtraGraphs,
        [
            ("dedicated", Loc.T("Dedicated GPU memory"), item.Third, null, Math.Max(1, dedicatedTotal), LiveUsageFormat.UsedOfTotal(dedicated, dedicatedTotal), LiveUsageFormat.Bytes(dedicatedTotal)),
            ("shared", Loc.T("Shared GPU memory"), item.Fourth, null, Math.Max(1, sharedTotal), LiveUsageFormat.UsedOfTotal(shared, sharedTotal), LiveUsageFormat.Bytes(sharedTotal)),
        ]);

        rows.Add((Loc.T("Utilization"), LiveUsageFormat.Percent(gpu?.UtilizationPercent ?? 0), Loc.T("The busiest engine, as Task Manager counts it."), true));
        rows.Add((Loc.T("Dedicated GPU memory"), LiveUsageFormat.UsedOfTotal(dedicated, dedicatedTotal), Loc.T("Memory on the graphics card itself."), true));
        rows.Add((Loc.T("Shared GPU memory"), LiveUsageFormat.UsedOfTotal(shared, sharedTotal), Loc.T("System memory the graphics card borrows when its own is full."), true));
        rows.Add((Loc.T("GPU memory"), LiveUsageFormat.UsedOfTotal(dedicated + shared, dedicatedTotal + sharedTotal), null, true));
        if (gpu?.TemperatureC is { } temp)
        {
            rows.Add((Loc.T("GPU temperature"), string.Create(CultureInfo.InvariantCulture, $"{temp:0} °C"), Loc.T("From the Temperatures page's sensors."), true));
        }

        rows.Add((Loc.T("Driver version"), facts?.DriverVersion ?? "—", null, false));
        rows.Add((Loc.T("Driver date"), facts?.DriverDate is { } date ? date.ToString("d", CultureInfo.CurrentCulture) : "—", null, false));
        rows.Add((Loc.T("DirectX version"), facts?.DirectXVersion ?? "—", Loc.T("FL is the highest feature level the driver offers; games check it."), false));
        rows.Add((Loc.T("Physical location"), facts?.PhysicalLocation ?? "—", null, false));
    }

    /// <summary>Updates the small graphs in place (same keys) or rebuilds them when the set changed.</summary>
    private static void SyncSmallGraphs(ObservableCollection<LiveSmallGraph> target,
        IEnumerable<(string Key, string Title, SampleRing Ring, SampleRing? Second, double Maximum, string Value, string MaxText)> source)
    {
        var list = source.ToList();
        if (target.Count != list.Count || target.Where((g, i) => g.Key != list[i].Key).Any())
        {
            target.Clear();
            foreach (var s in list)
            {
                target.Add(new LiveSmallGraph(s.Key, s.Title));
            }
        }

        for (int i = 0; i < list.Count; i++)
        {
            var (_, title, ring, second, maximum, value, maxText) = list[i];
            var graph = target[i];
            graph.Title = title;
            graph.Values = ring.ToPaddedArray();
            graph.SecondValues = second?.ToPaddedArray();
            graph.Maximum = maximum;
            graph.ValueText = value;
            graph.MaxText = maxText;
        }
    }

    private static void Fill(ObservableCollection<LiveDetailRow> target, IEnumerable<(string Label, string Value, string? Hint, bool Live)> rows)
    {
        var list = rows.ToList();
        if (target.Count != list.Count || target.Where((r, i) => r.Label != list[i].Label).Any())
        {
            target.Clear();
            foreach (var row in list)
            {
                target.Add(new LiveDetailRow(row.Label, row.Value, row.Hint));
            }

            return;
        }

        for (int i = 0; i < list.Count; i++)
        {
            target[i].Value = list[i].Value;
        }
    }
}
