using System.Globalization;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Hardware;

/// <summary>Headline facts for the Hardware page summary strip and the "Copy summary" text.</summary>
public sealed record HardwareOverview(
    string Cpu, string? CpuDetail,
    string Gpu, string? GpuDetail,
    string Memory, string? MemoryDetail,
    string Storage, string? StorageDetail,
    string Os, string? OsDetail);

/// <summary>Pure helpers that condense a <see cref="HardwareInventory"/> into headline numbers and a text summary.</summary>
public static class HardwareSummary
{
    /// <summary>Key under <c>ComputerSystemInfo.Properties</c> where the scanner stores the number of memory slots.</summary>
    public const string MemorySlotsKey = "MemoryArray.MemoryDevices";

    /// <summary>Key under <c>ComputerSystemInfo.Properties</c> where the scanner stores the maximum memory (KB).</summary>
    public const string MemoryMaxCapacityKey = "MemoryArray.MaxCapacityEx";

    public static HardwareOverview Overview(HardwareInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var cpu = inventory.Processors.FirstOrDefault();
        var gpu = PrimaryGpu(inventory);
        var slots = MemorySlots(inventory);
        var used = inventory.MemoryModules.Count(m => m.CapacityBytes is > 0);
        var memType = inventory.MemoryModules.Select(m => m.MemoryType).FirstOrDefault(t => t is not null);
        var memSpeed = inventory.MemoryModules.Select(m => m.ConfiguredClockSpeedMhz ?? m.SpeedMhz).FirstOrDefault(s => s is > 0);
        long storageTotal = inventory.PhysicalDisks.Sum(d => d.SizeBytes ?? 0);
        var os = inventory.OperatingSystem;

        return new HardwareOverview(
            cpu?.Name ?? "Unknown processor",
            cpu is null ? null : Join(cpu.Cores is null ? null : $"{cpu.Cores} cores / {cpu.LogicalProcessors} threads", HardwareFieldFormatter.Mhz(cpu.MaxClockMhz)),
            gpu?.Name ?? "No graphics adapter found",
            gpu is null ? null : Join(HardwareFieldFormatter.Bytes(gpu.AdapterRamBytes), gpu.DriverVersion is null ? null : "driver " + gpu.DriverVersion),
            inventory.TotalMemoryBytes is { } total ? ByteSize.Format(total) : "Unknown",
            Join(memType, memSpeed is > 0 ? $"{memSpeed} MT/s" : null, slots is > 0 ? $"{used} of {slots} slots used" : used > 0 ? $"{used} module{(used == 1 ? string.Empty : "s")}" : null),
            storageTotal > 0 ? ByteSize.Format(storageTotal) : inventory.LogicalDrives.Count > 0 ? ByteSize.Format(inventory.LogicalDrives.Sum(d => d.SizeBytes ?? 0)) : "Unknown",
            StorageDetail(inventory),
            os?.Caption ?? os?.Name ?? "Unknown Windows version",
            os is null ? null : HardwareFieldFormatter.VersionText(os));
    }

    /// <summary>The dedicated GPU when there is one, otherwise the first adapter.</summary>
    public static GpuInfo? PrimaryGpu(HardwareInventory inventory) =>
        inventory.Graphics.FirstOrDefault(g => !g.IsIntegrated) ?? inventory.Graphics.FirstOrDefault();

