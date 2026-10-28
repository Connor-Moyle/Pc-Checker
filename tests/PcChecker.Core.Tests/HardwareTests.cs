using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using Xunit;

namespace PcChecker.Core.Tests;

public class HardwareMappingsTests
{
    [Theory]
    [InlineData(34, null, "DDR5")]
    [InlineData(26, null, "DDR4")]
    [InlineData(24, null, "DDR3")]
    [InlineData(35, null, "LPDDR5")]
    [InlineData(0, 26, "DDR4")]
    [InlineData(0, 24, "DDR3")]
    [InlineData(null, null, null)]
    public void Memory_type_prefers_smbios_then_legacy(int? smbios, int? legacy, string? expected) =>
        Assert.Equal(expected, HardwareMappings.MemoryTypeName(smbios, legacy));

    [Fact]
    public void Memory_form_factor_and_vendor_decode()
    {
        Assert.Equal("DIMM", HardwareMappings.MemoryFormFactorName(8));
        Assert.Equal("SODIMM", HardwareMappings.MemoryFormFactorName(12));
        Assert.Null(HardwareMappings.MemoryFormFactorName(0));
        Assert.Equal("G.Skill", HardwareMappings.MemoryManufacturerName("04CD"));
        Assert.Equal("Samsung", HardwareMappings.MemoryManufacturerName("80CE"));
        Assert.Equal("Corsair", HardwareMappings.MemoryManufacturerName("Corsair"));
        Assert.Null(HardwareMappings.MemoryManufacturerName("Unknown"));
        Assert.Null(HardwareMappings.MemoryManufacturerName("  "));
    }

