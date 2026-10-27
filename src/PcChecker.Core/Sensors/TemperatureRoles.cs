using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PcChecker.Core.Hardware;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Sensors;

/// <summary>What a temperature sensor measures, so each one is held to the right limit.</summary>
public enum TemperatureRole
{
    Other,

    /// <summary>The CPU's control temperature: Tdie, Tctl/Tdie, CPU Package, Core Max.</summary>
    CpuControl,

    /// <summary>"Core (Tctl)" on Ryzen 1000/2000 X and Threadripper 1000/2000: Tdie plus a 10–27 °C fan offset.</summary>
    CpuTctlRaw,

    /// <summary>Per-core or per-CCD readings.</summary>
    CpuCore,

    /// <summary>GPU core / edge temperature.</summary>
    GpuCore,

    /// <summary>GPU hot spot (junction): AMD specifies up to 110 °C.</summary>
    GpuHotSpot,

    /// <summary>GPU memory (VRAM) temperature.</summary>
    GpuMemory,

    /// <summary>GPU voltage regulator (VR SoC/VDDC/MVDD).</summary>
    GpuVr,

    /// <summary>GPU liquid (AIO) temperature.</summary>
    GpuLiquid,

    /// <summary>A temperature of integrated graphics: it follows the CPU, so it is held to the CPU limits.</summary>
    IntegratedGpu,
}

/// <summary>
/// Pure classification of temperature sensors by role and the shared CPU temperature pick. One place for the
/// heuristics the Dashboard, overlay, Overclock page, fan profiles, alerts and watchdog all use.
/// </summary>
public static partial class TemperatureRoles
{
    /// <summary>Default hot spot (junction) thresholds: warn 100 °C, critical 110 °C (AMD's specified maximum).</summary>
    public const double HotSpotWarnC = 100;
    public const double HotSpotCritC = 110;

    /// <summary>Default GPU memory thresholds: warn 95 °C, critical 105 °C.</summary>
    public const double MemoryWarnC = 95;
    public const double MemoryCritC = 105;

    /// <summary>The stock CPU defaults in <see cref="MonitoringSettings"/>; platform-aware values replace only these.</summary>
    public const double StockCpuWarnC = 85;
    public const double StockCpuCritC = 95;

    /// <summary>Ryzen 7000/9000 (non-X3D) run at 95 °C by design, so their thresholds sit above that.</summary>
    public const double BoostToTjMaxWarnC = 90;
    public const double BoostToTjMaxCritC = 98;

    private static readonly ConcurrentDictionary<string, CpuTraitsInfo> TraitCache = new(StringComparer.Ordinal);

