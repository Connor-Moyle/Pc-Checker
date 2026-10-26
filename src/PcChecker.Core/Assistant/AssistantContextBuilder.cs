using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Performance;
using PcChecker.Core.Sensors;
using PcChecker.Core.Software;

namespace PcChecker.Core.Assistant;

/// <summary>
/// Builds the compact text context sent to the assistant. Pure: no Windows calls. Can redact serial numbers and MAC addresses.
/// </summary>
public static partial class AssistantContextBuilder
{
    [GeneratedRegex(@"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex MacAddress();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AssistantContext Build(HardwareInventory? hardware, HealthReport? health, PerformanceReport? performance,
        SensorSnapshot? sensors, SoftwareInventory? software, int maxChars, bool redactSerials)
    {
        // Give each section a share of the budget; hardware and health matter most.
        int budget = Math.Max(500, maxChars);
        string? hw = Trim(SummarizeHardware(hardware, redactSerials), budget * 35 / 100);
        string? he = Trim(SummarizeHealth(health, redactSerials), budget * 25 / 100);
        string? pf = Trim(SummarizePerformance(performance), budget * 20 / 100);
        string? se = Trim(SummarizeSensors(sensors), budget * 10 / 100);
        string? sw = Trim(SummarizeSoftware(software), budget * 10 / 100);
        return new AssistantContext(hw, he, pf, se, sw);
    }

    /// <summary>JSON profile of the hardware relevant to overclocking (GPU, CPU, memory, cooling) for the advisor.</summary>
    public static string BuildHardwareProfileJson(HardwareInventory? hardware, SensorSnapshot? sensors, OverclockCapabilities? capabilities)
    {
        var cpu = hardware?.Processors.FirstOrDefault();
        var gpus = hardware?.Graphics.Where(g => !g.IsIntegrated).ToList() ?? [];
        if (gpus.Count == 0 && hardware is not null)
        {
            gpus = hardware.Graphics.ToList();
        }

        var temps = sensors?.Readings.Where(r => r.Kind == SensorKind.Temperature)
            .Select(r => new { r.HardwareName, r.Name, r.Value, r.Max }).ToList();

        var profile = new
        {
            cpu = cpu is null ? null : new { cpu.Name, cpu.Cores, cpu.LogicalProcessors, cpu.MaxClockMhz, cpu.Socket, cpu.Manufacturer },
            gpus = gpus.Select(g => new { g.Name, g.Vendor, vramBytes = g.AdapterRamBytes, g.DriverVersion }).ToList(),
            memory = hardware is null ? null : new
            {
                totalBytes = hardware.TotalMemoryBytes,
                modules = hardware.MemoryModules.Select(m => new { m.CapacityBytes, m.SpeedMhz, m.ConfiguredClockSpeedMhz, m.MemoryType, m.PartNumber }).ToList(),
            },
            motherboard = hardware?.Motherboard is null ? null : new { hardware.Motherboard.Manufacturer, hardware.Motherboard.Product, hardware.Motherboard.Chipset },
            bios = hardware?.Bios is null ? null : new { hardware.Bios.Manufacturer, hardware.Bios.Version, hardware.Bios.ReleaseDate },
            system = hardware?.System is null ? null : new { hardware.System.Manufacturer, hardware.System.Model, hardware.System.PcSystemType },
            temperatures = temps,
            capabilities = capabilities is null ? null : new
            {
                capabilities.GpuVendor, capabilities.GpuMethod, capabilities.GpuLimits, capabilities.CpuMethod, capabilities.XmpState,
                capabilities.MemoryConfiguredMhz, capabilities.MemoryMaxMhz, capabilities.Notes,
            },
        };
        return JsonSerializer.Serialize(profile, JsonOptions);
    }

    /// <summary>Masks serial numbers and MAC addresses in free text.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return MacAddress().Replace(text, m => WmiPropertyMapper.Redact(m.Value, 5));
    }