    /// <summary>Total memory slots as recorded by the scanner, or null.</summary>
    public static int? MemorySlots(HardwareInventory inventory)
    {
        var text = inventory.System?.Get(MemorySlotsKey);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slots) && slots > 0 ? slots : null;
    }

    /// <summary>Plain-text summary suitable for the clipboard, a bug report or a chat message.</summary>
    public static string BuildText(HardwareInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var o = Overview(inventory);
        var sb = new StringBuilder();
        sb.AppendLine("PC Checker hardware summary");
        sb.AppendLine($"Captured: {inventory.Captured:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        if (inventory.System is { } s)
        {
            sb.AppendLine($"Computer: {s.Name} — {Join(s.Manufacturer, s.Model, s.ChassisType) ?? "unknown model"}");
        }

        sb.AppendLine($"Windows: {o.Os}{Suffix(o.OsDetail)}");
        if (inventory.Motherboard is { } mb)
        {
            sb.AppendLine($"Motherboard: {Join(mb.Manufacturer, mb.Product, mb.Chipset)}");
        }

        if (inventory.Bios is { } b)
        {
            sb.AppendLine($"BIOS: {Join(b.Manufacturer, b.SmBiosVersion ?? b.Version, b.ReleaseDate is { } rd ? rd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null, b.IsUefi == true ? "UEFI" : null, b.SecureBootEnabled == true ? "Secure Boot on" : b.SecureBootEnabled == false ? "Secure Boot off" : null)}");
        }

        sb.AppendLine($"Processor: {o.Cpu}{Suffix(o.CpuDetail)}");
        foreach (var g in inventory.Graphics)
        {
            sb.AppendLine($"Graphics: {g.Name}{Suffix(Join(HardwareFieldFormatter.Bytes(g.AdapterRamBytes), g.DriverVersion is null ? null : "driver " + g.DriverVersion, g.IsIntegrated ? "integrated" : null))}");
        }

        sb.AppendLine($"Memory: {o.Memory}{Suffix(o.MemoryDetail)}");
        foreach (var m in inventory.MemoryModules)
        {
            sb.AppendLine($"  - {Join(m.DeviceLocator, HardwareFieldFormatter.Bytes(m.CapacityBytes), m.MemoryType, m.ConfiguredClockSpeedMhz is > 0 ? $"{m.ConfiguredClockSpeedMhz} MT/s" : null, m.Manufacturer, m.PartNumber)}");
        }

        sb.AppendLine($"Storage: {o.Storage}{Suffix(o.StorageDetail)}");
        foreach (var d in inventory.PhysicalDisks)
        {
            sb.AppendLine($"  - {Join(d.Model ?? d.Name, HardwareFieldFormatter.Bytes(d.SizeBytes), d.MediaType, d.BusType ?? d.InterfaceType, d.HealthStatusText)}");
        }

        foreach (var l in inventory.LogicalDrives)
        {
            sb.AppendLine($"  - {l.Name} {Join(l.VolumeName, l.FileSystem, l.FreeSpaceBytes is null ? null : HardwareFieldFormatter.Bytes(l.FreeSpaceBytes) + " free of " + HardwareFieldFormatter.Bytes(l.SizeBytes))}");
        }

        foreach (var n in inventory.NetworkAdapters)
        {
            sb.AppendLine($"Network: {n.Name}{Suffix(Join(n.AdapterType, n.NetConnectionStatus, HardwareFieldFormatter.BitsPerSecond(n.SpeedBitsPerSecond)))}");
        }

        foreach (var mo in inventory.Monitors)
        {
            sb.AppendLine($"Monitor: {mo.Name}{Suffix(Join(mo.Manufacturer, mo.ScreenWidth is > 0 ? $"{mo.ScreenWidth} x {mo.ScreenHeight}" : null, mo.Get("DiagonalInches") is { } di ? di + "\"" : null))}");
        }

        foreach (var a in inventory.AudioDevices)
        {
            sb.AppendLine($"Audio: {a.Name}");
        }

        foreach (var bt in inventory.Batteries)
        {
            sb.AppendLine($"Battery: {bt.Name}{Suffix(Join(bt.EstimatedChargePercent is null ? null : bt.EstimatedChargePercent + " %", bt.BatteryStatusText, HardwareMappings.BatteryWearPercent(bt.DesignCapacityMwh, bt.FullChargeCapacityMwh) is { } w ? $"{w:0.#} % wear" : null))}");
        }

        foreach (var i in inventory.InputDevices)
        {
            sb.AppendLine($"{i.Kind}: {i.Name}");
        }

        var problems = inventory.PnpDevices.Where(p => p.ConfigManagerErrorCode is > 0).ToList();
        if (problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Devices with problems: {problems.Count}");
            foreach (var p in problems.Take(20))
            {
                sb.AppendLine($"  - {p.Name} (code {p.ConfigManagerErrorCode}: {HardwareFieldFormatter.ProblemText(p.ConfigManagerErrorCode!.Value)})");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Devices listed: {inventory.AllItems().Count()} (USB {inventory.UsbDevices.Count}, Plug and Play {inventory.PnpDevices.Count})");
        if (inventory.Warnings.Count > 0)
        {
            sb.AppendLine("Scan notes:");
            foreach (var w in inventory.Warnings)
            {
                sb.AppendLine("  - " + w);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string? StorageDetail(HardwareInventory inventory)
    {
        if (inventory.PhysicalDisks.Count == 0)
        {
            return inventory.LogicalDrives.Count == 0 ? null : $"{inventory.LogicalDrives.Count} drive{(inventory.LogicalDrives.Count == 1 ? string.Empty : "s")}";
        }

        int ssd = inventory.PhysicalDisks.Count(d => d.MediaType is not null && d.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase));
        int hdd = inventory.PhysicalDisks.Count(d => d.MediaType is not null && d.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase));
        int other = inventory.PhysicalDisks.Count - ssd - hdd;
        var parts = new List<string>();
        if (ssd > 0) parts.Add($"{ssd} SSD");
        if (hdd > 0) parts.Add($"{hdd} HDD");
        if (other > 0) parts.Add($"{other} other");
        var free = inventory.LogicalDrives.Sum(d => d.FreeSpaceBytes ?? 0);
        return Join(string.Join(", ", parts), free > 0 ? ByteSize.Format(free) + " free" : null);
    }

    private static string Suffix(string? detail) => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";

    private static string? Join(params string?[] parts)
    {
        var filtered = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return filtered.Length == 0 ? null : string.Join(" · ", filtered);
    }
}
