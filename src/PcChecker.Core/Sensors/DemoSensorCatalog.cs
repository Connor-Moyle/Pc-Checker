using PcChecker.Core.Common;

namespace PcChecker.Core.Sensors;

/// <summary>One simulated sensor: a slow sine wave around a base value plus a little noise.</summary>
public sealed record DemoSensorSpec(
    string Id,
    string HardwareName,
    HardwareKind Hardware,
    SensorKind Kind,
    string Name,
    double Base,
    double Amplitude,
    double Noise,
    double PeriodSeconds,
    double Phase,
    string Unit,
    int Decimals)
{
    /// <summary>Value at <paramref name="seconds"/> into the session; <paramref name="unit"/> is a −1..1 noise sample.</summary>
    public double ValueAt(double seconds, double unit)
    {
        double wave = Amplitude * Math.Sin(2 * Math.PI * seconds / Math.Max(1, PeriodSeconds) + Phase);
        double value = Base + wave + Noise * unit;
        if (Kind is SensorKind.Load or SensorKind.Control or SensorKind.Level)
        {
            value = Math.Clamp(value, 0, 100);
        }
        else if (Kind != SensorKind.Voltage)
        {
            value = Math.Max(0, value);
        }

        return Math.Round(value, Decimals);
    }
}

/// <summary>The sensors of the sample gaming desktop every Demo* service describes.</summary>
public static class DemoSensorCatalog
{
    private const string Chip = "Nuvoton NCT6798D";
    private const string Nic = "Intel Ethernet Controller I226-V";
    private const string Ram = "Generic Memory";

    public static IReadOnlyList<DemoSensorSpec> All { get; } = Build();

    /// <summary>Fan ids the demo Fans module pairs with (<c>fan.&lt;name&gt;.rpm</c> / <c>fan.&lt;name&gt;.control</c>).</summary>
    public static IReadOnlyList<string> FanIds { get; } = ["fan.cpu", "fan.case.front", "fan.case.rear", "fan.gpu"];