    [Fact]
    public void Chassis_types_map_to_text()
    {
        Assert.Equal("Tower", HardwareMappings.ChassisTypeName([7]));
        Assert.Equal("Laptop", HardwareMappings.ChassisTypeName([9]));
        Assert.Equal("Desktop", HardwareMappings.ChassisTypeName([2, 3]));
        Assert.Equal("Unknown", HardwareMappings.ChassisTypeName([1]));
        Assert.Equal("Unknown", HardwareMappings.ChassisTypeName(null));
        Assert.True(HardwareMappings.IsPortableChassis([10]));
        Assert.False(HardwareMappings.IsPortableChassis([3]));
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043&REV_A1\4&2F3A5B6C&0&0008", "NVIDIA")]
    [InlineData(@"PCI\VEN_1002&DEV_744C&SUBSYS_0E3D1002", "AMD")]
    [InlineData(@"PCI\VEN_8086&DEV_56A0", "Intel")]
    [InlineData(@"pci\ven_10de&dev_2206", "NVIDIA")]
    [InlineData(@"ROOT\BasicDisplay", "Unknown")]
    [InlineData(null, "Unknown")]
    public void Gpu_vendor_from_pnp_id(string? pnp, string expected) =>
        Assert.Equal(expected, HardwareMappings.GpuVendorFromPnpId(pnp));

    [Fact]
    public void Gpu_vendor_falls_back_to_adapter_compatibility()
    {
        Assert.Equal("AMD", HardwareMappings.GpuVendorFromPnpId(null, "Advanced Micro Devices, Inc."));
        Assert.Equal("Intel", HardwareMappings.GpuVendorFromPnpId("SWD\\x", "Intel Corporation"));
        Assert.Equal("10DE", HardwareMappings.ExtractPnpVendorId(@"PCI\VEN_10DE&DEV_2786"));
        Assert.Null(HardwareMappings.ExtractPnpVendorId("ACPI\\PNP0A08"));
    }

    [Fact]
    public void Integrated_gpu_heuristic()
    {
        Assert.True(HardwareMappings.IsIntegratedGpu("Intel(R) UHD Graphics 770", "Intel"));
        Assert.True(HardwareMappings.IsIntegratedGpu("Intel(R) Iris(R) Xe Graphics", "Intel"));
        Assert.False(HardwareMappings.IsIntegratedGpu("Intel(R) Arc(TM) A770 Graphics", "Intel"));
        Assert.True(HardwareMappings.IsIntegratedGpu("AMD Radeon(TM) Graphics", "AMD"));
        Assert.False(HardwareMappings.IsIntegratedGpu("AMD Radeon RX 7800 XT", "AMD"));
        Assert.False(HardwareMappings.IsIntegratedGpu("NVIDIA GeForce RTX 4070", "NVIDIA"));
    }

    [Fact]
    public void Vram_prefers_registry_qword_over_capped_adapter_ram()
    {
        const long FourGbCap = 4_293_918_720;
        const long TwelveGb = 12L * 1024 * 1024 * 1024;
        Assert.Equal(TwelveGb, HardwareMappings.ResolveVramBytes(FourGbCap, TwelveGb));
        Assert.Equal(FourGbCap, HardwareMappings.ResolveVramBytes(FourGbCap, null));
        Assert.Equal(FourGbCap, HardwareMappings.ResolveVramBytes(FourGbCap, 0));
        Assert.Null(HardwareMappings.ResolveVramBytes(0, null));
        Assert.Null(HardwareMappings.ResolveVramBytes(null, null));
    }

    [Fact]
    public void Pnp_id_matches_registry_matching_device_id()
    {
        const string pnp = @"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043&REV_A1\4&2F3A5B6C&0&0008";
        Assert.True(HardwareMappings.PnpIdMatches(pnp, @"pci\ven_10de&dev_2786&subsys_88ab1043"));
        Assert.True(HardwareMappings.PnpIdMatches(pnp, @"PCI\VEN_10DE&DEV_2786"));
        Assert.False(HardwareMappings.PnpIdMatches(pnp, @"PCI\VEN_8086&DEV_4680&SUBSYS_86941043"));
        Assert.False(HardwareMappings.PnpIdMatches(null, "x"));
    }

    [Fact]
    public void Monitor_strings_decode_from_ushort_arrays()
    {
        var units = "ROG PG279QM".Select(c => (object?)(ushort)c).Concat([(object?)(ushort)0, (object?)(ushort)65]).ToList();
        Assert.Equal("ROG PG279QM", HardwareMappings.DecodeMonitorString(units));
        Assert.Equal("AUS", HardwareMappings.DecodeMonitorString(new object?[] { (ushort)'A', (ushort)'U', (ushort)'S', (ushort)0 }));
        Assert.Null(HardwareMappings.DecodeMonitorString(new object?[] { (ushort)0 }));
        Assert.Null(HardwareMappings.DecodeMonitorString(null));
        Assert.Equal("ASUS", HardwareMappings.MonitorManufacturerName("AUS"));
        Assert.Equal("Dell", HardwareMappings.MonitorManufacturerName("DEL"));
        Assert.Equal("ZZZ", HardwareMappings.MonitorManufacturerName("ZZZ"));
    }

    [Fact]
    public void Diagonal_from_edid_size()
    {
        Assert.Equal(27.2, HardwareMappings.DiagonalInches(60, 34)!.Value, 1);
        Assert.Null(HardwareMappings.DiagonalInches(0, 34));
        Assert.Null(HardwareMappings.DiagonalInches(null, null));
    }

    [Fact]
    public void Storage_codes_map_to_text()
    {
        Assert.Equal("SSD", HardwareMappings.StorageMediaTypeName(4));
        Assert.Equal("HDD", HardwareMappings.StorageMediaTypeName(3));
        Assert.Equal("NVMe", HardwareMappings.StorageBusTypeName(17));
        Assert.Equal("SATA", HardwareMappings.StorageBusTypeName(11));
        Assert.Equal("USB", HardwareMappings.StorageBusTypeName(7));
        Assert.Equal("Healthy", HardwareMappings.StorageHealthName(0));
        Assert.Equal("Unhealthy", HardwareMappings.StorageHealthName(2));
        Assert.Equal("SSD", HardwareMappings.GuessMediaType("Samsung SSD 990 PRO", "Fixed hard disk media", null));
        Assert.Equal("HDD", HardwareMappings.GuessMediaType("WDC WD40EZAZ", "Fixed hard disk media", null));
        Assert.Equal("Local disk", HardwareMappings.DriveTypeName(3));
        Assert.Equal("CD/DVD drive", HardwareMappings.DriveTypeName(5));
    }

    [Theory]
    [InlineData("Intel Wi-Fi 6E AX211 160MHz", @"PCI\VEN_8086&DEV_51F0", "Wi-Fi")]
    [InlineData("Intel Ethernet Controller I226-V", @"PCI\VEN_8086&DEV_125C", "Ethernet")]
    [InlineData("Bluetooth Device (Personal Area Network)", @"BTH\MS_BTHPAN", "Bluetooth")]
    [InlineData("Hyper-V Virtual Ethernet Adapter", @"ROOT\VMS_MP\0000", "Virtual")]
    [InlineData("WAN Miniport (IP)", @"SWD\MSRRAS\MS_NDISWANIP", "Virtual")]
    [InlineData("Realtek USB GbE Family Controller", @"USB\VID_0BDA&PID_8153", "Ethernet")]
    public void Network_adapter_kind(string name, string pnp, string expected) =>
        Assert.Equal(expected, HardwareMappings.NetworkAdapterKind(name, pnp));

    [Fact]
    public void Misc_codes()
    {
        Assert.Equal("Connected", HardwareMappings.NetConnectionStatusName(2));
        Assert.Equal("Media disconnected", HardwareMappings.NetConnectionStatusName(7));
        Assert.Equal("x64", HardwareMappings.ProcessorArchitectureName(9));
        Assert.Equal("ARM64", HardwareMappings.ProcessorArchitectureName(12));
        Assert.Equal("Desktop", HardwareMappings.PcSystemTypeName(1));
        Assert.Equal("Lithium-ion", HardwareMappings.BatteryChemistryName(6));
        Assert.Equal("Charging", HardwareMappings.BatteryStatusName(6));
        Assert.Equal(12.5, HardwareMappings.BatteryWearPercent(80_000, 70_000));
        Assert.Equal(0, HardwareMappings.BatteryWearPercent(80_000, 85_000));
        Assert.Null(HardwareMappings.BatteryWearPercent(null, 70_000));
    }

    [Fact]
    public void Microcode_revision_reads_bytes_four_to_seven_little_endian()
    {
        Assert.Equal("0x12B", HardwareMappings.MicrocodeRevisionHex([0, 0, 0, 0, 0x2B, 0x01, 0, 0]));
        Assert.Equal("0x1", HardwareMappings.MicrocodeRevisionHex([1, 0]));
        Assert.Null(HardwareMappings.MicrocodeRevisionHex(null));
        Assert.Null(HardwareMappings.MicrocodeRevisionHex([]));
    }

    [Theory]
    [InlineData("ROG STRIX Z790-E GAMING WIFI", null, "Intel Z790")]
    [InlineData("MAG B650 TOMAHAWK WIFI", null, "AMD B650")]
    [InlineData("B760M DS3H", null, "Intel B760")]
    [InlineData("X670E AORUS MASTER", null, "AMD X670E")]
    [InlineData("PRIME X299-DELUXE", null, "Intel X299")]
    [InlineData("TUF GAMING B550-PLUS", "GenuineIntel", "Intel B550")]
    [InlineData("Some board", null, null)]
    [InlineData(null, null, null)]
    public void Chipset_guess_from_product_name(string? product, string? cpuVendor, string? expected) =>
        Assert.Equal(expected, HardwareMappings.GuessChipset(product, cpuVendor));

    [Fact]
    public void Wmi_reference_key_extraction()
    {
        Assert.Equal("C:", HardwareMappings.ExtractWmiKey(@"\\.\root\cimv2:Win32_LogicalDisk.DeviceID=""C:"""));
        Assert.Equal("Disk #0, Partition #1", HardwareMappings.ExtractWmiKey(@"\\DESKTOP\root\cimv2:Win32_DiskPartition.DeviceID=""Disk #0, Partition #1"""));
        Assert.Equal(@"USB\VID_046D&PID_085E\5&1A2B", HardwareMappings.ExtractWmiKey(@"\\.\root\cimv2:Win32_PnPEntity.DeviceID=""USB\\VID_046D&PID_085E\\5&1A2B"""));
        Assert.Null(HardwareMappings.ExtractWmiKey("nonsense"));
        Assert.Null(HardwareMappings.ExtractWmiKey(null));
    }
}

public class HardwareFormattingTests
{
    private static readonly HardwareInventory Demo = DemoHardwareInventoryService.Build();