    public static string? SummarizeHardware(HardwareInventory? hw, bool redactSerials)
    {
        if (hw is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        if (hw.System is not null)
        {
            sb.AppendLine($"System: {hw.System.Manufacturer} {hw.System.Model} ({hw.System.PcSystemType ?? hw.System.SystemType})");
        }

        if (hw.OperatingSystem is not null)
        {
            sb.AppendLine($"OS: {hw.OperatingSystem.Caption} {hw.OperatingSystem.DisplayVersion} build {hw.OperatingSystem.BuildNumber} {hw.OperatingSystem.Architecture}");
        }

        if (hw.Motherboard is not null)
        {
            sb.AppendLine($"Motherboard: {hw.Motherboard.Manufacturer} {hw.Motherboard.Product}" + (hw.Bios is null ? string.Empty : $", BIOS {hw.Bios.Version} ({hw.Bios.ReleaseDate:yyyy-MM-dd})"));
        }

        foreach (var c in hw.Processors)
        {
            sb.AppendLine($"CPU: {c.Name}, {c.Cores} cores / {c.LogicalProcessors} threads, up to {c.MaxClockMhz} MHz");
        }

        foreach (var g in hw.Graphics)
        {
            sb.AppendLine($"GPU: {g.Name}, {ByteSize.Format(g.AdapterRamBytes)} VRAM, driver {g.DriverVersion}{(g.IsIntegrated ? " (integrated)" : string.Empty)}");
        }

        if (hw.MemoryModules.Count > 0)
        {
            sb.AppendLine($"Memory: {ByteSize.Format(hw.TotalMemoryBytes)} total in {hw.MemoryModules.Count} modules: " +
                string.Join("; ", hw.MemoryModules.Select(m => $"{ByteSize.Format(m.CapacityBytes)} {m.MemoryType} {m.ConfiguredClockSpeedMhz ?? m.SpeedMhz} MHz")));
        }
        else if (hw.TotalMemoryBytes is { } total)
        {
            sb.AppendLine($"Memory: {ByteSize.Format(total)}");
        }

        foreach (var d in hw.PhysicalDisks)
        {
            var serial = redactSerials ? null : d.SerialNumber;
            sb.AppendLine($"Disk: {d.Model} {ByteSize.Format(d.SizeBytes)} {d.MediaType} {d.BusType}" + (serial is null ? string.Empty : $" S/N {serial}") + (d.HealthStatusText is null ? string.Empty : $" health {d.HealthStatusText}"));
        }

        foreach (var v in hw.LogicalDrives.Where(v => v.SizeBytes > 0))
        {
            sb.AppendLine($"Volume {v.Name}: {ByteSize.Format(v.FreeSpaceBytes)} free of {ByteSize.Format(v.SizeBytes)} ({v.FileSystem})");
        }

        foreach (var n in hw.NetworkAdapters.Where(n => n.PhysicalAdapter == true))
        {
            sb.AppendLine($"Network: {n.Name} {n.AdapterType}" + (redactSerials || n.MacAddress is null ? string.Empty : $" MAC {n.MacAddress}"));
        }

        foreach (var b in hw.Batteries)
        {
            sb.AppendLine($"Battery: {b.Name} {b.Chemistry} design {b.DesignCapacityMwh} mWh, full {b.FullChargeCapacityMwh} mWh");
        }

        foreach (var m in hw.Monitors)
        {
            sb.AppendLine($"Monitor: {m.Manufacturer} {m.Name} {m.ScreenWidth}x{m.ScreenHeight}");
        }

        if (hw.Warnings.Count > 0)
        {
            sb.AppendLine("Scan notes: " + string.Join("; ", hw.Warnings));
        }

        var text = sb.ToString().TrimEnd();
        return text.Length == 0 ? null : redactSerials ? Redact(text) : text;
    }

    public static string? SummarizeHealth(HealthReport? health, bool redactSerials)
    {
        if (health is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Health score: {health.Score}/100 ({HealthScoring.Describe(health.Score)})");
        foreach (var item in health.Items.Where(i => i.Status != HealthStatus.Good).Take(20))
        {
            sb.AppendLine($"- [{item.Status}] {item.Component}: {item.Title}. {item.Detail}");
        }

        foreach (var d in health.Disks)
        {
            sb.AppendLine($"- Disk {d.Model}" + (redactSerials || d.Serial is null ? string.Empty : $" ({d.Serial})") +
                $": {d.Status}, {d.TemperatureC?.ToString("0") ?? "?"} °C, {d.PowerOnHours?.ToString() ?? "?"} h, wear {d.WearPercent?.ToString("0") ?? "?"}%, reallocated {d.ReallocatedSectors?.ToString() ?? "?"}");
        }

        foreach (var b in health.Batteries)
        {
            sb.AppendLine($"- Battery {b.Name}: wear {b.WearPercent?.ToString("0") ?? "?"}%, cycles {b.CycleCount?.ToString() ?? "?"}, {b.Status}");
        }

        foreach (var p in health.DeviceProblems.Take(10))
        {
            sb.AppendLine($"- Device problem: {p.Name} code {p.ErrorCode}: {p.Description}");
        }

        if (health.SystemErrors.Count > 0)
        {
            sb.AppendLine($"- {health.SystemErrors.Count} recent system errors; top sources: " +
                string.Join(", ", health.SystemErrors.GroupBy(e => e.Source).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key} ({g.Count()})")));
        }

        if (health.CrashDumps.Count > 0)
        {
            sb.AppendLine($"- {health.CrashDumps.Count} crash dumps, newest {health.CrashDumps.Max(c => c.Time):yyyy-MM-dd}");
        }

        var text = sb.ToString().TrimEnd();
        return redactSerials ? Redact(text) : text;
    }

    public static string? SummarizePerformance(PerformanceReport? perf)
    {
        if (perf is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Performance score: {perf.Score}/100. Memory {perf.MemoryUsedPercent:0}% of {ByteSize.Format(perf.MemoryTotalBytes)} used. Power plan: {perf.ActivePowerPlan ?? "unknown"}." +
            (perf.LastBootSeconds is { } boot ? $" Last boot {boot:0} s." : string.Empty));
        foreach (var f in perf.Findings.Take(15))
        {
            sb.AppendLine($"- [{f.Severity}] {f.Title}: {f.Detail}");
        }

        int startup = perf.StartupItems.Count(s => s.Enabled);
        if (startup > 0)
        {
            sb.AppendLine($"Startup programs ({startup}): " + string.Join(", ", perf.StartupItems.Where(s => s.Enabled).Take(15).Select(s => s.Name)));
        }

        if (perf.TopProcesses.Count > 0)
        {
            sb.AppendLine("Top processes: " + string.Join(", ", perf.TopProcesses.Take(8).Select(p => $"{p.Name} {p.CpuPercent:0}% {ByteSize.Format(p.WorkingSetBytes)}")));
        }

        return sb.ToString().TrimEnd();
    }

    public static string? SummarizeSensors(SensorSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Readings.Count == 0)
        {
            return null;
        }

        var lines = snapshot.Readings
            .Where(r => r.Kind is SensorKind.Temperature or SensorKind.Load or SensorKind.Power or SensorKind.Clock or SensorKind.Fan)
            .Where(r => r.Value.HasValue)
            .GroupBy(r => r.HardwareName)
            .Select(g => g.Key + ": " + string.Join(", ", g.Take(12).Select(r => $"{r.Name} {r.Value:0.#}{r.Unit}" + (r.Max.HasValue && r.Kind == SensorKind.Temperature ? $" (max {r.Max:0})" : string.Empty))));
        return string.Join(Environment.NewLine, lines);
    }

    public static string? SummarizeSoftware(SoftwareInventory? software)
    {
        if (software is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{software.Programs.Count} programs installed.");
        var notable = software.Programs
            .Where(p => !p.IsSystemComponent)
            .OrderByDescending(p => p.SizeBytes ?? 0)
            .Take(25)
            .Select(p => p.Name + (p.Version is null ? string.Empty : " " + p.Version));
        sb.AppendLine("Largest: " + string.Join(", ", notable));
        if (software.Bundles.Count > 0)
        {
            sb.AppendLine("Bundled installs: " + string.Join("; ", software.Bundles.Take(8).Select(b => $"{b.Parent.Name} → {string.Join(", ", b.Children.Select(c => c.Name))}")));
        }

        return sb.ToString().TrimEnd();
    }

    private static string? Trim(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Length <= max)
        {
            return text;
        }

        int cut = text.LastIndexOf('\n', Math.Max(0, max - 1));
        if (cut < max / 2)
        {
            cut = max;
        }

        return text[..cut].TrimEnd() + Environment.NewLine + "…";
    }
}
