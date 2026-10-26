using PcChecker.Core.Common;

namespace PcChecker.Core.Hardware;

/// <summary>Demo inventory for the sample gaming desktop (i7-13700K, RTX 4070, 32 GB DDR5, 990 PRO + 4 TB HDD, Z790-E).</summary>
public sealed class DemoHardwareInventoryService : IHardwareInventoryService
{
    public HardwareInventory? Last { get; private set; }

    public async Task<HardwareInventory> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string[] stages = ["System", "Processor", "Graphics", "Memory", "Storage", "Network", "Monitors", "Audio", "USB", "Input", "Devices"];
        for (int i = 0; i < stages.Length; i++)
        {
            progress?.Report(new ProgressUpdate(stages[i], 100.0 * i / stages.Length));
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);
        }

        Last = Build();
        progress?.Report(new ProgressUpdate("Done", 100));
        return Last;
    }

    /// <summary>The sample inventory, also used by other demo services that need hardware facts.</summary>
    public static HardwareInventory Build()
    {
        var none = new Dictionary<string, string>();
        var inventory = new HardwareInventory
        {
            System = new ComputerSystemInfo(DemoData.ComputerName, "ASUS", "System Product Name", "x64-based PC", "To be filled by O.E.M.", "SKU",
                DemoData.TotalMemoryBytes, 1, 24, "WORKGROUP", @"GAMING-DESKTOP\Alex", "Desktop", "Desktop", Props(("Manufacturer", "ASUS"), ("Model", "System Product Name"))),
            OperatingSystem = new OperatingSystemInfo(DemoData.OsName, DemoData.OsName, "10.0.26100", "26100", "24H2", "64-bit",
                new DateTime(2025, 3, 14), DateTime.Now.AddHours(-6), @"C:\Windows\system32", @"C:\Windows", "en-US", DemoData.TotalMemoryBytes, DemoData.TotalMemoryBytes / 2, "00330-80000-00000-AA000", "Alex",
                Props(("Version", "10.0.26100"), ("DisplayVersion", "24H2"))),
            Bios = new BiosInfo("American Megatrends 2801", "American Megatrends Inc.", "2801", "3.5", new DateTime(2025, 1, 20), "DEMO-BIOS-0001", true, true, Props(("SMBIOSBIOSVersion", "2801"))),
            Motherboard = new MotherboardInfo(DemoData.MotherboardName, "ASUSTeK COMPUTER INC.", "ROG STRIX Z790-E GAMING WIFI", "Rev 1.xx", "DEMO-MB-0001", "Intel Z790", Props(("Product", "ROG STRIX Z790-E GAMING WIFI"))),
        };
        inventory.Processors.Add(new CpuInfo(DemoData.CpuName, "GenuineIntel", "CPU0", "OK", "Intel64 Family 6 Model 183 Stepping 1", "x64", "LGA1700", 16, 24, 5400, 5300, 24576, 30720,
            "BFEBFBFF000B0671", "6", "1", true, 12, 64, Props(("NumberOfCores", "16"), ("MaxClockSpeed", "5400"))));
        inventory.Graphics.Add(new GpuInfo(DemoData.GpuName, "NVIDIA", "VideoController1", "OK", DemoData.GpuMemoryBytes, "32.0.15.6094", new DateTime(2025, 2, 10), "NVIDIA GeForce RTX 4070",
            "2560 x 1440 x 4294967296 colors", 2560, 1440, 240, @"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043", "NVIDIA", false, Props(("AdapterRAM", DemoData.GpuMemoryBytes.ToString()))));
        inventory.MemoryModules.Add(new MemoryModuleInfo("DIMM_A2 16 GB DDR5", "G.Skill", "Physical Memory 0", "OK", "BANK 0", "DIMM_A2", DemoData.MemoryModuleBytes, 6000, 4800, "DDR5", "DIMM", "F5-6000J3636F16G", "00000A2", 1100, 64, 64, none));
        inventory.MemoryModules.Add(new MemoryModuleInfo("DIMM_B2 16 GB DDR5", "G.Skill", "Physical Memory 2", "OK", "BANK 2", "DIMM_B2", DemoData.MemoryModuleBytes, 6000, 4800, "DDR5", "DIMM", "F5-6000J3636F16G", "00000B2", 1100, 64, 64, none));
        inventory.PhysicalDisks.Add(new PhysicalDiskInfo(DemoData.NvmeModel, "Samsung", @"\\.\PHYSICALDRIVE0", "OK", DemoData.NvmeModel, "S6Z2NJ0DEMO0001", "4B2QJXD7", "SCSI", "SSD", "NVMe", DemoData.NvmeBytes, "Healthy", null,
            @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG_SSD_990", [new PartitionInfo("Disk #0, Partition #0", "Disk #0, Partition #0", 104857600, 1048576, 0, "GPT: System", true, true, true, none), new PartitionInfo("Disk #0, Partition #2", "Disk #0, Partition #2", DemoData.NvmeBytes - 800_000_000, 122683392, 2, "GPT: Basic Data", false, false, true, none)], none));
        inventory.PhysicalDisks.Add(new PhysicalDiskInfo(DemoData.HddModel, "Western Digital", @"\\.\PHYSICALDRIVE1", "OK", DemoData.HddModel, "WD-WX12DEMO0002", "80.00A80", "SCSI", "HDD", "SATA", DemoData.HddBytes, "Warning", 5400,
            @"SCSI\DISK&VEN_WDC&PROD_WD40EZAZ", [new PartitionInfo("Disk #1, Partition #0", "Disk #1, Partition #0", DemoData.HddBytes - 16_000_000, 16777216, 0, "GPT: Basic Data", false, false, true, none)], none));
        inventory.LogicalDrives.Add(new LogicalDriveInfo("C:", "C:", "Windows", "NTFS", "Local Disk", DemoData.NvmeBytes - 800_000_000, 742_000_000_000, "A1B2C3D4", false, none));
        inventory.LogicalDrives.Add(new LogicalDriveInfo("D:", "D:", "Games & Media", "NTFS", "Local Disk", DemoData.HddBytes - 16_000_000, 1_210_000_000_000, "E5F6A7B8", false, none));
        inventory.NetworkAdapters.Add(new NetworkAdapterInfo("Intel Ethernet Controller I226-V", "Intel", "7", "OK", "A8-5E-45-DE-M0-01", "Ethernet 802.3", 2_500_000_000, true, "Ethernet", "Connected", ["192.168.1.42", "fe80::1c2d:3e4f:5a6b:7c8d"], ["192.168.1.1"], ["192.168.1.1"], true, @"PCI\VEN_8086&DEV_125C", none));
        inventory.NetworkAdapters.Add(new NetworkAdapterInfo("Intel Wi-Fi 6E AX211 160MHz", "Intel", "9", "OK", "A8-5E-45-DE-M0-02", "Ethernet 802.3", 2_400_000_000, true, "Wi-Fi", "Disconnected", [], [], [], true, @"PCI\VEN_8086&DEV_51F0", none));
        inventory.Monitors.Add(new MonitorInfo(DemoData.MonitorName, "ASUS", "DISPLAY\\AUS27B1", "OK", "27B1", "M9LMQS000001", 2023, 12, "DISPLAY\\AUS27B1\\5&1a2b3c4d&0&UID4352", 2560, 1440, none));
        inventory.Monitors.Add(new MonitorInfo("Dell U2415", "Dell", "DISPLAY\\DELA0C4", "OK", "A0C4", "7MT0189J0ABS", 2019, 30, "DISPLAY\\DELA0C4\\5&1a2b3c4d&0&UID4353", 1920, 1200, none));
        inventory.AudioDevices.Add(new AudioDeviceInfo("Realtek(R) Audio", "Realtek", "HDAUDIO\\FUNC_01&VEN_10EC&DEV_4080", "OK", "Realtek(R) Audio", "HDAUDIO\\FUNC_01&VEN_10EC&DEV_4080", none));
        inventory.AudioDevices.Add(new AudioDeviceInfo("NVIDIA High Definition Audio", "NVIDIA", "HDAUDIO\\FUNC_01&VEN_10DE&DEV_00A3", "OK", "NVIDIA High Definition Audio", "HDAUDIO\\FUNC_01&VEN_10DE&DEV_00A3", none));
        inventory.AudioDevices.Add(new AudioDeviceInfo("SteelSeries Arctis Nova Pro", "SteelSeries", "USB\\VID_1038&PID_12E0", "OK", "Arctis Nova Pro Wireless", "USB\\VID_1038&PID_12E0", none));
        inventory.UsbDevices.Add(new UsbDeviceInfo("Logitech BRIO", "Logitech", "USB\\VID_046D&PID_085E", "OK", "USB Composite Device", "usbccgp", "{36fc9e60-c465-11cf-8056-444553540000}", "USB", none));
        inventory.UsbDevices.Add(new UsbDeviceInfo("USB Mass Storage Device", "SanDisk", "USB\\VID_0781&PID_5591", "OK", "SanDisk Ultra USB 3.0", "USBSTOR", "{36fc9e60-c465-11cf-8056-444553540000}", "USB", none));
        inventory.InputDevices.Add(new InputDeviceInfo(DemoData.KeyboardName, "Corsair", "HID\\VID_1B1C&PID_1B49", "OK", "Keyboard", "USB keyboard", "00000409", null, 12, "HID\\VID_1B1C&PID_1B49", none));
        inventory.InputDevices.Add(new InputDeviceInfo(DemoData.MouseName, "Logitech", "HID\\VID_046D&PID_C547", "OK", "Mouse", "HID-compliant mouse", null, 5, null, "HID\\VID_046D&PID_C547", none));
        inventory.PnpDevices.Add(new PnpDeviceInfo("Intel(R) Wireless Bluetooth(R)", "Intel", "USB\\VID_8087&PID_0033", "OK", "Bluetooth", "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", "BTHUSB", "Intel(R) Wireless Bluetooth(R)", 0, true, ["USB\\VID_8087&PID_0033"], none));
        inventory.PnpDevices.Add(new PnpDeviceInfo("Unknown device", null, "ACPI\\ASUS2021", "Error", "Other devices", "{4d36e97e-e325-11ce-bfc1-08002be10318}", null, "ASUS ACPI device (no driver)", 28, true, ["ACPI\\ASUS2021"], none));
        return inventory;
    }

    private static Dictionary<string, string> Props(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in pairs)
        {
            d[k] = v;
        }

        return d;
    }
}