    [Fact]
    public void Cpu_fields_are_human_labelled()
    {
        var fields = HardwareFieldFormatter.Fields(Demo.Processors[0]).ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal("16 (24 threads)", fields["Cores"]);
        Assert.Equal("5.4 GHz", fields["Max clock"]);
        Assert.Equal("30 MB", fields["L3 cache"]);
        Assert.Equal("Enabled in firmware", fields["Virtualization"]);
        Assert.Equal("OK", fields["Status"]);
    }

    [Fact]
    public void Gpu_memory_and_drive_fields()
    {
        var gpu = HardwareFieldFormatter.Fields(Demo.Graphics[0]).ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal("12.0 GB", gpu["Video memory"]);
        Assert.Equal("2560 x 1440 @ 240 Hz", gpu["Current mode"]);
        Assert.StartsWith("Dedicated", gpu["Type"]);

        var ram = HardwareFieldFormatter.Fields(Demo.MemoryModules[0]).ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal("DDR5 DIMM", ram["Type"]);
        Assert.Equal("6000 MT/s", ram["Configured speed"]);
        Assert.Equal("1.10 V", ram["Voltage"]);

        var drive = HardwareFieldFormatter.Fields(Demo.LogicalDrives[0]).ToDictionary(f => f.Key, f => f.Value);
        Assert.Contains("free", drive["Free space"]);
        Assert.Equal("NTFS", drive["File system"]);

        var disk = HardwareFieldFormatter.Fields(Demo.PhysicalDisks[0]).ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal("NVMe", disk["Connection"]);
        Assert.Contains("C:", disk["Partitions"]);
    }

