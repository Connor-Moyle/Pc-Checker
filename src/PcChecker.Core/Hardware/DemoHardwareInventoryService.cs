using PcChecker.Core.Common;

namespace PcChecker.Core.Hardware;

/// <summary>Demo inventory for the sample gaming desktop (i7-13700K, RTX 4070, 32 GB DDR5, 990 PRO + 4 TB HDD, Z790-E).</summary>
public sealed class DemoHardwareInventoryService : IHardwareInventoryService
{
    public HardwareInventory? Last { get; private set; }

    public async Task<HardwareInventory> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string[] stages = ["System", "Operating system", "Processor", "BIOS and motherboard", "Graphics", "Memory", "Storage", "Network", "Monitors", "Audio", "Devices", "USB", "Battery", "Input devices"];
        for (int i = 0; i < stages.Length; i++)
        {
            progress?.Report(new ProgressUpdate(stages[i], 100.0 * i / stages.Length, $"Reading {stages[i].ToLowerInvariant()}…"));
            await DemoData.DelayAsync(cancellationToken, 40, 120).ConfigureAwait(false);
        }

        Last = Build();
        progress?.Report(new ProgressUpdate("Done", 100));
        return Last;
    }

    /// <summary>The sample inventory, also used by other demo services that need hardware facts.</summary>
    public static HardwareInventory Build()
    {
        var none = new Dictionary<string, string>();
        var lastBoot = DateTime.Now.AddHours(-6).AddMinutes(-14);
        var inventory = new HardwareInventory
        {
            System = new ComputerSystemInfo(DemoData.ComputerName, "ASUS", "System Product Name", "x64-based PC", "To be filled by O.E.M.", "SKU",
                DemoData.TotalMemoryBytes, 1, 24, "WORKGROUP", @"GAMING-DESKTOP\Alex", "Desktop", "Tower",
                Props(("Name", DemoData.ComputerName), ("Manufacturer", "ASUS"), ("Model", "System Product Name"), ("SystemType", "x64-based PC"),
                    ("PCSystemType", "1"), ("TotalPhysicalMemory", DemoData.TotalMemoryBytes.ToString()), ("NumberOfLogicalProcessors", "24"),
                    ("Domain", "WORKGROUP"), ("UserName", @"GAMING-DESKTOP\Alex"), ("BootupState", "Normal boot"), ("HypervisorPresent", "Yes"),
                    ("Product.UUID", "5A1B2C3D-0000-4000-8000-DEMO00000001"), ("Product.IdentifyingNumber", "System Serial Number"),
                    ("Enclosure.ChassisTypes", "7"), ("Enclosure.Manufacturer", "Default string"),
                    (HardwareSummary.MemorySlotsKey, "4"), (HardwareSummary.MemoryMaxCapacityKey, "201326592"),
                    ("Tpm.SpecVersion", "2.0, 0, 1.59"), ("Tpm.IsEnabled_InitialValue", "Yes"), ("Tpm.ManufacturerIdTxt", "INTC"), ("Tpm.Present", "Yes"),
                    ("SecureBoot.Enabled", "Yes"))),
            OperatingSystem = new OperatingSystemInfo(DemoData.OsName, DemoData.OsName, "10.0.26100", "26100", "24H2", "64-bit",
                new DateTime(2025, 3, 14, 18, 2, 11), lastBoot, @"C:\Windows\system32", @"C:\Windows", "en-US", DemoData.TotalMemoryBytes - 300_000_000, 17_800_000_000, "00330-80000-00000-AA000", "Alex",
                Props(("Caption", DemoData.OsName), ("Version", "10.0.26100"), ("BuildNumber", "26100"), ("OSArchitecture", "64-bit"),
                    ("InstallDate", "2025-03-14 18:02:11"), ("LastBootUpTime", lastBoot.ToString("yyyy-MM-dd HH:mm:ss")), ("SystemDrive", "C:"),
                    ("WindowsDirectory", @"C:\Windows"), ("OSLanguage", "1033"), ("ProductType", "1"), ("ServicePackMajorVersion", "0"),
                    ("Registry.DisplayVersion", "24H2"), ("Registry.UBR", "3037"), ("Registry.EditionID", "Professional"), ("Registry.InstallationType", "Client"),
                    ("Registry.ProductName", "Windows 10 Pro"), ("Registry.CurrentBuild", "26100"))),
            Bios = new BiosInfo("American Megatrends Inc. 2801", "American Megatrends Inc.", "ALASKA - 1072009", "2801", new DateTime(2025, 1, 20), "DEMO-BIOS-0001", true, true,
                Props(("Manufacturer", "American Megatrends Inc."), ("SMBIOSBIOSVersion", "2801"), ("Version", "ALASKA - 1072009"), ("ReleaseDate", "2025-01-20 00:00:00"),
                    ("SMBIOSMajorVersion", "3"), ("SMBIOSMinorVersion", "6"), ("BIOSVersion", "ALASKA - 1072009, 2801, American Megatrends - 5002B"), ("PrimaryBIOS", "Yes"))),
            Motherboard = new MotherboardInfo(DemoData.MotherboardName, "ASUSTeK COMPUTER INC.", "ROG STRIX Z790-E GAMING WIFI", "Rev 1.xx", "DEMO-MB-0001", "Intel Z790",
                Props(("Manufacturer", "ASUSTeK COMPUTER INC."), ("Product", "ROG STRIX Z790-E GAMING WIFI"), ("Version", "Rev 1.xx"), ("SerialNumber", "DEMO-MB-0001"),
                    ("HostingBoard", "Yes"), ("PoweredOn", "Yes"), ("Removable", "No"), ("Replaceable", "Yes"), ("Tag", "Base Board"))),
        };

        inventory.Processors.Add(new CpuInfo(DemoData.CpuName, "GenuineIntel", "CPU0", "OK", "Intel64 Family 6 Model 183 Stepping 1", "x64", "LGA1700", 16, 24, 5400, 5300, 24576, 30720,
            "BFEBFBFF000B0671", "6", "1", true, 12, 64,
            Props(("Name", DemoData.CpuName), ("Manufacturer", "GenuineIntel"), ("NumberOfCores", "16"), ("NumberOfLogicalProcessors", "24"), ("NumberOfEnabledCore", "16"),
                ("MaxClockSpeed", "5400"), ("CurrentClockSpeed", "5300"), ("L2CacheSize", "24576"), ("L3CacheSize", "30720"), ("SocketDesignation", "LGA1700"),
                ("Architecture", "9"), ("Family", "6"), ("Stepping", "1"), ("ProcessorId", "BFEBFBFF000B0671"), ("VirtualizationFirmwareEnabled", "Yes"),
                ("SecondLevelAddressTranslationExtensions", "Yes"), ("ThreadCount", "24"), ("LoadPercentage", "12"), ("AddressWidth", "64"), ("DataWidth", "64"),
                ("Caption", "Intel64 Family 6 Model 183 Stepping 1"), ("ExtClock", "100"), ("Registry.MHz", "3400"), ("Registry.Microcode", "0x12B"), ("Registry.VendorIdentifier", "GenuineIntel"))));

        inventory.Graphics.Add(new GpuInfo(DemoData.GpuName, "NVIDIA", "VideoController1", "OK", DemoData.GpuMemoryBytes, "32.0.15.6094", new DateTime(2025, 2, 10), "NVIDIA GeForce RTX 4070",
            "2560 x 1440 x 4294967296 colors", 2560, 1440, 240, @"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043&REV_A1\4&2F3A5B6C&0&0008", "NVIDIA", false,
            Props(("Name", DemoData.GpuName), ("AdapterCompatibility", "NVIDIA"), ("AdapterRAM", "4293918720"), ("Registry.qwMemorySize", DemoData.GpuMemoryBytes.ToString()),
                ("DriverVersion", "32.0.15.6094"), ("DriverDate", "2025-02-10 00:00:00"), ("VideoProcessor", "NVIDIA GeForce RTX 4070"), ("VideoArchitecture", "5"),
                ("CurrentHorizontalResolution", "2560"), ("CurrentVerticalResolution", "1440"), ("CurrentRefreshRate", "240"), ("CurrentBitsPerPixel", "32"),
                ("PNPDeviceID", @"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043&REV_A1\4&2F3A5B6C&0&0008"), ("InfSection", "Section150"), ("InstalledDisplayDrivers", @"C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_demo\nvldumdx.dll"),
                ("Status", "OK"), ("Availability", "3"), ("VideoModeDescription", "2560 x 1440 x 4294967296 colors"))));

        inventory.MemoryModules.Add(DemoModule("DIMM_A2", "BANK 0", "Physical Memory 0", "00000A2"));
        inventory.MemoryModules.Add(DemoModule("DIMM_B2", "BANK 2", "Physical Memory 2", "00000B2"));

        var nvmePartitions = new[]
        {
            new PartitionInfo("Disk #0, Partition #0 (EFI)", "Disk #0, Partition #0", 104857600, 1048576, 0, "GPT: System", true, true, true, Props(("DeviceID", "Disk #0, Partition #0"), ("Size", "104857600"), ("Type", "GPT: System"))),
            new PartitionInfo("Disk #0, Partition #1 (C:)", "Disk #0, Partition #1", DemoData.NvmeBytes - 800_000_000, 122683392, 1, "GPT: Basic Data", false, false, true, Props(("DeviceID", "Disk #0, Partition #1"), ("DriveLetter", "C:"), ("Type", "GPT: Basic Data"))),
            new PartitionInfo("Disk #0, Partition #2 (Recovery)", "Disk #0, Partition #2", 680_000_000, DemoData.NvmeBytes - 690_000_000, 2, "GPT: Unknown", false, false, true, Props(("DeviceID", "Disk #0, Partition #2"), ("Type", "GPT: Unknown"))),
        };
        inventory.PhysicalDisks.Add(new PhysicalDiskInfo(DemoData.NvmeModel, "Samsung", @"\\.\PHYSICALDRIVE0", "OK", DemoData.NvmeModel, "S6Z2NJ0DEMO0001", "4B2QJXD7", "SCSI", "SSD", "NVMe", DemoData.NvmeBytes, "Healthy", null,
            @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG_SSD_990\5&DEMO0001&0&000000", nvmePartitions,
            Props(("Model", DemoData.NvmeModel), ("SerialNumber", "S6Z2NJ0DEMO0001"), ("FirmwareRevision", "4B2QJXD7"), ("Size", DemoData.NvmeBytes.ToString()), ("InterfaceType", "SCSI"),
                ("MediaType", "Fixed hard disk media"), ("Partitions", "3"), ("BytesPerSector", "512"), ("TotalSectors", (DemoData.NvmeBytes / 512).ToString()), ("Status", "OK"), ("Index", "0"),
                ("PhysicalDisk.MediaType", "4"), ("PhysicalDisk.BusType", "17"), ("PhysicalDisk.HealthStatus", "0"), ("PhysicalDisk.FriendlyName", DemoData.NvmeModel), ("PhysicalDisk.PhysicalSectorSize", "512"),
                ("PhysicalDisk.Usage", "1"), ("PhysicalDisk.CanPool", "No"), ("PhysicalDisk.CannotPoolReason", "Insufficient Capacity"))));
        inventory.PhysicalDisks.Add(new PhysicalDiskInfo(DemoData.HddModel, "Western Digital", @"\\.\PHYSICALDRIVE1", "OK", DemoData.HddModel, "WD-WX12DEMO0002", "80.00A80", "SCSI", "HDD", "SATA", DemoData.HddBytes, "Warning", 5400,
            @"SCSI\DISK&VEN_WDC&PROD_WD40EZAZ-00SF3B0\4&DEMO0002&0&010000",
            [new PartitionInfo("Disk #1, Partition #0 (D:)", "Disk #1, Partition #0", DemoData.HddBytes - 16_000_000, 16777216, 0, "GPT: Basic Data", false, false, true, Props(("DeviceID", "Disk #1, Partition #0"), ("DriveLetter", "D:")))],
            Props(("Model", DemoData.HddModel), ("SerialNumber", "WD-WX12DEMO0002"), ("FirmwareRevision", "80.00A80"), ("Size", DemoData.HddBytes.ToString()), ("InterfaceType", "SCSI"),
                ("MediaType", "Fixed hard disk media"), ("Partitions", "1"), ("BytesPerSector", "512"), ("Status", "OK"), ("Index", "1"),
                ("PhysicalDisk.MediaType", "3"), ("PhysicalDisk.BusType", "11"), ("PhysicalDisk.HealthStatus", "1"), ("PhysicalDisk.SpindleSpeed", "5400"), ("PhysicalDisk.OperationalStatus", "Predictive Failure"))));

        inventory.LogicalDrives.Add(new LogicalDriveInfo("C:", "C:", "Windows", "NTFS", "Local disk", DemoData.NvmeBytes - 800_000_000, 742_000_000_000, "A1B2C3D4", false,
            Props(("DeviceID", "C:"), ("VolumeName", "Windows"), ("FileSystem", "NTFS"), ("DriveType", "3"), ("Size", (DemoData.NvmeBytes - 800_000_000).ToString()), ("FreeSpace", "742000000000"), ("SupportsFileBasedCompression", "Yes"), ("QuotasDisabled", "Yes"))));
        inventory.LogicalDrives.Add(new LogicalDriveInfo("D:", "D:", "Games & Media", "NTFS", "Local disk", DemoData.HddBytes - 16_000_000, 1_210_000_000_000, "E5F6A7B8", false,
            Props(("DeviceID", "D:"), ("VolumeName", "Games & Media"), ("FileSystem", "NTFS"), ("DriveType", "3"), ("FreeSpace", "1210000000000"))));

        inventory.NetworkAdapters.Add(new NetworkAdapterInfo("Intel Ethernet Controller I226-V", "Intel", "7", "OK", "A8-5E-45-DE-00-01", "Ethernet", 2_500_000_000, true, "Ethernet", "Connected",
            ["192.168.1.42", "fe80::1c2d:3e4f:5a6b:7c8d"], ["192.168.1.1"], ["192.168.1.1", "1.1.1.1"], true, @"PCI\VEN_8086&DEV_125C&SUBSYS_87D21043&REV_04\DEMO0001",
            Props(("Name", "Intel Ethernet Controller I226-V"), ("AdapterType", "Ethernet 802.3"), ("MACAddress", "A8-5E-45-DE-00-01"), ("Speed", "2500000000"), ("NetConnectionID", "Ethernet"),
                ("NetConnectionStatus", "2"), ("PhysicalAdapter", "Yes"), ("Configuration.IPAddress", "192.168.1.42, fe80::1c2d:3e4f:5a6b:7c8d"), ("Configuration.DefaultIPGateway", "192.168.1.1"),
                ("Configuration.DNSServerSearchOrder", "192.168.1.1, 1.1.1.1"), ("Configuration.DHCPEnabled", "Yes"), ("Configuration.IPSubnet", "255.255.255.0, 64"), ("Configuration.MTU", "1500"),
                ("Driver.DriverVersion", "2.1.4.3"), ("Driver.DriverDate", "2024-11-05 00:00:00"), ("Driver.DriverProviderName", "Intel"), ("Driver.InfName", "oem12.inf"))));
        inventory.NetworkAdapters.Add(new NetworkAdapterInfo("Intel Wi-Fi 6E AX211 160MHz", "Intel", "9", "OK", "A8-5E-45-DE-00-02", "Wi-Fi", 2_400_000_000, true, "Wi-Fi", "Media disconnected", [], [], [], true,
            @"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086&REV_01\DEMO0002",
            Props(("Name", "Intel Wi-Fi 6E AX211 160MHz"), ("AdapterType", "Ethernet 802.3"), ("MACAddress", "A8-5E-45-DE-00-02"), ("NetConnectionID", "Wi-Fi"), ("NetConnectionStatus", "7"),
                ("PhysicalAdapter", "Yes"), ("Driver.DriverVersion", "23.80.1.3"), ("Driver.DriverProviderName", "Intel"))));

        inventory.Monitors.Add(new MonitorInfo(DemoData.MonitorName, "ASUS", "DISPLAY\\AUS27B1", "OK", "27B1", "M9LMQS000001", 2023, 12, "DISPLAY\\AUS27B1\\5&1a2b3c4d&0&UID4352_0", 2560, 1440,
            Props(("InstanceName", "DISPLAY\\AUS27B1\\5&1a2b3c4d&0&UID4352_0"), ("ManufacturerName.Decoded", "AUS"), ("UserFriendlyName.Decoded", DemoData.MonitorName), ("ProductCodeID", "27B1"),
                ("YearOfManufacture", "2023"), ("WeekOfManufacture", "12"), ("DiagonalInches", "27"), ("MaxRefreshHz", "240"), ("DisplayParams.MaxHorizontalImageSize", "60"), ("DisplayParams.MaxVerticalImageSize", "34"),
                ("DisplayParams.Active", "Yes"), ("DisplayParams.VideoInputType", "1"))));
        inventory.Monitors.Add(new MonitorInfo("Dell U2415", "Dell", "DISPLAY\\DELA0C4", "OK", "A0C4", "7MT0189J0ABS", 2019, 30, "DISPLAY\\DELA0C4\\5&1a2b3c4d&0&UID4353_0", 1920, 1200,
            Props(("InstanceName", "DISPLAY\\DELA0C4\\5&1a2b3c4d&0&UID4353_0"), ("ManufacturerName.Decoded", "DEL"), ("ProductCodeID", "A0C4"), ("YearOfManufacture", "2019"), ("WeekOfManufacture", "30"),
                ("DiagonalInches", "24.1"), ("MaxRefreshHz", "60"), ("DisplayParams.MaxHorizontalImageSize", "52"), ("DisplayParams.MaxVerticalImageSize", "32"), ("DisplayParams.Active", "Yes"))));

        inventory.AudioDevices.Add(new AudioDeviceInfo("Realtek(R) Audio", "Realtek", "HDAUDIO\\FUNC_01&VEN_10EC&DEV_4080", "OK", "Realtek(R) Audio", "HDAUDIO\\FUNC_01&VEN_10EC&DEV_4080&SUBSYS_10438A5F",
            Props(("Name", "Realtek(R) Audio"), ("Manufacturer", "Realtek"), ("ProductName", "Realtek(R) Audio"), ("PNPDeviceID", "HDAUDIO\\FUNC_01&VEN_10EC&DEV_4080&SUBSYS_10438A5F"), ("Status", "OK"), ("StatusInfo", "3"))));
        inventory.AudioDevices.Add(new AudioDeviceInfo("NVIDIA High Definition Audio", "NVIDIA", "HDAUDIO\\FUNC_01&VEN_10DE&DEV_00A3", "OK", "NVIDIA High Definition Audio", "HDAUDIO\\FUNC_01&VEN_10DE&DEV_00A3&SUBSYS_10DE0000",
            Props(("Name", "NVIDIA High Definition Audio"), ("Manufacturer", "NVIDIA"), ("Status", "OK"))));
        inventory.AudioDevices.Add(new AudioDeviceInfo("SteelSeries Arctis Nova Pro", "SteelSeries", "USB\\VID_1038&PID_12E0", "OK", "Arctis Nova Pro Wireless", "USB\\VID_1038&PID_12E0&MI_00\\DEMO",
            Props(("Name", "SteelSeries Arctis Nova Pro"), ("Manufacturer", "SteelSeries"), ("ProductName", "Arctis Nova Pro Wireless"), ("Status", "OK"))));

        inventory.UsbDevices.Add(new UsbDeviceInfo("Logitech BRIO", "Logitech", "USB\\VID_046D&PID_085E\\DEMO0001", "OK", "USB Composite Device", "usbccgp", "{36fc9e60-c465-11cf-8056-444553540000}", "USB",
            Props(("Name", "Logitech BRIO"), ("Manufacturer", "Logitech"), ("DeviceID", "USB\\VID_046D&PID_085E\\DEMO0001"), ("PNPClass", "USB"), ("Service", "usbccgp"), ("Status", "OK"), ("HardwareID", "USB\\VID_046D&PID_085E&REV_0317, USB\\VID_046D&PID_085E"))));
        inventory.UsbDevices.Add(new UsbDeviceInfo("USB Mass Storage Device", "SanDisk", "USB\\VID_0781&PID_5591\\DEMO0002", "OK", "SanDisk Ultra USB 3.0", "USBSTOR", "{36fc9e60-c465-11cf-8056-444553540000}", "USB",
            Props(("Name", "USB Mass Storage Device"), ("Manufacturer", "SanDisk"), ("DeviceID", "USB\\VID_0781&PID_5591\\DEMO0002"), ("Service", "USBSTOR"), ("Status", "OK"))));
        inventory.UsbDevices.Add(new UsbDeviceInfo("Corsair K70 RGB PRO", "Corsair", "USB\\VID_1B1C&PID_1B49\\DEMO0003", "OK", "USB Composite Device", "usbccgp", "{36fc9e60-c465-11cf-8056-444553540000}", "USB",
            Props(("Name", "Corsair K70 RGB PRO"), ("Manufacturer", "Corsair"), ("Service", "usbccgp"), ("Status", "OK"))));
        inventory.UsbDevices.Add(new UsbDeviceInfo("Logitech G PRO X SUPERLIGHT", "Logitech", "USB\\VID_046D&PID_C547\\DEMO0004", "OK", "USB Composite Device", "usbccgp", "{36fc9e60-c465-11cf-8056-444553540000}", "USB",
            Props(("Name", "Logitech G PRO X SUPERLIGHT"), ("Manufacturer", "Logitech"), ("Service", "usbccgp"), ("Status", "OK"))));
        inventory.UsbDevices.Add(new UsbDeviceInfo("USB Root Hub (USB 3.0)", "Microsoft", "USB\\ROOT_HUB30\\DEMO0005", "OK", "USB Root Hub (USB 3.0)", "USBHUB3", "{36fc9e60-c465-11cf-8056-444553540000}", "USB",
            Props(("Name", "USB Root Hub (USB 3.0)"), ("Manufacturer", "Microsoft"), ("Service", "USBHUB3"), ("Status", "OK"))));

        inventory.InputDevices.Add(new InputDeviceInfo(DemoData.KeyboardName, "Corsair", "HID\\VID_1B1C&PID_1B49&MI_00\\DEMO", "OK", "Keyboard", "USB keyboard", "00000409", null, 12, "HID\\VID_1B1C&PID_1B49&MI_00\\DEMO",
            Props(("Name", DemoData.KeyboardName), ("Description", "USB keyboard"), ("Layout", "00000409"), ("NumberOfFunctionKeys", "12"), ("Status", "OK"), ("PNPDeviceID", "HID\\VID_1B1C&PID_1B49&MI_00\\DEMO"))));
        inventory.InputDevices.Add(new InputDeviceInfo(DemoData.MouseName, "Logitech", "HID\\VID_046D&PID_C547&MI_01\\DEMO", "OK", "Mouse", "HID-compliant mouse", null, 5, null, "HID\\VID_046D&PID_C547&MI_01\\DEMO",
            Props(("Name", DemoData.MouseName), ("Description", "HID-compliant mouse"), ("NumberOfButtons", "5"), ("PointingType", "3"), ("HardwareType", "HID-compliant mouse"), ("SampleRate", "1000"), ("Status", "OK"))));

        inventory.PnpDevices.AddRange(
        [
            Pnp("Intel(R) Wireless Bluetooth(R)", "Intel", "USB\\VID_8087&PID_0033\\DEMO", "Bluetooth", "BTHUSB", 0),
            Pnp("Unknown device", null, "ACPI\\ASUS2021\\DEMO", "Other devices", null, 28),
            Pnp("Intel(R) Z790 Chipset LPC/eSPI Controller - 7A04", "Intel", "PCI\\VEN_8086&DEV_7A04\\DEMO", "System devices", "msisadrv", 0),
            Pnp("High Definition Audio Controller", "Microsoft", "PCI\\VEN_8086&DEV_7A50\\DEMO", "System devices", "HDAudBus", 0),
            Pnp("NVIDIA GeForce RTX 4070", "NVIDIA", "PCI\\VEN_10DE&DEV_2786\\DEMO", "Display adapters", "nvlddmkm", 0),
            Pnp("Samsung SSD 990 PRO 2TB", "Samsung", "SCSI\\DISK&VEN_NVME&PROD_SAMSUNG_SSD_990\\DEMO", "Disk drives", "disk", 0),
            Pnp("WDC WD40EZAZ-00SF3B0", "Western Digital", "SCSI\\DISK&VEN_WDC&PROD_WD40EZAZ\\DEMO", "Disk drives", "disk", 0),
            Pnp("Standard NVM Express Controller", "Microsoft", "PCI\\VEN_144D&DEV_A80C\\DEMO", "Storage controllers", "stornvme", 0),
            Pnp("Intel(R) USB 3.20 eXtensible Host Controller", "Intel", "PCI\\VEN_8086&DEV_7A60\\DEMO", "Universal Serial Bus controllers", "USBXHCI", 0),
            Pnp("Intel Ethernet Controller I226-V", "Intel", "PCI\\VEN_8086&DEV_125C\\DEMO", "Network adapters", "e2fnexpress", 0),
            Pnp("Intel Wi-Fi 6E AX211 160MHz", "Intel", "PCI\\VEN_8086&DEV_51F0\\DEMO", "Network adapters", "Netwtw14", 0),
            Pnp("Logitech BRIO", "Logitech", "USB\\VID_046D&PID_085E\\DEMO0001", "Cameras", "usbvideo", 0),
            Pnp("HID Keyboard Device", "Microsoft", "HID\\VID_1B1C&PID_1B49&MI_00\\DEMO", "Keyboards", "kbdhid", 0),
            Pnp("HID-compliant mouse", "Microsoft", "HID\\VID_046D&PID_C547&MI_01\\DEMO", "Mice and other pointing devices", "mouhid", 0),
            Pnp("Generic PnP Monitor", "Microsoft", "DISPLAY\\AUS27B1\\DEMO", "Monitors", "monitor", 0),
            Pnp("Generic PnP Monitor", "Microsoft", "DISPLAY\\DELA0C4\\DEMO", "Monitors", "monitor", 0),
            Pnp("Trusted Platform Module 2.0", "Microsoft", "ACPI\\MSFT0101\\DEMO", "Security devices", "TPM", 0),
            Pnp("Microsoft Print to PDF", "Microsoft", "SWD\\PRINTENUM\\DEMO", "Print queues", null, 0),
            Pnp("ASUS Aura RGB Controller", "ASUS", "HID\\VID_0B05&PID_19AF\\DEMO", "Human Interface Devices", "HidUsb", 0),
            Pnp("Intel(R) Serial IO I2C Host Controller", "Intel", "PCI\\VEN_8086&DEV_7A4C\\DEMO", "System devices", "iaLPSS2_I2C", 22),
        ]);
        return inventory;
    }

    private static MemoryModuleInfo DemoModule(string locator, string bank, string tag, string serial) =>
        new($"{locator} 16 GB DDR5", "G.Skill", tag, "OK", bank, locator, DemoData.MemoryModuleBytes, 6000, 6000, "DDR5", "DIMM", "F5-6000J3636F16G", serial, 1100, 64, 64,
            Props(("Tag", tag), ("DeviceLocator", locator), ("BankLabel", bank), ("Capacity", DemoData.MemoryModuleBytes.ToString()), ("Speed", "6000"), ("ConfiguredClockSpeed", "6000"),
                ("Manufacturer", "04CD"), ("PartNumber", "F5-6000J3636F16G"), ("SerialNumber", serial), ("SMBIOSMemoryType", "34"), ("MemoryType", "0"), ("FormFactor", "8"),
                ("ConfiguredVoltage", "1100"), ("MinVoltage", "1100"), ("MaxVoltage", "1100"), ("DataWidth", "64"), ("TotalWidth", "64"), ("TypeDetail", "128"), ("Status", "OK")));

    private static PnpDeviceInfo Pnp(string name, string? manufacturer, string deviceId, string pnpClass, string? service, int problem) =>
        new(name, manufacturer, deviceId, problem == 0 ? "OK" : "Error", pnpClass, null, service, name, problem, true, [deviceId.Split('\\').Length > 1 ? string.Join('\\', deviceId.Split('\\').Take(2)) : deviceId],
            Props(("Name", name), ("DeviceID", deviceId), ("PNPClass", pnpClass), ("ConfigManagerErrorCode", problem.ToString()), ("Present", "Yes"), ("Status", problem == 0 ? "OK" : "Error")));

    private static Dictionary<string, string> Props(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs)
        {
            d[k] = v;
        }

        return d;
    }
}
