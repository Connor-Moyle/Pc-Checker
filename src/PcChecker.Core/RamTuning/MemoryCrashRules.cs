using PcChecker.Core.Crashes;

namespace PcChecker.Core.RamTuning;

/// <summary>Blue screens and hardware errors since a point in time that point at memory settings.</summary>
public sealed record MemoryCrashSummary(int MemoryBlueScreens, int WheaErrors, DateTimeOffset? Latest, IReadOnlyList<string> StopCodes)
{
    public static MemoryCrashSummary None { get; } = new(0, 0, null, []);

    public bool Any => MemoryBlueScreens > 0 || WheaErrors > 0;
}

/// <summary>
/// Picks the crashes from the Crashes service that typically follow an unstable memory setting. Stop codes:
/// 0x1A MEMORY_MANAGEMENT, 0x50 PAGE_FAULT_IN_NONPAGED_AREA, 0x4E PFN_LIST_CORRUPT, 0x12B FAULTY_HARDWARE_CORRUPTED_PAGE
/// (Microsoft's bug check reference), and 0x124 WHEA_UNCORRECTABLE_ERROR when its record names memory, the memory
/// controller or a bus/interconnect error (on Ryzen the Infinity Fabric). Read-only and pure.
/// </summary>
public static class MemoryCrashRules
{
    /// <summary>Stop codes that almost always mean bad or unstable memory.</summary>
    public static IReadOnlySet<uint> MemoryStopCodes { get; } = new HashSet<uint> { 0x1A, 0x50, 0x4E, 0x12B };

    /// <summary>True when the crash is a memory-related blue screen.</summary>
    public static bool IsMemoryRelated(CrashEvent crash)
    {
        ArgumentNullException.ThrowIfNull(crash);
        if (crash.Kind != CrashKind.BlueScreen || crash.BugCheckCode is not { } code)
        {
            return false;
        }

        if (MemoryStopCodes.Contains(code))
        {
            return true;
        }

        return code == 0x124 && crash.Whea is { } whea && IsMemoryWhea(whea);
    }

    /// <summary>True when a WHEA record names memory, the memory controller or the bus between them.</summary>
    public static bool IsMemoryWhea(WheaErrorInfo whea)
    {
        ArgumentNullException.ThrowIfNull(whea);
        if (whea.Components.Any(c => c.Contains("Memory", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (whea.MciStatus is { } status)
        {
            // Architectural MCA error codes: 0000 1MMM CCCC (memory controller) and 0000 1PPT RRRR IILL (bus/interconnect).
            int c = (int)(status & 0xEFFF);
            return (c & 0xEF80) == 0x0080 || (c & 0xE800) == 0x0800;
        }

        return string.Equals(whea.ProcessorErrorType, "Bus error", StringComparison.Ordinal);
    }

    /// <summary>What happened since <paramref name="since"/>: memory blue screens and WHEA hardware errors (corrected or fatal).</summary>
    public static MemoryCrashSummary Since(CrashReport? report, DateTimeOffset since)
    {
        if (report is null)
        {
            return MemoryCrashSummary.None;
        }

        // A blue screen is often listed twice (BugCheck 1001 and Kernel-Power 41); count each time once.
        var crashes = report.Events.Where(e => e.Time >= since && IsMemoryRelated(e))
            .GroupBy(e => (e.BugCheckCode, Minute: e.Time.ToUnixTimeSeconds() / 300))
            .Select(g => g.First())
            .ToList();
        var whea = report.SystemEvents.Where(e => e.Time >= since && e.Kind is CrashSystemEventKind.WheaCorrected or CrashSystemEventKind.WheaFatal).ToList();
        DateTimeOffset? latest = crashes.Select(c => (DateTimeOffset?)c.Time).Concat(whea.Select(w => (DateTimeOffset?)w.Time)).Max();
        var codes = crashes.Select(c => $"0x{c.BugCheckCode!.Value:X}").Distinct(StringComparer.Ordinal).ToList();
        return new MemoryCrashSummary(crashes.Count, whea.Count, latest, codes);
    }
}