    [Fact]
    public void Subtitles_and_status_classification()
    {
        Assert.Contains("16 cores / 24 threads", HardwareFieldFormatter.Subtitle(Demo.Processors[0]));
        Assert.Equal(HealthStatus.Good, HardwareFieldFormatter.StatusOf(Demo.Processors[0]));
        Assert.Equal(HealthStatus.Warning, HardwareFieldFormatter.StatusOf(Demo.PhysicalDisks[1]));
        var broken = Demo.PnpDevices.First(p => p.ConfigManagerErrorCode == 28);
        Assert.Equal(HealthStatus.Critical, HardwareFieldFormatter.StatusOf(broken));
        Assert.Contains("code 28", HardwareFieldFormatter.Subtitle(broken));
        Assert.Equal(HealthStatus.Unknown, HardwareFieldFormatter.StatusOf(Demo.System!));
        Assert.Contains("drivers are not installed", HardwareFieldFormatter.ProblemText(28));
    }

    [Fact]
    public void Unit_formatters()
    {
        Assert.Equal("2.5 Gbit/s", HardwareFieldFormatter.BitsPerSecond(2_500_000_000));
        Assert.Equal("100 Mbit/s", HardwareFieldFormatter.BitsPerSecond(100_000_000));
        Assert.Null(HardwareFieldFormatter.BitsPerSecond(0));
        Assert.Equal("800 MHz", HardwareFieldFormatter.Mhz(800));
        Assert.Equal("2 MB", HardwareFieldFormatter.Kb(2048));
        Assert.Equal("512 KB", HardwareFieldFormatter.Kb(512));
        Assert.Equal("57.5 Wh", HardwareFieldFormatter.Mwh(57_500));
        Assert.Equal("1920 x 1080", HardwareFieldFormatter.Mode(1920, 1080, null));
        Assert.Null(HardwareFieldFormatter.Mode(0, 1080, 60));
        Assert.Equal("24H2 · build 26100.3037", HardwareFieldFormatter.VersionText(Demo.OperatingSystem!));
    }

