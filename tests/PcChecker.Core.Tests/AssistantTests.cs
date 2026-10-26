using PcChecker.Core.Assistant;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Performance;
using PcChecker.Core.Sensors;
using PcChecker.Core.Software;
using Xunit;

namespace PcChecker.Core.Tests;

public class AssistantContextBuilderTests
{
    private static HardwareInventory Inventory()
    {
        var props = HardwareItem.NoProperties;
        return new HardwareInventory
        {
            System = new ComputerSystemInfo("PC", "Contoso", "Model X", "x64", null, null, 32L * 1024 * 1024 * 1024, 1, 16, null, null, "Desktop", null, props),
            Processors = [new CpuInfo("Ryzen 7", "AMD", "CPU0", "OK", null, "x64", "AM5", 8, 16, 5000, 4000, null, null, null, null, null, true, null, 64, props)],
            Graphics = [new GpuInfo("GeForce RTX 4070", "NVIDIA", "GPU0", "OK", 12L * 1024 * 1024 * 1024, "551.23", null, null, null, 2560, 1440, 165, null, "NVIDIA", false, props)],
            PhysicalDisks = [new PhysicalDiskInfo("NVMe", "Samsung", "D0", "OK", "980 PRO", "S6B2NS0R123456", "1B2QGXA7", "NVMe", "SSD", "NVMe", 1_000_000_000_000, 2, "Healthy", null, null, [], props)],
            NetworkAdapters = [new NetworkAdapterInfo("Ethernet", "Intel", "N0", "OK", "AA:BB:CC:DD:EE:FF", "Ethernet 802.3", 1_000_000_000, true, "Ethernet", "Connected", [], [], [], true, null, props)],
        };
    }

    [Fact]
    public void Builds_all_sections_from_data()
    {
        var hw = Inventory();
        var health = new HealthReport(85, [new HealthItem("Disk", HealthStatus.Warning, "Warm drive", "58 °C", null, HealthItem.NoMetrics)], [], [], [], [], [], [], DateTimeOffset.Now, []);
        var perf = new PerformanceReport(70, [new SlowdownFinding("x", FindingCategory.Memory, Severity.High, "RAM high", "90%", "", [], false)], [], [], [], 30, 90, 16L * 1024 * 1024 * 1024, "Balanced", [], DateTimeOffset.Now, []);
        var sensors = new SensorSnapshot(DateTimeOffset.Now, [new SensorReading("cpu/t", "CPU", HardwareKind.Cpu, SensorKind.Temperature, "Package", 65, 40, 80, "°C")]);
        var software = new SoftwareInventory([InstalledProgram.Create("a", "Big App", sizeBytes: 5_000_000_000)], [], DateTimeOffset.Now, []);

        var ctx = AssistantContextBuilder.Build(hw, health, perf, sensors, software, 12000, redactSerials: false);
        Assert.False(ctx.IsEmpty);
        Assert.Contains("Ryzen 7", ctx.HardwareSummary);
        Assert.Contains("S6B2NS0R123456", ctx.HardwareSummary);
        Assert.Contains("AA:BB:CC:DD:EE:FF", ctx.HardwareSummary);
        Assert.Contains("85/100", ctx.HealthSummary);
        Assert.Contains("Warm drive", ctx.HealthSummary);
        Assert.Contains("RAM high", ctx.PerformanceSummary);
        Assert.Contains("Package 65", ctx.SensorSummary);
        Assert.Contains("Big App", ctx.SoftwareSummary);
    }

    [Fact]
    public void Redacts_serials_and_mac_addresses()
    {
        var ctx = AssistantContextBuilder.Build(Inventory(), null, null, null, null, 12000, redactSerials: true);
        Assert.DoesNotContain("S6B2NS0R123456", ctx.HardwareSummary);
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", ctx.HardwareSummary);
        Assert.Contains("980 PRO", ctx.HardwareSummary);
        Assert.Null(ctx.HealthSummary);
    }

    [Fact]
    public void Redact_masks_mac_in_free_text()
    {
        var text = AssistantContextBuilder.Redact("MAC 00-11-22-33-44-55 and AA:BB:CC:DD:EE:FF");
        Assert.DoesNotContain("00-11-22-33-44-55", text);
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", text);
        Assert.Contains("00-11", text);
    }

    [Fact]
    public void Respects_character_budget()
    {
        var hw = Inventory();
        for (int i = 0; i < 300; i++)
        {
            hw.PnpDevices.Add(new PnpDeviceInfo($"Device {i}", null, $"ID{i}", "OK", null, null, null, null, 0, true, [], HardwareItem.NoProperties));
            hw.UsbDevices.Add(new UsbDeviceInfo($"USB thing number {i} with a long descriptive name", "Vendor", $"USB{i}", "OK", null, null, null, null, HardwareItem.NoProperties));
        }

        var ctx = AssistantContextBuilder.Build(hw, null, null, null, null, 1000, false);
        Assert.NotNull(ctx.HardwareSummary);
        Assert.True(ctx.HardwareSummary.Length <= 1000 * 35 / 100 + 10);
    }

    [Fact]
    public void Empty_inputs_give_empty_context()
    {
        var ctx = AssistantContextBuilder.Build(null, null, null, null, null, 12000, true);
        Assert.True(ctx.IsEmpty);
        Assert.Null(AssistantContextBuilder.SummarizeSensors(SensorSnapshot.Empty));
    }

    [Fact]
    public void Hardware_profile_json_includes_gpu_cpu_and_capabilities()
    {
        var caps = new OverclockCapabilities(GpuVendor.Nvidia, "RTX", GpuControlMethod.NvApi, new GpuLimits(-200, 200, -1000, 1000, 50, 115, 60, 90, true), "CPU", CpuControlMethod.None, null, "Enabled", 6000, 6000, ["note"]);
        var json = AssistantContextBuilder.BuildHardwareProfileJson(Inventory(), null, caps);
        Assert.Contains("\"Name\":\"GeForce RTX 4070\"", json);
        Assert.Contains("\"Cores\":8", json);
        Assert.Contains("\"GpuMethod\":\"NvApi\"", json);
        Assert.Contains("\"XmpState\":\"Enabled\"", json);
        Assert.DoesNotContain("S6B2NS0R123456", json);
    }

    [Fact]
    public void Profile_json_handles_missing_data()
    {
        var json = AssistantContextBuilder.BuildHardwareProfileJson(null, null, null);
        Assert.Contains("\"gpus\":[]", json);
    }
}
