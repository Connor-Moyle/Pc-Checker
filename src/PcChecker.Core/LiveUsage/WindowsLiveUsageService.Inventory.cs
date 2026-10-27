using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Hardware;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PcChecker.Core.LiveUsage;

/// <summary>
/// The static facts: processor topology from <c>GetLogicalProcessorInformation</c> and WMI, memory modules and slots
/// from WMI, disks from the PhysicalDisk counter instances plus MSFT_PhysicalDisk, active adapters from .NET's network
/// information, and graphics cards from DXGI (name, LUID, memory) matched to WMI for the driver and location.
/// </summary>
public sealed partial class WindowsLiveUsageService
{
    private const int PfVirtFirmwareEnabled = 21;
    private const ushort MicrosoftVendorId = 0x1414;

    public async Task<LiveUsageInventory> GetInventoryAsync(CancellationToken cancellationToken)
    {
        var wifi = await ReadWifiSafeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var inventory = await Task.Run(() => ReadInventory(wifi, cancellationToken), cancellationToken).ConfigureAwait(false);
            _inventory = inventory;
            _wifi = wifi;
            _wifiClock.Restart();
            return inventory;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, Network.WifiInterfaceInfo>> ReadWifiSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadWifiAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the Wi-Fi connection failed.");
            return new Dictionary<string, Network.WifiInterfaceInfo>();
        }
    }

    private LiveUsageInventory ReadInventory(IReadOnlyDictionary<string, Network.WifiInterfaceInfo> wifi, CancellationToken cancellationToken)
    {
        var wmiWarnings = new List<string>();
        var wmi = new WmiQueryRunner(_logger, wmiWarnings);
        var warnings = new List<string>();
        EnsureCounters(warnings);
        var perf = ReadPerformanceInfo();

        var cpu = Guard(() => ReadCpu(wmi), "processor", warnings) ?? new CpuFacts(Environment.MachineName, 0, 1, Environment.ProcessorCount, Environment.ProcessorCount, null, null, null, null);
        cancellationToken.ThrowIfCancellationRequested();
        var memory = Guard(() => ReadMemory(wmi, perf), "memory", warnings) ?? new MemoryFacts(0, null, null, null, null, null, null);
        cancellationToken.ThrowIfCancellationRequested();
        var disks = Guard(() => ReadDisks(wmi), "disks", warnings) ?? [];
        cancellationToken.ThrowIfCancellationRequested();
        var networks = Guard(() => ReadNetworks(wifi), "network adapters", warnings) ?? [];
        cancellationToken.ThrowIfCancellationRequested();
        var gpus = Guard(() => ReadGpus(wmi), "graphics cards", warnings) ?? [];
        foreach (string warning in wmiWarnings)
        {
            _logger.LogDebug("Live usage inventory: {Warning}", warning);
        }

        return new LiveUsageInventory(cpu, memory, disks, networks, gpus, warnings);
    }

    private T? Guard<T>(Func<T> read, string what, List<string> warnings)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading the {What} for Live usage failed.", what);
            warnings.Add($"Some details about the {what} could not be read.");
            return null;
        }
    }

    private static CpuFacts ReadCpu(WmiQueryRunner wmi)
    {
        var rows = wmi.Query("SELECT Name, MaxClockSpeed, NumberOfCores, NumberOfLogicalProcessors, VirtualizationFirmwareEnabled, L2CacheSize, L3CacheSize FROM Win32_Processor", friendlyName: "Processor");
        var first = rows.FirstOrDefault();
        string name = first?.Str("Name")?.Trim()
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string
            ?? "Processor";
        double baseMhz = first?.Int("MaxClockSpeed") ?? 0;
        if (baseMhz <= 0 && Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", null) is int mhz)
        {
            baseMhz = mhz;
        }

        var topology = ReadTopology();
        int sockets = topology?.Sockets > 0 ? topology.Sockets : Math.Max(1, rows.Count);
        int cores = topology?.Cores > 0 ? topology.Cores : rows.Sum(r => r.Int("NumberOfCores") ?? 0);
        int logical = Math.Max(Environment.ProcessorCount, topology?.LogicalProcessors ?? 0);

        bool? virtualization = null;
        try
        {
            virtualization = IsProcessorFeaturePresent(PfVirtFirmwareEnabled);
        }
        catch (EntryPointNotFoundException)
        {
        }

        // With Hyper-V running the firmware flag is hidden from Windows; the hypervisor being present means it is on.
        bool hypervisor = wmi.Query("SELECT HypervisorPresent FROM Win32_ComputerSystem", friendlyName: "Computer system").FirstOrDefault()?.Bool("HypervisorPresent") == true;
        if (hypervisor || first?.Bool("VirtualizationFirmwareEnabled") == true)
        {
            virtualization = true;
        }

        long? l1 = Positive(topology?.L1CacheBytes);
        long? l2 = Positive(topology?.L2CacheBytes) ?? Positive(rows.Sum(r => (long)(r.Int("L2CacheSize") ?? 0)) * 1024);
        long? l3 = Positive(topology?.L3CacheBytes) ?? Positive(rows.Sum(r => (long)(r.Int("L3CacheSize") ?? 0)) * 1024);
        return new CpuFacts(name, baseMhz, sockets, Math.Max(cores, 1), logical, virtualization, l1, l2, l3);
    }

    private static long? Positive(long? value) => value > 0 ? value : null;

    private static CpuTopology? ReadTopology()
    {
        uint length = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref length);
        if (length == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformation(buffer, ref length))
            {
                return null;
            }

            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, (int)length);
            return CpuTopologyParser.Parse(bytes, IntPtr.Size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static MemoryFacts ReadMemory(WmiQueryRunner wmi, PerformanceInfo? perf)
    {
        long total = perf is { } p ? (long)p.PhysicalTotal * (long)p.PageSize : 0;
        long? installed = null;
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out ulong kilobytes) && kilobytes > 0)
            {
                installed = (long)kilobytes * 1024;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }

        var modules = wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, FormFactor, SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory", friendlyName: "Memory modules");
        int speed = modules.Select(m => m.Int("ConfiguredClockSpeed") > 0 ? m.Int("ConfiguredClockSpeed") ?? 0 : m.Int("Speed") ?? 0).DefaultIfEmpty(0).Max();
        int slots = wmi.Query("SELECT MemoryDevices, Use FROM Win32_PhysicalMemoryArray", friendlyName: "Memory slots")
            .Where(r => (r.Int("Use") ?? 3) == 3)
            .Sum(r => r.Int("MemoryDevices") ?? 0);
        var first = modules.FirstOrDefault();
        installed ??= Positive(modules.Sum(m => m.Long("Capacity") ?? 0));
        return new MemoryFacts(total, installed, speed > 0 ? speed : null, modules.Count > 0 ? modules.Count : null, slots > 0 ? Math.Max(slots, modules.Count) : null,
            HardwareMappings.MemoryFormFactorName(first?.Int("FormFactor")), HardwareMappings.MemoryTypeName(first?.Int("SMBIOSMemoryType"), first?.Int("MemoryType")));
    }

    private List<DiskFacts> ReadDisks(WmiQueryRunner wmi)
    {
        var instances = new Dictionary<int, (string Instance, IReadOnlyList<string> Letters)>();
        if (_query is not null)
        {
            _query.Collect();
            foreach (string instance in _query.ReadInstanceNames(_c.DiskIdle))
            {
                if (CounterInstanceParser.TryParsePhysicalDisk(instance, out int index, out var letters))
                {
                    instances[index] = (instance, letters);
                }
            }
        }

        var storage = wmi.Query("SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk", WmiQueryRunner.StorageNamespace, "Physical disks")
            .Select(r => (Index: int.TryParse(r.Str("DeviceId"), NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : -1, Row: r))
            .Where(r => r.Index >= 0)
            .GroupBy(r => r.Index)
            .ToDictionary(g => g.Key, g => g.First().Row);
        var drives = wmi.Query("SELECT Index, Model, Size, MediaType FROM Win32_DiskDrive", friendlyName: "Disk drives")
            .Where(r => r.Int("Index") is >= 0)
            .GroupBy(r => r.Int("Index")!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var volumeSizes = wmi.Query("SELECT DeviceID, Size FROM Win32_LogicalDisk", friendlyName: "Volumes")
            .Where(r => r.Str("DeviceID") is { Length: 2 })
            .GroupBy(r => r.Str("DeviceID")!.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First().Long("Size"));
        var pageFileLetters = wmi.Query("SELECT Name FROM Win32_PageFileUsage", friendlyName: "Page files")
            .Select(r => r.Str("Name"))
            .Where(n => n is { Length: >= 2 } && n[1] == ':')
            .Select(n => char.ToUpperInvariant(n![0]) + ":")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string systemDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\').ToUpperInvariant();

        var result = new List<DiskFacts>();
        foreach (int index in instances.Keys.Concat(storage.Keys).Concat(drives.Keys).Distinct().Order())
        {
            instances.TryGetValue(index, out var counter);
            storage.TryGetValue(index, out var msft);
            drives.TryGetValue(index, out var drive);
            var letters = counter.Letters ?? [];
            string model = (msft?.Str("FriendlyName") ?? drive?.Str("Model") ?? "Disk").Trim();
            var media = LiveUsageMath.ClassifyDisk(msft?.Int("MediaType"), msft?.Int("BusType"), model);
            if (media == DiskMediaKind.Unknown && drive?.Str("MediaType") is { } wmiMedia
                && (wmiMedia.Contains("Removable", StringComparison.OrdinalIgnoreCase) || wmiMedia.Contains("External", StringComparison.OrdinalIgnoreCase)))
            {
                media = DiskMediaKind.Removable;
            }

            long? formatted = Positive(letters.Sum(l => volumeSizes.GetValueOrDefault(l) ?? 0));
            result.Add(new DiskFacts(index, model, letters, media, msft?.Long("Size") ?? drive?.Long("Size"), formatted,
                letters.Contains(systemDrive, StringComparer.OrdinalIgnoreCase), letters.Any(pageFileLetters.Contains), counter.Instance));
        }

        return result;
    }

    private List<NetworkFacts> ReadNetworks(IReadOnlyDictionary<string, Network.WifiInterfaceInfo> wifi)
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(a => a.OperationalStatus == OperationalStatus.Up
                && a.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                && !a.IsReceiveOnly)
            .ToList();
        _adapters = adapters;
        var result = new List<NetworkFacts>();
        foreach (var adapter in adapters)
        {
            var kind = adapter.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => NetworkKind.WiFi,
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => NetworkKind.Ethernet,
                _ => NetworkKind.Other,
            };
            var v4 = new List<string>();
            var v6 = new List<string>();
            try
            {
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    (address.Address.AddressFamily == AddressFamily.InterNetworkV6 ? v6 : v4).Add(address.Address.ToString());
                }
            }
            catch (NetworkInformationException)
            {
            }

            wifi.TryGetValue(adapter.Name, out var w);
            long speed = 0;
            try
            {
                speed = adapter.Speed;
            }
            catch (PlatformNotSupportedException)
            {
            }

            string connectionType = kind switch
            {
                NetworkKind.WiFi => w?.Radio ?? "Wi-Fi",
                NetworkKind.Ethernet => "Ethernet",
                _ => adapter.NetworkInterfaceType.ToString(),
            };
            result.Add(new NetworkFacts(adapter.Id, adapter.Name, adapter.Description, kind, connectionType, v4, v6, w?.Ssid, w?.SignalPercent, speed > 0 ? speed : null));
        }

        // Physical adapters first, like Task Manager: Ethernet, then Wi-Fi, then the rest.
        return result.OrderBy(n => n.Kind).ThenBy(n => n.ConnectionName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private List<GpuFacts> ReadGpus(WmiQueryRunner wmi)
    {
        var controllers = wmi.Query("SELECT Name, PNPDeviceID, DriverVersion, DriverDate FROM Win32_VideoController", friendlyName: "Graphics cards");
        var locations = wmi.Query("SELECT DeviceID, Location FROM Win32_PnPSignedDriver WHERE DeviceClass = 'DISPLAY'", friendlyName: "Display drivers");
        var used = new HashSet<WmiRow>();
        var result = new List<GpuFacts>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; i < 16; i++)
        {
            if (factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Failure || adapter is null)
            {
                break;
            }

            using (adapter)
            {
                var d = adapter.Description1;
                if ((d.Flags & AdapterFlags.Software) != 0 || d.VendorId == MicrosoftVendorId)
                {
                    continue;
                }

                string luid = CounterInstanceParser.FormatLuid(d.Luid.HighPart, d.Luid.LowPart);
                if (result.Any(g => g.Luid == luid))
                {
                    continue;
                }

                string pnpKey = string.Create(CultureInfo.InvariantCulture, $"VEN_{d.VendorId:X4}&DEV_{d.DeviceId:X4}");
                var controller = controllers.FirstOrDefault(c => !used.Contains(c) && (c.Str("PNPDeviceID") ?? string.Empty).Contains(pnpKey, StringComparison.OrdinalIgnoreCase));
                if (controller is not null)
                {
                    used.Add(controller);
                }

                string? pnp = controller?.Str("PNPDeviceID");
                string? location = pnp is null ? null : locations.FirstOrDefault(l => string.Equals(l.Str("DeviceID"), pnp, StringComparison.OrdinalIgnoreCase))?.Str("Location");
                string name = (d.Description ?? controller?.Str("Name") ?? "Graphics card").Trim();
                result.Add(new GpuFacts(result.Count, name, luid, (long)(ulong)d.DedicatedVideoMemory, (long)(ulong)d.SharedSystemMemory,
                    controller?.Str("DriverVersion"), controller?.Date("DriverDate"), DirectXVersion(adapter), location));
            }
        }

        return result;
    }

    /// <summary>"12 (FL 12.1)": the DirectX runtime and the highest feature level the card's driver offers.</summary>
    private static string? DirectXVersion(IDXGIAdapter1 adapter)
    {
        (FeatureLevel Level, string Text)[] levels =
        [
            (FeatureLevel.Level_12_2, "12.2"), (FeatureLevel.Level_12_1, "12.1"), (FeatureLevel.Level_12_0, "12.0"),
            (FeatureLevel.Level_11_1, "11.1"), (FeatureLevel.Level_11_0, "11.0"), (FeatureLevel.Level_10_1, "10.1"), (FeatureLevel.Level_10_0, "10.0"),
        ];
        foreach (var (level, text) in levels)
        {
            try
            {
                if (D3D11.IsSupportedFeatureLevel(adapter, level, DeviceCreationFlags.None))
                {
                    return $"12 (FL {text})";
                }
            }
            catch (Exception)
            {
                // Older runtimes reject the newer feature levels outright; try the next one.
            }
        }

        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessorFeaturePresent(int processorFeature);
}
