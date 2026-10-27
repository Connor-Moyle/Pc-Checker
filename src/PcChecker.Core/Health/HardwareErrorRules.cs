using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Health;

/// <summary>What a WHEA-Logger event points at.</summary>
public enum WheaKind
{
    /// <summary>Event 19 (corrected machine check): a CPU core, cache or the Infinity Fabric corrected an error.</summary>
    CpuCorrected,

    /// <summary>Event 19 reported as a bus/interconnect error: on Ryzen usually the Infinity Fabric (FCLK).</summary>
    CpuBus,

    /// <summary>Event 17: a PCI Express link corrected an error (AER).</summary>
    Pcie,

    /// <summary>Event 47: corrected memory error.</summary>
    Memory,

    /// <summary>Event 18/20 or BugCheck 0x124: an uncorrectable hardware error that crashed Windows.</summary>
    Fatal,

    Other,
}

/// <summary>One WHEA-Logger event, with the fields read from its event data (names, not translated text).</summary>
public sealed record WheaEvent(DateTimeOffset Time, int EventId, string? ErrorType, string? ErrorSource, string? Component, int? ApicId);

/// <summary>A graphics driver that stopped responding and recovered (Display 4101, LiveKernelEvent 141/117).</summary>
public sealed record GpuDriverTimeout(DateTimeOffset Time, string Vendor, string Source);

/// <summary>
/// Pure rules for hardware-error events: WHEA-Logger (the main stability signal of a Ryzen PBO / Curve Optimizer /
/// EXPO / FCLK setting), BugCheck 0x124 and graphics driver timeouts. Parsing uses event ids and event-data field
/// names, never the translated message text.
/// </summary>
public static class HardwareErrorRules
{
    public const int WindowDays = 30;

    /// <summary>WHEA-Logger event ids PC Checker reads.</summary>
    public static IReadOnlyList<int> WheaEventIds { get; } = [1, 17, 18, 19, 20, 47];

    /// <summary>BugCheck code of WHEA_UNCORRECTABLE_ERROR.</summary>
    public const int WheaUncorrectableBugCheck = 0x124;

    public static WheaKind Classify(WheaEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.EventId switch
        {
            18 or 20 => WheaKind.Fatal,
            17 => WheaKind.Pcie,
            47 => WheaKind.Memory,
            19 when Has(e.ErrorType, "Bus") || Has(e.ErrorType, "Interconnect") || Has(e.Component, "Bus") => WheaKind.CpuBus,
            19 => WheaKind.CpuCorrected,
            1 when Has(e.Component, "PCI") => WheaKind.Pcie,
            1 when Has(e.Component, "Memory") => WheaKind.Memory,
            _ => WheaKind.Other,
        };
    }

    /// <summary>Builds a <see cref="WheaEvent"/> from event-data fields (ErrorType, ErrorSource, Component, ApicId).</summary>
    public static WheaEvent ParseWhea(int eventId, DateTimeOffset time, IReadOnlyDictionary<string, string?> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        string? Get(params string[] names)
        {
            foreach (var n in names)
            {
                if (data.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v))
                {
                    return v.Trim();
                }
            }

            return null;
        }

