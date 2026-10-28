using System.Globalization;
using System.Text.RegularExpressions;
using PcChecker.Core.Hardware;
using PcChecker.Core.History;

namespace PcChecker.Core.Upgrades;

/// <summary>Where one memory module sits: its channel letter and its slot inside the channel (1 = nearest the CPU).</summary>
public sealed record SlotPosition(char? Channel, int? IndexInChannel)
{
    public static SlotPosition Unknown { get; } = new(null, null);

    /// <summary>"A2", "B1", or null when the position is not known.</summary>
    public string? Label => Channel is { } c && IndexInChannel is { } i ? $"{c}{i}" : null;
}

/// <summary>How the installed modules are laid out.</summary>
public enum ChannelMode
{
    Unknown,
    SingleChannel,
    DualChannel,
}

/// <summary>The memory layout verdict for the Upgrade advice page.</summary>
public sealed record MemoryLayout(
    int ModuleCount,
    ChannelMode Mode,
    bool SameChannel,
    bool WrongSlots,
    bool MixedKits,
    IReadOnlyList<string> CurrentSlots,
    string RecommendedSlots);

/// <summary>How full the memory was in the recorded History.</summary>
public sealed record MemoryPressure(int Samples, double HighShare, double? PeakPercent)
{
    public static MemoryPressure None { get; } = new(0, 0, null);

    /// <summary>True when memory was at least <see cref="MemoryLayoutRules.HighUsePercent"/> % full often enough to matter.</summary>
    public bool IsOftenFull => Samples >= MemoryLayoutRules.MinPressureSamples && HighShare >= MemoryLayoutRules.OftenShare;
}

/// <summary>
/// Pure memory rules for the advisor: slot positions from the module's DeviceLocator and BankLabel (ASUS "DIMM_A2",
/// Gigabyte "DDR5_A2", MSI "DIMMA2", ASRock and Intel "ChannelA-DIMM1", AMD "P0 CHANNEL A" + "DIMM 1"), single or dual
/// channel, the A2/B2 rule for two modules on four-slot boards, mixed kits and memory pressure from History.
/// </summary>
public static partial class MemoryLayoutRules
{
    /// <summary>Memory in use at or above this percentage counts as "nearly full".</summary>
    public const double HighUsePercent = 85;

    /// <summary>Share of the recorded time that must be nearly full before the advisor says "often".</summary>
    public const double OftenShare = 0.10;

    /// <summary>At least this many samples are needed before pressure is judged.</summary>
    public const int MinPressureSamples = 60;

