using System.Globalization;
using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Hardware;
using PcChecker.Core.Localization;
using PcChecker.Core.Memory;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.RamTuning;

/// <summary>
/// Builds <see cref="RamFacts"/> from what the other modules already read: the Win32_PhysicalMemory modules (shared
/// <see cref="MemoryModuleInfo"/>), the processor name, the "Your PC" platform info from the Drivers page and the
/// sensor readings. Uses the shared XMP verdict (<see cref="XmpStateEvaluator"/>). Pure.
/// </summary>
public static class RamFactsBuilder
{
    public static RamFacts Build(
        IReadOnlyList<MemoryModuleInfo> modules,
        int slotCount,
        string? cpuName,
        PcPlatformInfo? platformInfo,
        IEnumerable<SensorReading>? readings,
        DateTimeOffset now,
        Translator? tr = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        tr ??= Translator.English;
        var warnings = new List<string>();
        string? memoryType = modules.Select(m => NormalizeType(m.MemoryType)).FirstOrDefault(t => t is not null);
        var ramModules = modules.Select(m => ToModule(m, memoryType, tr)).ToList();
        if (ramModules.Count == 0)
        {
            warnings.Add(tr.T("Windows did not list the memory modules (Win32_PhysicalMemory)."));
        }

        int slots = Math.Max(slotCount, ramModules.Count);
        var xmp = XmpStateEvaluator.Evaluate(modules.Select(m => new ModuleSpeed(m.ConfiguredClockSpeedMhz, m.SpeedMhz, m.PartNumber)), memoryType);
        var platform = RamPlatformDetector.Detect(cpuName ?? platformInfo?.CpuName, memoryType);
        var layout = RamLayoutRules.Evaluate(ramModules, slots, tr);
        var temps = DimmTemperatures(readings);
        bool brand = platformInfo is not null && !platformInfo.IsSelfBuilt && platformInfo.SystemMaker is not (PcMaker.Unknown or PcMaker.Other);

        return new RamFacts
        {
            Modules = ramModules,
            MemoryType = memoryType,
            Platform = platform,
            BoardMaker = platformInfo?.BoardMaker ?? PcMaker.Unknown,
            BoardName = platformInfo?.DisplayName is { } name && name != "This PC" ? name : null,
            BiosVersion = platformInfo?.BiosVersion,
            BiosDate = platformInfo?.BiosDate,
            SupportPage = platformInfo is null ? null : SupportPageBuilder.For(platformInfo),
            SlotCount = slots,
            Xmp = xmp,
            Layout = layout,
            DimmTemperatures = temps,
            IsBrandPc = brand,
            Captured = now,
            Warnings = warnings,
        };
    }

    /// <summary>Ranks from SMBIOS type 17 "Attributes" (bits 0–3), which Win32_PhysicalMemory exposes on Windows 10 and later.</summary>
    public static int? Ranks(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null || !properties.TryGetValue("Attributes", out var text)
            || !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attributes))
        {
            return null;
        }

        int ranks = (int)(attributes & 0xF);
        return ranks is >= 1 and <= 8 ? ranks : null;
    }

    /// <summary>DIMM temperature sensors (DDR5 SPD hubs; LibreHardwareMonitor exposes them with PawnIO).</summary>
    public static IReadOnlyList<DimmTemperature> DimmTemperatures(IEnumerable<SensorReading>? readings) =>
        readings is null
            ? []
            : readings.Where(r => r.Hardware == HardwareKind.Memory && r.Kind == SensorKind.Temperature && r.Value is > 0 and < 150)
                .Select(r => new DimmTemperature(r.Name, r.Value!.Value))
                .ToList();

    private static RamModule ToModule(MemoryModuleInfo m, string? kitType, Translator tr)
    {
        var (channel, slot) = RamLayoutRules.ParseSlot(m.DeviceLocator, m.BankLabel);
        string type = NormalizeType(m.MemoryType) ?? kitType ?? string.Empty;
        int? fromPart = MemorySpeedRules.RatedFromPartNumber(m.PartNumber);
        int? rated = (m.SpeedMhz, fromPart) switch
        {
            ({ } s, { } p) => Math.Max(s, p),
            ({ } s, null) => s,
            (null, { } p) => p,
            _ => null,
        };
        var ic = RamIcCatalog.Guess(m.PartNumber, m.Manufacturer, type, m.CapacityBytes, tr);
        return new RamModule(m.DeviceLocator ?? m.Name, channel, slot, m.CapacityBytes, Ranks(m.Properties), m.PartNumber?.Trim(), m.Manufacturer,
            rated, m.ConfiguredClockSpeedMhz, type.Length == 0 ? null : type, ic);
    }

    private static string? NormalizeType(string? type) =>
        type is null ? null
        : type.Contains("DDR5", StringComparison.OrdinalIgnoreCase) ? "DDR5"
        : type.Contains("DDR4", StringComparison.OrdinalIgnoreCase) ? "DDR4"
        : null;
}