        int? apic = int.TryParse(Get("ApicId", "ProcessorAPICID", "ProcessorApicId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : null;
        return new WheaEvent(time, eventId, Get("ErrorType", "ErrorTypeName"), Get("ErrorSource", "ErrorSourceName"), Get("Component", "ComponentName"), apic);
    }

    /// <summary>"0x00000124" or "292" → 0x124; null when not a number.</summary>
    public static int? ParseBugCheckCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        int space = v.IndexOf(' ');
        if (space > 0)
        {
            v = v[..space];
        }

        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? (int)(hex & 0xFFFFFFFF) : null;
        }

        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : null;
    }

    /// <summary>
    /// The LiveKernelEvent code of a Windows Error Reporting 1001 record whose unnamed data fields are
    /// [bucket, type, event name, response, cab id, P1, …]; 141 and 117 are graphics driver timeouts. Null otherwise.
    /// </summary>
    public static int? LiveKernelCode(IReadOnlyList<string?> werData)
    {
        ArgumentNullException.ThrowIfNull(werData);
        if (werData.Count < 6 || !string.Equals(werData[2]?.Trim(), "LiveKernelEvent", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var p1 = werData[5]?.Trim();
        if (p1 is null)
        {
            return null;
        }

        return int.TryParse(p1, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    /// <summary>Graphics vendor from a driver name in event data: amdkmdag/amdwddmg → AMD, nvlddmkm → NVIDIA, igfx → Intel.</summary>
    public static string? DriverVendor(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var raw in values)
        {
            var v = raw ?? string.Empty;
            if (Has(v, "amdkmdag") || Has(v, "amdwddmg") || Has(v, "atikmdag") || Has(v, "atikmpag"))
            {
                return "AMD";
            }

            if (Has(v, "nvlddmkm"))
            {
                return "NVIDIA";
            }

            if (Has(v, "igfx") || Has(v, "igdkmd"))
            {
                return "Intel";
            }
        }

        return null;
    }

    /// <summary>The core number Ryzen Master and the BIOS use for an APIC id (two APIC ids per core with SMT on).</summary>
    public static int? CoreFromApicId(int? apicId, bool smt = true) => apicId is { } a and >= 0 ? (smt ? a / 2 : a) : null;

    /// <summary>The WHEA health item, or null when nothing was logged in the window.</summary>
    public static HealthItem? WheaItem(IReadOnlyList<WheaEvent> events, int bugCheck124Count, string? cpuName, DateTimeOffset now, int windowDays = WindowDays)
    {
        ArgumentNullException.ThrowIfNull(events);
        var recent = events.Where(e => (now - e.Time).TotalDays <= windowDays).ToList();
        if (recent.Count == 0 && bugCheck124Count == 0)
        {
            return null;
        }

        var traits = CpuTraits.Parse(cpuName);
        var groups = recent.GroupBy(Classify).ToDictionary(g => g.Key, g => g.ToList());
        int Count(WheaKind k) => groups.TryGetValue(k, out var l) ? l.Count : 0;
        var metrics = new Dictionary<string, string>();
        foreach (var (kind, list) in groups.OrderBy(g => g.Key))
        {
            metrics[KindLabel(kind)] = list.Count.ToString(CultureInfo.InvariantCulture);
        }

        if (bugCheck124Count > 0)
        {
            metrics["WHEA_UNCORRECTABLE_ERROR crashes"] = bugCheck124Count.ToString(CultureInfo.InvariantCulture);
        }

        var parts = new List<string>();
        var advice = new List<string>();
        int fatal = Count(WheaKind.Fatal) + bugCheck124Count;
        int cpu = Count(WheaKind.CpuCorrected) + Count(WheaKind.CpuBus);
        if (fatal > 0)
        {
            parts.Add($"Windows crashed {fatal} time{(fatal == 1 ? string.Empty : "s")} because of an uncorrectable hardware error (WHEA_UNCORRECTABLE_ERROR / machine check).");
            advice.Add("Undo any overclock or undervolt first (BIOS: Load Optimized Defaults), then check temperatures and run the memory test.");
        }

        if (cpu > 0)
        {
            var cores = recent.Where(e => Classify(e) is WheaKind.CpuCorrected or WheaKind.CpuBus)
                .Select(e => CoreFromApicId(e.ApicId)).Where(c => c is not null).Distinct().OrderBy(c => c).Take(4).ToList();
            string coreText = cores.Count > 0 ? $" (core {string.Join(", ", cores)}, from APIC ID ÷ 2)" : string.Empty;
            parts.Add($"The CPU corrected an internal error {cpu} time{(cpu == 1 ? string.Empty : "s")}{coreText}.");
            if (traits.IsAmd)
            {
                advice.Add(Count(WheaKind.CpuBus) > 0
                    ? "On Ryzen, bus/interconnect errors usually mean the Infinity Fabric clock (FCLK) or EXPO is not fully stable: lower FCLK (AM5 about 2000 MHz, AM4 1800 MHz) or turn EXPO off to test."
                    : "On Ryzen this usually means an overclock or undervolt is not fully stable: reduce Curve Optimizer on that core, turn off PBO, or lower EXPO/FCLK.");
            }
            else
            {
                advice.Add("This usually means an overclock or undervolt is not fully stable. Return the CPU and memory to stock and test again.");
            }
        }

        if (Count(WheaKind.Pcie) > 0)
        {
            parts.Add($"A PCI Express link corrected {Count(WheaKind.Pcie)} error(s).");
            advice.Add("Reseat the graphics card (and riser cable, if any), or set the PCIe slot to Gen4 or Gen3 in the BIOS.");
        }

        if (Count(WheaKind.Memory) > 0)
        {
            parts.Add($"The memory controller corrected {Count(WheaKind.Memory)} error(s).");
            advice.Add("Turn EXPO/XMP off to test, then run the memory test on the Memory page.");
        }

        if (Count(WheaKind.Other) > 0 && parts.Count == 0)
        {
            parts.Add($"Windows logged {Count(WheaKind.Other)} hardware error report(s).");
            advice.Add("Check temperatures and undo any overclock if it keeps happening.");
        }

        var status = fatal > 0 ? HealthStatus.Critical : HealthStatus.Warning;
        string title = fatal > 0 ? "Hardware errors crashed Windows" : "The hardware reported corrected errors";
        return new HealthItem("Stability", status, title,
            string.Join(" ", parts) + $" (last {windowDays} days, WHEA-Logger).",
            string.Join(" ", advice.Distinct()), metrics);
    }

    /// <summary>The graphics driver timeout item, or null when there were none.</summary>
    public static HealthItem? DriverTimeoutItem(IReadOnlyList<GpuDriverTimeout> timeouts, DateTimeOffset now, int windowDays = WindowDays)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        var recent = timeouts.Where(t => (now - t.Time).TotalDays <= windowDays).ToList();
        if (recent.Count == 0)
        {
            return null;
        }

        var vendor = recent.GroupBy(t => t.Vendor).OrderByDescending(g => g.Count()).First().Key;
        string reinstall = vendor switch
        {
            "AMD" => "otherwise reinstall AMD Software with the factory-reset option",
            "NVIDIA" => "otherwise do a clean install of the NVIDIA driver",
            "Intel" => "otherwise reinstall the Intel graphics driver",
            _ => "otherwise reinstall the graphics driver",
        };
        var metrics = new Dictionary<string, string>
        {
            ["Driver resets"] = recent.Count.ToString(CultureInfo.InvariantCulture),
            ["Most recent"] = recent.Max(t => t.Time).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        };
        return new HealthItem("Graphics", recent.Count >= 3 ? HealthStatus.Critical : HealthStatus.Warning,
            $"Your graphics driver crashed and recovered {recent.Count} time{(recent.Count == 1 ? string.Empty : "s")}",
            $"The {vendor} graphics driver stopped responding and Windows restarted it (Display event 4101 or LiveKernelEvent 141/117) in the last {windowDays} days.",
            $"If you tuned the GPU (overclock or undervolt), reset it on the Overclock page; {reinstall}.", metrics);
    }

    private static string KindLabel(WheaKind kind) => kind switch
    {
        WheaKind.CpuCorrected => "CPU corrected errors",
        WheaKind.CpuBus => "CPU bus/interconnect errors",
        WheaKind.Pcie => "PCI Express errors",
        WheaKind.Memory => "Memory errors",
        WheaKind.Fatal => "Fatal hardware errors",
        _ => "Other hardware errors",
    };

    private static bool Has(string? text, string part) => text is not null && text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
