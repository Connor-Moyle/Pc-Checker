using PcChecker.Core.Common;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Memory;

/// <summary>
/// Pure rules for the Memory page: XMP/EXPO evaluation, channel heuristic, plain-English warnings, the last
/// Windows Memory Diagnostic result and a few formatting helpers. No Windows dependency, fully unit-testable.
/// </summary>
public static class MemoryRules
{
    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>Speeds within this fraction of the rating count as "running at rated speed" (6000 vs 5999 rounding, etc.).</summary>
    public const double SpeedTolerance = 0.03;

    /// <summary>
    /// Compares the configured clock of every module against its rated speed. <paramref name="platform"/> is the CPU vendor
    /// ("Intel", "AMD" or null) and only changes the wording (XMP vs EXPO).
    /// </summary>
    public static XmpEvaluation EvaluateXmp(IReadOnlyList<MemoryModuleInfo> modules, string? platform = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        int? configured = modules.Select(m => m.ConfiguredClockSpeedMhz).Where(s => s is > 0).DefaultIfEmpty().Max();
        int? rated = modules.Select(m => m.SpeedMhz).Where(s => s is > 0).DefaultIfEmpty().Max();
        string memoryType = modules.Select(m => m.MemoryType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty;
        string profile = ProfileNameFor(platform, memoryType);

        if (configured is null or 0 || rated is null or 0)
        {
            return new XmpEvaluation("Unknown", profile, configured is 0 ? null : configured, rated is 0 ? null : rated,
                "Memory speed could not be read",
                $"Windows did not report the rated and running speed of your memory. You can check whether {profile} is on in the BIOS (it is usually under an Overclocking, Tweaker or Extreme Memory Profile menu).",
                Severity.Low);
        }

        if (configured >= rated * (1 - SpeedTolerance))
        {
            string title = configured > rated * (1 + SpeedTolerance)
                ? $"Running above the rated {rated} MT/s ({configured} MT/s)"
                : $"Running at the rated {rated} MT/s";
            return new XmpEvaluation("Enabled", profile, configured, rated, title,
                $"{profile} is on: your memory runs at the speed it was sold for. Nothing to do here.",
                Severity.Info);
        }

        return new XmpEvaluation("Disabled", profile, configured, rated,
            $"Your RAM is rated for {rated} MT/s but runs at {configured} MT/s",
            $"Enable {profile} in the BIOS to get the speed you paid for: restart the PC, press Del or F2 while it starts, "
            + $"find \"{profile}\" (also called DOCP or A-XMP on some boards) under Overclocking, Tweaker or Memory, choose Profile 1, then save and exit. "
            + "Games and heavy multitasking will feel noticeably smoother. If the PC then fails to boot, the BIOS resets to defaults after a few tries.",
            Severity.Medium);
    }

    /// <summary>"EXPO" for DDR5 on AMD, "XMP" for Intel, "XMP/EXPO" when the platform is unknown.</summary>
    public static string ProfileNameFor(string? platform, string? memoryType)
    {
        bool amd = platform?.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true;
        bool intel = platform?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true;
        bool ddr5 = memoryType?.Contains("DDR5", StringComparison.OrdinalIgnoreCase) == true;
        if (amd)
        {
            return ddr5 ? "EXPO" : "XMP";
        }

        return intel ? "XMP" : "XMP/EXPO";
    }

    /// <summary>Dual channel is likely when there are two or more modules of equal size (an even count).</summary>
    public static ChannelEvaluation EvaluateChannels(IReadOnlyList<MemoryModuleInfo> modules, int slots)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var sizes = modules.Select(m => m.CapacityBytes ?? 0).Where(c => c > 0).ToList();
        if (sizes.Count == 0)
        {
            return new ChannelEvaluation(false, "Module details unavailable", "Windows did not report the installed modules.");
        }

        if (sizes.Count == 1)
        {
            string detail = slots > 1
                ? $"Only one module is installed in {slots} slots, so the memory runs single-channel. Adding a second matching module roughly doubles memory bandwidth and helps games and integrated graphics noticeably."
                : "Only one module is installed, so the memory runs single-channel.";
            return new ChannelEvaluation(false, "Single-channel", detail);
        }

        bool equal = sizes.Distinct().Count() == 1;
        bool even = sizes.Count % 2 == 0;
        if (equal && even)
        {
            string label = sizes.Count >= 8 ? "Multi-channel likely" : sizes.Count >= 4 ? "Quad or dual-channel likely" : "Dual-channel likely";
            return new ChannelEvaluation(true, label, $"{sizes.Count} matching modules of {FormatCapacity(sizes[0])} each. Memory bandwidth is being used fully.");
        }

        if (!equal)
        {
            return new ChannelEvaluation(false, "Mixed module sizes",
                "The modules are different sizes. Many boards still pair part of the memory (\"flex mode\"), but matching modules are faster and more stable.");
        }

        return new ChannelEvaluation(false, "Odd number of modules",
            $"{sizes.Count} modules are installed. Dual-channel needs modules in pairs; the unpaired one runs single-channel.");
    }