    [GeneratedRegex(@"\b(?:Core\s*#\s*\d+|CCD\s*\d+|CCDs?\s+(?:Max|Average)|P-Core|E-Core|Core\s+\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PerCore();

    public static TemperatureRole Classify(SensorReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.Kind != SensorKind.Temperature)
        {
            return TemperatureRole.Other;
        }

        var n = reading.Name ?? string.Empty;
        if (reading.Hardware == HardwareKind.Cpu)
        {
            if (IsRawTctl(n))
            {
                return TemperatureRole.CpuTctlRaw;
            }

            return PerCore().IsMatch(n) ? TemperatureRole.CpuCore : TemperatureRole.CpuControl;
        }

        if (reading.Hardware != HardwareKind.Gpu)
        {
            return TemperatureRole.Other;
        }

        if (GpuSelection.IsIntegrated(reading.HardwareName))
        {
            return TemperatureRole.IntegratedGpu;
        }

        if (Has(n, "memory") || Has(n, "vram") || Has(n, "mem "))
        {
            return TemperatureRole.GpuMemory;
        }

        if (Has(n, "hot spot") || Has(n, "hotspot") || Has(n, "junction"))
        {
            return TemperatureRole.GpuHotSpot;
        }

        if (Has(n, "VR ") || Has(n, "VRM") || Has(n, "VDDC") || Has(n, "MVDD") || Has(n, "SoC"))
        {
            return TemperatureRole.GpuVr;
        }

        return Has(n, "liquid") ? TemperatureRole.GpuLiquid : TemperatureRole.GpuCore;
    }

    /// <summary>True for "Core (Tctl)" alone (not "Core (Tctl/Tdie)").</summary>
    public static bool IsRawTctl(string? name) =>
        name is not null && name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) && !name.Contains("Tdie", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The CPU temperature to show: Tdie first (the real die temperature), then Tctl/Tdie, the Intel package,
    /// raw Tctl only when nothing better exists, then Core Max and anything named CPU. Per-core readings come last.
    /// </summary>
    public static SensorReading? PickCpuTemperature(IEnumerable<SensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var cpu = readings.Where(r => r.Hardware == HardwareKind.Cpu && r.Kind == SensorKind.Temperature && r.Value is { } v && !double.IsNaN(v)).ToList();
        if (cpu.Count == 0)
        {
            return null;
        }

        return cpu.FirstOrDefault(r => Has(r.Name, "Core (Tdie)"))
            ?? cpu.FirstOrDefault(r => Has(r.Name, "Tctl/Tdie"))
            ?? cpu.FirstOrDefault(r => Has(r.Name, "Package"))
            ?? cpu.FirstOrDefault(r => IsRawTctl(r.Name))
            ?? cpu.FirstOrDefault(r => Has(r.Name, "Core Max"))
            ?? cpu.FirstOrDefault(r => Has(r.Name, "Tdie") && !PerCore().IsMatch(r.Name))
            ?? cpu.FirstOrDefault(r => Has(r.Name, "CPU"))
            ?? cpu.Where(r => !PerCore().IsMatch(r.Name)).MaxBy(r => r.Value)
            ?? cpu.MaxBy(r => r.Value);
    }

    /// <summary>The core/edge temperature of the primary (discrete) GPU, or null.</summary>
    public static SensorReading? PickGpuCoreTemperature(IReadOnlyList<SensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var gpu = GpuSelection.PrimaryGpuOnly(readings)
            .Where(r => r.Hardware == HardwareKind.Gpu && r.Kind == SensorKind.Temperature && r.Value is { } v && !double.IsNaN(v))
            .ToList();
        return gpu.FirstOrDefault(r => Classify(r) == TemperatureRole.GpuCore && Has(r.Name, "core"))
            ?? gpu.FirstOrDefault(r => Classify(r) == TemperatureRole.GpuCore)
            ?? gpu.FirstOrDefault(r => Classify(r) == TemperatureRole.GpuHotSpot);
    }

    /// <summary>
    /// The CPU readings the watchdog and alerts compare with the CPU limit: control temperatures only, with raw
    /// Tctl left out when a Tdie reading exists. Falls back to every CPU temperature when no control reading exists.
    /// </summary>
    public static IEnumerable<SensorReading> CpuLimitReadings(IReadOnlyList<SensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var cpu = readings.Where(r => r.Hardware == HardwareKind.Cpu && r.Kind == SensorKind.Temperature).ToList();
        bool hasTdie = cpu.Any(r => Has(r.Name, "Tdie"));
        var control = cpu.Where(r => Classify(r) == TemperatureRole.CpuControl || (!hasTdie && Classify(r) == TemperatureRole.CpuTctlRaw)).ToList();
        return control.Count > 0 ? control : cpu.Where(r => !(hasTdie && Classify(r) == TemperatureRole.CpuTctlRaw));
    }

    /// <summary>CPU traits for a hardware name, cached (the alert path runs every second).</summary>
    public static CpuTraitsInfo TraitsFor(string? cpuName) =>
        string.IsNullOrWhiteSpace(cpuName) ? CpuTraitsInfo.Unknown : TraitCache.GetOrAdd(cpuName, static n => CpuTraits.Parse(n));

    /// <summary>
    /// The CPU warning and critical thresholds. When the user kept the stock 85/95 °C and the CPU is a Ryzen
    /// 7000/9000 that boosts to 95 °C by design, 90/98 °C is used so normal all-core load is not "critical".
    /// </summary>
    public static (double Warn, double Crit) CpuThresholds(MonitoringSettings settings, string? cpuName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool stock = settings.CpuWarnC == StockCpuWarnC && settings.CpuCritC == StockCpuCritC;
        if (stock && TraitsFor(cpuName).BoostsToTjMax)
        {
            return (BoostToTjMaxWarnC, BoostToTjMaxCritC);
        }

        return (settings.CpuWarnC, settings.CpuCritC);
    }

    /// <summary>
    /// Warning and critical thresholds for one reading, by role; null when the reading is not held to a limit
    /// (raw Tctl, which LibreHardwareMonitor always pairs with a real Tdie reading).
    /// </summary>
    public static (double Warn, double Crit)? ThresholdsFor(SensorReading reading, MonitoringSettings settings, string? cpuName = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(settings);
        var role = Classify(reading);
        return role switch
        {
            TemperatureRole.CpuTctlRaw => null,
            TemperatureRole.CpuControl or TemperatureRole.CpuCore => CpuThresholds(settings, reading.HardwareName),
            TemperatureRole.IntegratedGpu => CpuThresholds(settings, cpuName),
            TemperatureRole.GpuHotSpot or TemperatureRole.GpuVr => (Math.Max(settings.GpuHotSpotWarnC, settings.GpuWarnC), Math.Max(settings.GpuHotSpotCritC, settings.GpuCritC)),
            TemperatureRole.GpuMemory => (Math.Max(settings.GpuMemoryWarnC, settings.GpuWarnC), Math.Max(settings.GpuMemoryCritC, settings.GpuCritC)),
            TemperatureRole.GpuCore or TemperatureRole.GpuLiquid => (settings.GpuWarnC, settings.GpuCritC),
            _ => SensorMath.ThresholdsFor(reading.Hardware, settings),
        };
    }

    /// <summary>
    /// The CPU stress-test stop temperature. When the user kept the stock 90 °C and the CPU boosts to its Tjmax by
    /// design (Ryzen 7000/9000), the limit becomes Tjmax + 3 °C so a healthy chip is not stopped at its design point.
    /// </summary>
    public static int EffectiveCpuStopC(int configured, string? cpuName, int stockDefault = 90)
    {
        var traits = TraitsFor(cpuName);
        if (configured == stockDefault && traits.BoostsToTjMax && traits.TjMaxC is { } tj)
        {
            return Math.Max(configured, tj + 3);
        }

        return configured;
    }

    private static bool Has(string? text, string part) => text is not null && text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