    private static List<DemoSensorSpec> Build()
    {
        var list = new List<DemoSensorSpec>();
        string cpu = DemoData.CpuName, gpu = DemoData.GpuName;

        // Processor
        Temp(list, "cpu.package", cpu, HardwareKind.Cpu, "CPU Package", 58, 4, 0.6, 95, 0.0);
        Temp(list, "cpu.core.max", cpu, HardwareKind.Cpu, "Core Max", 62, 4, 0.8, 95, 0.1);
        Temp(list, "cpu.core.average", cpu, HardwareKind.Cpu, "Core Average", 55, 3.5, 0.5, 95, 0.1);
        for (int i = 1; i <= 8; i++)
        {
            Temp(list, $"cpu.core.{i}", cpu, HardwareKind.Cpu, $"CPU Core #{i}", 50 + i, 3.5, 0.9, 80 + i * 4, i * 0.6);
        }

        Add(list, "cpu.load", cpu, HardwareKind.Cpu, SensorKind.Load, "CPU Total", 14, 7, 2, 60, 0.3, "%", 1);
        Add(list, "cpu.load.max", cpu, HardwareKind.Cpu, SensorKind.Load, "CPU Core Max", 46, 15, 6, 45, 1.2, "%", 1);
        Add(list, "cpu.clock.p", cpu, HardwareKind.Cpu, SensorKind.Clock, "CPU Core #1", 5300, 80, 20, 60, 0.3, "MHz", 0);
        Add(list, "cpu.clock.e", cpu, HardwareKind.Cpu, SensorKind.Clock, "CPU Core #9 (E-core)", 4200, 60, 15, 60, 0.9, "MHz", 0);
        Add(list, "cpu.bus", cpu, HardwareKind.Cpu, SensorKind.Clock, "Bus Speed", 100, 0, 0, 60, 0, "MHz", 1);
        Add(list, "cpu.power", cpu, HardwareKind.Cpu, SensorKind.Power, "CPU Package", 48, 8, 1.5, 95, 0.0, "W", 1);
        Add(list, "cpu.power.cores", cpu, HardwareKind.Cpu, SensorKind.Power, "CPU Cores", 37, 7, 1.2, 95, 0.0, "W", 1);
        Add(list, "cpu.voltage", cpu, HardwareKind.Cpu, SensorKind.Voltage, "CPU Core", 1.21, 0.03, 0.005, 60, 0.3, "V", 3);

        // Graphics card
        Temp(list, "gpu.core", gpu, HardwareKind.Gpu, "GPU Core", 46, 3, 0.4, 120, 0.5);
        Temp(list, "gpu.hotspot", gpu, HardwareKind.Gpu, "GPU Hot Spot", 58, 3, 0.5, 120, 0.5);
        Temp(list, "gpu.memory.temp", gpu, HardwareKind.Gpu, "GPU Memory Junction", 52, 2, 0.4, 120, 0.8);
        Add(list, "gpu.load", gpu, HardwareKind.Gpu, SensorKind.Load, "GPU Core", 9, 5, 2, 50, 1.1, "%", 0);
        Add(list, "gpu.load.memory", gpu, HardwareKind.Gpu, SensorKind.Load, "GPU Memory Controller", 6, 3, 1, 50, 1.4, "%", 0);
        Add(list, "gpu.clock", gpu, HardwareKind.Gpu, SensorKind.Clock, "GPU Core", 2505, 25, 8, 50, 1.1, "MHz", 0);
        Add(list, "gpu.memclock", gpu, HardwareKind.Gpu, SensorKind.Clock, "GPU Memory", 10501, 0, 0, 60, 0, "MHz", 0);
        Add(list, "gpu.vram", gpu, HardwareKind.Gpu, SensorKind.Data, "GPU Memory Used", 2140, 60, 10, 200, 0.2, "MB", 0);
        Add(list, "gpu.vram.total", gpu, HardwareKind.Gpu, SensorKind.Data, "GPU Memory Total", 12282, 0, 0, 60, 0, "MB", 0);
        Add(list, "gpu.power", gpu, HardwareKind.Gpu, SensorKind.Power, "GPU Power", 42, 6, 1.2, 120, 0.5, "W", 1);
        Add(list, "gpu.voltage", gpu, HardwareKind.Gpu, SensorKind.Voltage, "GPU Core", 0.885, 0.015, 0.003, 50, 1.1, "V", 3);
        Add(list, "fan.gpu.rpm", gpu, HardwareKind.Gpu, SensorKind.Fan, "GPU Fan 1", 1180, 25, 6, 120, 0.5, "RPM", 0);
        Add(list, "fan.gpu.control", gpu, HardwareKind.Gpu, SensorKind.Control, "GPU Fan 1", 46, 1, 0, 120, 0.5, "%", 0);

        // Motherboard sensor chip
        Temp(list, "mb.vrm", Chip, HardwareKind.Motherboard, "VRM", 44, 1.5, 0.3, 150, 0.2);
        Temp(list, "mb.chipset", Chip, HardwareKind.Motherboard, "Chipset", 49, 1, 0.3, 150, 0.4);
        Temp(list, "mb.system", Chip, HardwareKind.Motherboard, "Motherboard", 38, 1, 0.2, 150, 0.6);
        Temp(list, "mb.tsensor", Chip, HardwareKind.Motherboard, "T_Sensor (case air)", 33, 1, 0.2, 150, 0.8);
        Add(list, "fan.cpu.rpm", Chip, HardwareKind.Motherboard, SensorKind.Fan, "CPU Fan", 820, 25, 6, 95, 0.0, "RPM", 0);
        Add(list, "fan.cpu.control", Chip, HardwareKind.Motherboard, SensorKind.Control, "CPU Fan", 42, 1, 0, 95, 0.0, "%", 0);
        Add(list, "fan.case.front.rpm", Chip, HardwareKind.Motherboard, SensorKind.Fan, "Chassis Fan #1", 640, 15, 5, 150, 0.6, "RPM", 0);
        Add(list, "fan.case.front.control", Chip, HardwareKind.Motherboard, SensorKind.Control, "Chassis Fan #1", 35, 0, 0, 150, 0, "%", 0);
        Add(list, "fan.case.rear.rpm", Chip, HardwareKind.Motherboard, SensorKind.Fan, "Chassis Fan #2", 655, 15, 5, 150, 0.9, "RPM", 0);
        Add(list, "fan.case.rear.control", Chip, HardwareKind.Motherboard, SensorKind.Control, "Chassis Fan #2", 38, 0, 0, 150, 0, "%", 0);
        Add(list, "mb.voltage.vcore", Chip, HardwareKind.Motherboard, SensorKind.Voltage, "Vcore", 1.208, 0.02, 0.004, 60, 0.3, "V", 3);
        Add(list, "mb.voltage.12v", Chip, HardwareKind.Motherboard, SensorKind.Voltage, "+12V", 12.10, 0.03, 0.01, 200, 0, "V", 3);
        Add(list, "mb.voltage.5v", Chip, HardwareKind.Motherboard, SensorKind.Voltage, "+5V", 5.02, 0.01, 0.005, 200, 0.5, "V", 3);
        Add(list, "mb.voltage.3v", Chip, HardwareKind.Motherboard, SensorKind.Voltage, "+3.3V", 3.33, 0.01, 0.004, 200, 1.0, "V", 3);
        Add(list, "mb.voltage.dram", Chip, HardwareKind.Motherboard, SensorKind.Voltage, "DRAM", 1.35, 0.004, 0.002, 200, 1.5, "V", 3);

        // Memory
        Add(list, "ram.used", Ram, HardwareKind.Memory, SensorKind.Data, "Memory Used", 13.4, 0.3, 0.05, 240, 0.1, "GB", 1);
        Add(list, "ram.available", Ram, HardwareKind.Memory, SensorKind.Data, "Memory Available", 18.4, 0.3, 0.05, 240, 0.1 + Math.PI, "GB", 1);
        Add(list, "ram.load", Ram, HardwareKind.Memory, SensorKind.Load, "Memory", 42, 1, 0.2, 240, 0.1, "%", 1);
        Temp(list, "ram.temp.a2", DemoData.MemoryKit, HardwareKind.Memory, "DIMM A2", 41, 1, 0.2, 180, 0.3);
        Temp(list, "ram.temp.b2", DemoData.MemoryKit, HardwareKind.Memory, "DIMM B2", 42, 1, 0.2, 180, 0.7);

        // Storage
        Temp(list, "nvme.temp", DemoData.NvmeModel, HardwareKind.Storage, "Temperature", 41, 1.5, 0.3, 200, 0.2);
        Temp(list, "nvme.temp.2", DemoData.NvmeModel, HardwareKind.Storage, "Temperature 2 (controller)", 48, 2, 0.3, 200, 0.2);
        Add(list, "nvme.space", DemoData.NvmeModel, HardwareKind.Storage, SensorKind.Load, "Used Space", 54.2, 0, 0, 60, 0, "%", 1);
        Add(list, "nvme.life", DemoData.NvmeModel, HardwareKind.Storage, SensorKind.Level, "Remaining Life", 97, 0, 0, 60, 0, "%", 0);
        Add(list, "nvme.read", DemoData.NvmeModel, HardwareKind.Storage, SensorKind.Throughput, "Read Rate", 1.4, 1.2, 0.6, 30, 0.2, "MB/s", 1);
        Add(list, "nvme.write", DemoData.NvmeModel, HardwareKind.Storage, SensorKind.Throughput, "Write Rate", 0.7, 0.5, 0.3, 30, 1.5, "MB/s", 1);
        Add(list, "nvme.data.read", DemoData.NvmeModel, HardwareKind.Storage, SensorKind.Data, "Data Read", 14820, 0, 0, 60, 0, "GB", 0);
        Add(list, "nvme.data.written", DemoData.NvmeModel, HardwareKind.Storage, SensorKind.Data, "Data Written", 9360, 0, 0, 60, 0, "GB", 0);
        Temp(list, "hdd.temp", DemoData.HddModel, HardwareKind.Storage, "Temperature", 36, 0.5, 0.2, 300, 0.4);
        Add(list, "hdd.space", DemoData.HddModel, HardwareKind.Storage, SensorKind.Load, "Used Space", 71.6, 0, 0, 60, 0, "%", 1);
        Add(list, "hdd.read", DemoData.HddModel, HardwareKind.Storage, SensorKind.Throughput, "Read Rate", 0.1, 0.1, 0.05, 40, 0.1, "MB/s", 2);
        Add(list, "hdd.write", DemoData.HddModel, HardwareKind.Storage, SensorKind.Throughput, "Write Rate", 0.05, 0.05, 0.02, 40, 0.8, "MB/s", 2);

        // Network
        Add(list, "net.down", Nic, HardwareKind.Network, SensorKind.Throughput, "Download Speed", 1.8, 1.5, 0.4, 25, 0.0, "MB/s", 2);
        Add(list, "net.up", Nic, HardwareKind.Network, SensorKind.Throughput, "Upload Speed", 0.3, 0.2, 0.1, 25, 1.0, "MB/s", 2);
        Add(list, "net.load", Nic, HardwareKind.Network, SensorKind.Load, "Network Utilization", 0.7, 0.5, 0.1, 25, 0.0, "%", 1);

        return list;
    }

    private static void Temp(List<DemoSensorSpec> list, string id, string hardwareName, HardwareKind hardware, string name,
        double baseValue, double amplitude, double noise, double period, double phase) =>
        list.Add(new DemoSensorSpec(id, hardwareName, hardware, SensorKind.Temperature, name, baseValue, amplitude, noise, period, phase, "°C", 1));

    private static void Add(List<DemoSensorSpec> list, string id, string hardwareName, HardwareKind hardware, SensorKind kind, string name,
        double baseValue, double amplitude, double noise, double period, double phase, string unit, int decimals) =>
        list.Add(new DemoSensorSpec(id, hardwareName, hardware, kind, name, baseValue, amplitude, noise, period, phase, unit, decimals));
}