    /// <summary>Plain-English warnings, ordered by importance.</summary>
    public static IReadOnlyList<string> BuildWarnings(MemoryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var warnings = new List<string>();

        if (facts.LastMemoryDiagnosticResult is { } diag && diag.Contains("problem", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("The last Windows Memory Diagnostic found hardware problems. Faulty RAM causes crashes and corrupt files: test the modules one at a time and replace the bad one.");
        }

        if (facts.WarnOnLowMemory && facts.TotalBytes > 0)
        {
            double freePercent = 100.0 * facts.AvailableBytes / facts.TotalBytes;
            if (freePercent < facts.LowMemoryPercent)
            {
                warnings.Add($"Only {freePercent:0} % of memory is free right now. Close programs you are not using, or clear the standby list for a quick breather.");
            }
        }

        if (facts.CommitLimitBytes > 0 && facts.CommitTotalBytes > facts.CommitLimitBytes * 0.9)
        {
            warnings.Add("Windows is close to its commit limit (RAM plus page file). Programs may fail to start; a larger page file or more RAM avoids this.");
        }

        if (facts.Xmp.IsDisabled)
        {
            warnings.Add(facts.Xmp.Title + ". " + $"Enable {facts.Xmp.ProfileName} in the BIOS (see the memory profile card).");
        }

        var sizes = facts.Modules.Select(m => m.CapacityBytes ?? 0).Where(c => c > 0).ToList();
        if (sizes.Count == 1)
        {
            warnings.Add("A single memory module runs single-channel. A second matching module makes games and integrated graphics faster.");
        }
        else if (sizes.Count > 1 && sizes.Distinct().Count() > 1)
        {
            warnings.Add("The memory modules are different sizes, which can disable dual-channel mode. Matching pairs are faster.");
        }

        if (facts.TotalBytes > 0 && facts.TotalBytes < 8 * Gb)
        {
            warnings.Add($"This PC has {FormatCapacity(facts.TotalBytes)} of RAM, which is tight for Windows 11. The \"Low-memory PC\" profile helps; adding RAM helps more.");
        }

        warnings.AddRange(PageFileRules.Evaluate(facts.TotalBytes, facts.AutomaticPageFile, facts.PageFiles));

        if (!facts.MemoryCompressionEnabled && facts.TotalBytes > 0 && facts.TotalBytes <= 16 * Gb)
        {
            warnings.Add("Memory compression is off. On a PC with 16 GB or less it keeps more programs in RAM instead of the slower page file; the Balanced profile turns it back on.");
        }

        if (!facts.SysMainEnabled)
        {
            warnings.Add("SysMain (Superfetch) is off. It preloads the programs you use most; turning it back on usually makes everyday use feel faster, especially on a hard drive.");
        }

        return warnings;
    }

    /// <summary>Turns the newest MemoryDiagnostics-Results event into one sentence, or "never run" wording when there is none.</summary>
    public static string DescribeDiagnostic(MemoryDiagnosticEvent? latest, DateTimeOffset now)
    {
        if (latest is null)
        {
            return "The Windows Memory Diagnostic has not been run yet (no result in the System event log).";
        }

        string when = DescribeAge(now - latest.Time);
        return latest.EventId switch
        {
            1201 => $"No errors were detected (Windows Memory Diagnostic, {when})",
            1202 => $"Hardware problems were detected (Windows Memory Diagnostic, {when}). Replace or re-seat the faulty module.",
            _ => $"Windows Memory Diagnostic ran {when}: " + (string.IsNullOrWhiteSpace(latest.Message) ? "result unknown" : latest.Message.Trim()),
        };
    }

    /// <summary>"today", "yesterday", "12 days ago", "3 months ago".</summary>
    public static string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        int days = (int)age.TotalDays;
        return days switch
        {
            0 => "today",
            1 => "yesterday",
            < 31 => $"{days} days ago",
            < 365 => $"{days / 30} month{(days / 30 == 1 ? string.Empty : "s")} ago",
            _ => $"{days / 365} year{(days / 365 == 1 ? string.Empty : "s")} ago",
        };
    }