    [GeneratedRegex(@"CHANNEL\s*[-_]?\s*(?<ch>[A-H])\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelWord();

    [GeneratedRegex(@"DIMM\s*[-_]?\s*(?<i>[0-3])\b", RegexOptions.IgnoreCase)]
    private static partial Regex DimmIndex();

    [GeneratedRegex(@"(?:^|[^A-Z0-9])(?:DIMM|DDR\d)?[-_\s]?(?<ch>[A-H])[-_]?(?<i>[1-4])\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LetterNumber();

    /// <summary>Reads the slot position from the module's DeviceLocator and BankLabel. Unknown when neither says it.</summary>
    public static SlotPosition ParseSlot(string? deviceLocator, string? bankLabel)
    {
        string locator = (deviceLocator ?? string.Empty).Trim();
        string bank = (bankLabel ?? string.Empty).Trim();

        // "DIMM_A2", "DIMMA2", "DDR5_A2", "A2".
        var letter = LetterNumber().Match(locator);
        if (letter.Success)
        {
            return new SlotPosition(char.ToUpperInvariant(letter.Groups["ch"].Value[0]), int.Parse(letter.Groups["i"].Value, CultureInfo.InvariantCulture));
        }

        // "ChannelA-DIMM1", "Controller0-ChannelB-DIMM0", or BankLabel "P0 CHANNEL A" with DeviceLocator "DIMM 1".
        var channel = ChannelWord().Match(locator);
        if (!channel.Success)
        {
            channel = ChannelWord().Match(bank);
        }

        if (channel.Success)
        {
            char ch = char.ToUpperInvariant(channel.Groups["ch"].Value[0]);
            var dimm = DimmIndex().Match(locator);
            return new SlotPosition(ch, dimm.Success ? int.Parse(dimm.Groups["i"].Value, CultureInfo.InvariantCulture) + 1 : null);
        }

        return SlotPosition.Unknown;
    }

    /// <summary>The two slots most boards want for two modules, in the maker's own spelling.</summary>
    public static string RecommendedSlotNames(string? boardMaker, string? memoryType)
    {
        string maker = (boardMaker ?? string.Empty).ToUpperInvariant();
        string ddr = (memoryType ?? string.Empty).ToUpperInvariant().Contains("DDR4", StringComparison.Ordinal) ? "DDR4" : "DDR5";
        if (maker.Contains("ASUS", StringComparison.Ordinal))
        {
            return "DIMM_A2 + DIMM_B2";
        }

        if (maker.Contains("GIGA", StringComparison.Ordinal))
        {
            return $"{ddr}_A2 + {ddr}_B2";
        }

        if (maker.Contains("MICRO-STAR", StringComparison.Ordinal) || maker.Contains("MSI", StringComparison.Ordinal))
        {
            return "DIMMA2 + DIMMB2";
        }

        return "A2 + B2";
    }

    /// <summary>
    /// Single or dual channel, modules in the same channel, two modules in the wrong slots (A1/B1 on a four-slot board),
    /// and mixed kits (different part numbers, sizes or speeds). Laptops skip the slot rule.
    /// </summary>
    public static MemoryLayout Evaluate(IReadOnlyList<MemoryModuleInfo> modules, int? slots, string? boardMaker, bool isLaptop)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var present = modules.Where(m => m.CapacityBytes is > 0).ToList();
        string memoryType = present.Select(m => m.MemoryType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty;
        string recommended = RecommendedSlotNames(boardMaker, memoryType);
        var positions = present.Select(m => ParseSlot(m.DeviceLocator, m.BankLabel)).ToList();
        var labels = present.Select((m, i) => positions[i].Label ?? m.DeviceLocator ?? m.BankLabel ?? "?").ToList();

        if (present.Count == 0)
        {
            return new MemoryLayout(0, ChannelMode.Unknown, false, false, false, [], recommended);
        }

        bool mixed = IsMixedKit(present);
        if (present.Count == 1)
        {
            return new MemoryLayout(1, ChannelMode.SingleChannel, false, false, mixed, labels, recommended);
        }

        bool allKnownChannels = positions.All(p => p.Channel is not null);
        bool sameChannel = allKnownChannels && positions.Select(p => p.Channel).Distinct().Count() == 1;
        bool wrongSlots = !isLaptop && present.Count == 2 && slots == 4 && !sameChannel
            && positions.All(p => p.IndexInChannel == 1);

        ChannelMode mode = sameChannel ? ChannelMode.SingleChannel
            : allKnownChannels ? ChannelMode.DualChannel
            : Memory.MemoryRules.EvaluateChannels(present, slots ?? 0).DualChannelLikely ? ChannelMode.DualChannel : ChannelMode.Unknown;
        return new MemoryLayout(present.Count, mode, sameChannel, wrongSlots, mixed, labels, recommended);
    }

    /// <summary>True when the modules come from different kits: part numbers, sizes or rated speeds differ.</summary>
    public static bool IsMixedKit(IReadOnlyList<MemoryModuleInfo> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (modules.Count < 2)
        {
            return false;
        }

        static string Norm(string? s) => (s ?? string.Empty).Trim().ToUpperInvariant();
        var parts = modules.Select(m => Norm(m.PartNumber)).Where(p => p.Length > 0).Distinct().Count();
        var sizes = modules.Select(m => m.CapacityBytes ?? 0).Distinct().Count();
        var speeds = modules.Select(m => m.SpeedMhz ?? 0).Where(s => s > 0).Distinct().Count();
        return parts > 1 || sizes > 1 || speeds > 1;
    }

    /// <summary>How often memory was nearly full in the History samples (the "Memory in use" metric).</summary>
    public static MemoryPressure EvaluatePressure(IReadOnlyList<HistorySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double total = 0, high = 0;
        double? peak = null;
        int count = 0;
        foreach (var s in samples)
        {
            if (s.SpanSeconds <= 0 || s.Get(HistoryMetrics.Ram) is not { } ram || !double.IsFinite(ram))
            {
                continue;
            }

            count++;
            total += s.SpanSeconds;
            if (ram >= HighUsePercent)
            {
                high += s.SpanSeconds;
            }

            peak = peak is null ? ram : Math.Max(peak.Value, ram);
        }

        return total <= 0 ? MemoryPressure.None : new MemoryPressure(count, high / total, peak);
    }

    /// <summary>
    /// The memory size worth buying: 16 GB for PCs under 16 GB, 32 GB for 16 GB that is often full, 64 GB for 32 GB that
    /// is often full, otherwise null (enough).
    /// </summary>
    public static int? RecommendedTotalGb(long? totalBytes, MemoryPressure pressure)
    {
        ArgumentNullException.ThrowIfNull(pressure);
        if (totalBytes is not > 0)
        {
            return null;
        }

        double gb = totalBytes.Value / (1024d * 1024 * 1024);
        if (gb < 15)
        {
            return gb >= 7 && pressure.IsOftenFull ? 32 : 16;
        }

        if (!pressure.IsOftenFull)
        {
            return null;
        }

        return gb < 31 ? 32 : gb < 63 ? 64 : null;
    }
}
