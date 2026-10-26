namespace PcChecker.Core.Hardware;

/// <summary>Everything a hardware scan found. Lists are empty (never null) when a category could not be read.</summary>
public sealed class HardwareInventory
{
    public DateTimeOffset Captured { get; set; } = DateTimeOffset.Now;
    public ComputerSystemInfo? System { get; set; }
    public OperatingSystemInfo? OperatingSystem { get; set; }
    public BiosInfo? Bios { get; set; }
    public MotherboardInfo? Motherboard { get; set; }
    public List<CpuInfo> Processors { get; set; } = [];
    public List<GpuInfo> Graphics { get; set; } = [];
    public List<MemoryModuleInfo> MemoryModules { get; set; } = [];
    public List<PhysicalDiskInfo> PhysicalDisks { get; set; } = [];
    public List<LogicalDriveInfo> LogicalDrives { get; set; } = [];
    public List<NetworkAdapterInfo> NetworkAdapters { get; set; } = [];
    public List<MonitorInfo> Monitors { get; set; } = [];
    public List<AudioDeviceInfo> AudioDevices { get; set; } = [];
    public List<UsbDeviceInfo> UsbDevices { get; set; } = [];
    public List<BatteryInfo> Batteries { get; set; } = [];
    public List<InputDeviceInfo> InputDevices { get; set; } = [];
    public List<PnpDeviceInfo> PnpDevices { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    /// <summary>Total installed memory from modules, or the system total when modules are unavailable.</summary>
    public long? TotalMemoryBytes
    {
        get
        {
            long sum = 0;
            bool any = false;
            foreach (var m in MemoryModules)
            {
                if (m.CapacityBytes is { } c)
                {
                    sum += c;
                    any = true;
                }
            }

            return any ? sum : System?.TotalPhysicalMemoryBytes;
        }
    }

    /// <summary>Flat list of every item for generic display and search.</summary>
    public IEnumerable<HardwareItem> AllItems()
    {
        if (System is not null) { yield return System; }
        if (OperatingSystem is not null) { yield return OperatingSystem; }
        if (Bios is not null) { yield return Bios; }
        if (Motherboard is not null) { yield return Motherboard; }
        foreach (var x in Processors) { yield return x; }
        foreach (var x in Graphics) { yield return x; }
        foreach (var x in MemoryModules) { yield return x; }
        foreach (var x in PhysicalDisks) { yield return x; }
        foreach (var x in LogicalDrives) { yield return x; }
        foreach (var x in NetworkAdapters) { yield return x; }
        foreach (var x in Monitors) { yield return x; }
        foreach (var x in AudioDevices) { yield return x; }
        foreach (var x in UsbDevices) { yield return x; }
        foreach (var x in Batteries) { yield return x; }
        foreach (var x in InputDevices) { yield return x; }
        foreach (var x in PnpDevices) { yield return x; }
    }
}