    /// <summary>SMBIOS memory type code (Win32_PhysicalMemory.SMBIOSMemoryType) to a name.</summary>
    public static string MemoryTypeName(int smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        36 => "LPDDR5X",
        0 or 1 or 2 => "Unknown",
        _ => $"Type {smbiosType}",
    };

    /// <summary>SMBIOS form factor code (Win32_PhysicalMemory.FormFactor) to a name.</summary>
    public static string FormFactorName(int formFactor) => formFactor switch
    {
        8 => "DIMM",
        12 => "SODIMM",
        13 => "SRIMM",
        9 => "TSOP",
        10 => "Row of chips",
        11 => "RIMM",
        0 or 1 or 2 => "Unknown",
        _ => $"Form factor {formFactor}",
    };

    /// <summary>"2 × 16 GB DDR5-6000 (G.Skill)" style summary of the installed kit.</summary>
    public static string DescribeKit(IReadOnlyList<MemoryModuleInfo> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var withSize = modules.Where(m => m.CapacityBytes is > 0).ToList();
        if (withSize.Count == 0)
        {
            return "No module details available";
        }

        var groups = withSize.GroupBy(m => m.CapacityBytes!.Value).OrderByDescending(g => g.Key).ToList();
        string sizes = string.Join(" + ", groups.Select(g => $"{g.Count()} × {FormatCapacity(g.Key)}"));
        string type = withSize.Select(m => m.MemoryType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty;
        int? rated = withSize.Select(m => m.SpeedMhz).Where(s => s is > 0).DefaultIfEmpty().Max();
        string speed = rated is > 0 ? $"-{rated}" : string.Empty;
        string vendor = withSize.Select(m => CleanVendor(m.Manufacturer)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
        string typeText = string.IsNullOrWhiteSpace(type) ? string.Empty : $" {type}{speed}";
        string vendorText = string.IsNullOrWhiteSpace(vendor) ? string.Empty : $" ({vendor})";
        return sizes + typeText + vendorText;
    }

    /// <summary>Strips SMBIOS placeholders such as "Unknown", "0000" or "Manufacturer00" and trims.</summary>
    public static string? CleanVendor(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            return null;
        }

        var v = manufacturer.Trim();
        if (v.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || v.All(c => c == '0') || v.StartsWith("Manufacturer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return v;
    }

    /// <summary>Module capacities are whole gigabytes almost always: "16 GB" rather than "16.0 GB"; falls back to <see cref="ByteSize.Format(long)"/>.</summary>
    public static string FormatCapacity(long bytes)
    {
        if (bytes > 0 && bytes % Gb == 0)
        {
            return $"{bytes / Gb} GB";
        }

        if (bytes > 0 && bytes < Gb && bytes % (1024 * 1024) == 0)
        {
            return $"{bytes / (1024 * 1024)} MB";
        }

        return ByteSize.Format(bytes);
    }

    /// <summary>"6000 MT/s" or "—".</summary>
    public static string FormatSpeed(int? mhz) => mhz is > 0 ? $"{mhz} MT/s" : "—";
}