    [Fact]
    public void Overview_summarises_headline_parts()
    {
        var o = HardwareSummary.Overview(Demo);
        Assert.Equal(DemoData.CpuName, o.Cpu);
        Assert.Equal(DemoData.GpuName, o.Gpu);
        Assert.Equal("32.0 GB", o.Memory);
        Assert.Contains("2 of 4 slots used", o.MemoryDetail);
        Assert.Contains("DDR5", o.MemoryDetail);
        // NVMe + SATA SSD; the internal and USB hard disks and the two pool drives; the storage space.
        Assert.Contains("2 SSD", o.StorageDetail);
        Assert.Contains("4 HDD", o.StorageDetail);
        Assert.Equal(DemoData.OsName, o.Os);
        Assert.Equal(4, HardwareSummary.MemorySlots(Demo));
        Assert.Null(HardwareSummary.MemorySlots(new HardwareInventory()));
        Assert.Equal("Unknown processor", HardwareSummary.Overview(new HardwareInventory()).Cpu);
    }

    [Fact]
    public void Summary_text_lists_every_major_part()
    {
        var text = HardwareSummary.BuildText(Demo);
        Assert.Contains("Processor: " + DemoData.CpuName, text);
        Assert.Contains("Graphics: " + DemoData.GpuName, text);
        Assert.Contains("Motherboard:", text);
        Assert.Contains("Intel Z790", text);
        Assert.Contains("DIMM_A2", text);
        Assert.Contains("Devices with problems: 2", text);
        Assert.Contains("Unknown device (code 28", text);
        Assert.DoesNotContain("Scan notes", text);
    }

    [Fact]
    public void Json_round_trip_keeps_typed_fields_and_raw_properties()
    {
        var json = HardwareInventoryJson.Serialize(Demo);
        Assert.Contains("\"processors\"", json);
        Assert.Contains("13700K", json);
        var back = HardwareInventoryJson.TryDeserialize(json);
        Assert.NotNull(back);
        Assert.Equal(Demo.Processors[0].Name, back.Processors[0].Name);
        Assert.Equal(Demo.Graphics[0].AdapterRamBytes, back.Graphics[0].AdapterRamBytes);
        Assert.Equal(Demo.PhysicalDisks[0].PartitionCount, back.PhysicalDisks[0].PartitionCount);
        Assert.Equal("16", back.Processors[0].Get("NumberOfCores"));
        Assert.Equal(Demo.TotalMemoryBytes, back.TotalMemoryBytes);
        Assert.Null(HardwareInventoryJson.TryDeserialize("{ not json"));
        Assert.Null(HardwareInventoryJson.TryDeserialize(""));
    }

    [Fact]
    public void Demo_inventory_is_rich_enough_for_the_page()
    {
        Assert.True(Demo.System!.Properties.Count >= 15);
        Assert.True(Demo.Processors[0].Properties.Count >= 20);
        Assert.All(Demo.AllItems(), item => Assert.False(string.IsNullOrWhiteSpace(item.Name)));
        Assert.Contains(Demo.PnpDevices, p => p.ConfigManagerErrorCode is > 0);
        Assert.Equal(2, Demo.Monitors.Count);
        Assert.Equal("Wi-Fi", Demo.NetworkAdapters[1].AdapterType);
    }
}
