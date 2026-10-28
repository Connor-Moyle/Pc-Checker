using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Hardware;
using PcChecker.Core.Overclocking;

namespace PcChecker.Core.RamTuning;

/// <summary>The memory platform the BIOS plan is written for.</summary>
public enum RamPlatformKind
{
    Unknown,

    /// <summary>Ryzen 7000/8000/9000 desktop on AM5 (DDR5).</summary>
    Am5,

    /// <summary>Ryzen 3000/5000 desktop on AM4 (DDR4), Zen 2 or Zen 3.</summary>
    Am4,

    /// <summary>Ryzen 1000/2000 desktop on AM4 (Zen and Zen+): older memory controllers.</summary>
    Am4Legacy,

    /// <summary>Intel 12th–14th gen desktop on LGA1700 with DDR5.</summary>
    IntelLga1700Ddr5,

    /// <summary>Intel 12th–14th gen desktop on LGA1700 with DDR4.</summary>
    IntelLga1700Ddr4,

    /// <summary>Intel Core Ultra 200S (Arrow Lake) desktop on LGA1851 (DDR5).</summary>
    IntelArrowLake,

    /// <summary>A laptop, handheld or mini PC: memory speed is usually fixed by the maker.</summary>
    Mobile,

    /// <summary>An older or unrecognised desktop: only the generic plan applies.</summary>
    OtherDesktop,
}

/// <summary>What PC Checker knows about the processor for memory tuning.</summary>
public sealed record RamPlatform(
    RamPlatformKind Kind,
    string? CpuName,
    ZenGeneration Zen,
    int? IntelGeneration,
    bool IsX3D,
    string? Socket)
{
    public static RamPlatform Unknown { get; } = new(RamPlatformKind.Unknown, null, ZenGeneration.Unknown, null, false, null);

    public bool IsAmd => Kind is RamPlatformKind.Am5 or RamPlatformKind.Am4 or RamPlatformKind.Am4Legacy;

    public bool IsIntel => Kind is RamPlatformKind.IntelLga1700Ddr5 or RamPlatformKind.IntelLga1700Ddr4 or RamPlatformKind.IntelArrowLake;

    /// <summary>
    /// Intel Core 13th and 14th gen desktop chips need a BIOS with Intel's 0x12B (or newer) microcode and the Intel
    /// Default Settings because of the Vmin shift instability.
    /// </summary>
    public bool NeedsIntelMicrocodeFix => IntelGeneration is 13 or 14 && Kind is RamPlatformKind.IntelLga1700Ddr5 or RamPlatformKind.IntelLga1700Ddr4;
}

/// <summary>Which DRAM chips a module most likely uses.</summary>
public enum DramIc
{
    Unknown,

    /// <summary>SK hynix 16 Gbit DDR5 (M-die or A-die; the part number cannot tell which).</summary>
    HynixDdr5Gen16,

    /// <summary>SK hynix 24 Gbit DDR5 M-die (24 and 48 GB modules).</summary>
    HynixDdr5Gen24,

    SamsungDdr5,
    MicronDdr5,

    /// <summary>Samsung 8 Gbit B-die DDR4: the best-known DDR4 overclocking chip.</summary>
    SamsungBDie,

    MicronDdr4,
}

/// <summary>How sure the chip guess is.</summary>
public enum IcConfidence
{
    Unknown,

    /// <summary>Follows from the speed and timings on the label; usually right, not guaranteed.</summary>
    Likely,

    /// <summary>The brand only uses one chip maker (Crucial is Micron).</summary>
    Known,
}

/// <summary>The chip guess with its reason in plain words.</summary>
public sealed record RamIcGuess(DramIc Ic, IcConfidence Confidence, string Name, string Reason)
{
    public static RamIcGuess Unknown(string reason) => new(DramIc.Unknown, IcConfidence.Unknown, "Unknown", reason);

    public bool IsKnownEnough => Ic != DramIc.Unknown && Confidence != IcConfidence.Unknown;
}

/// <summary>What a part number says about the kit: rated speed and the first timings on the label.</summary>
public readonly record struct PartNumberTimings(int? RatedMts, int? Cl, int? Trcd);

/// <summary>One installed module as the RAM tuning page sees it.</summary>
public sealed record RamModule(
    string Slot,
    char? Channel,
    int? SlotNumber,
    long? CapacityBytes,
    int? Ranks,
    string? PartNumber,
    string? Manufacturer,
    int? RatedMts,
    int? ConfiguredMts,
    string? MemoryType,
    RamIcGuess Ic);

/// <summary>How the modules are spread over the memory channels.</summary>
public enum ChannelMode
{
    Unknown,
    Single,
    Dual,
}

/// <summary>Slots, channels and kits.</summary>
public sealed record RamLayout(
    ChannelMode Mode,
    int ModuleCount,
    int SlotCount,
    bool MixedKits,
    bool RecommendedSlots,
    string? SlotAdvice);

/// <summary>A DIMM temperature sensor (the SPD hub of a DDR5 module).</summary>
public sealed record DimmTemperature(string Name, double Celsius);

/// <summary>Everything detected for the plan.</summary>
public sealed record RamFacts
{
    public IReadOnlyList<RamModule> Modules { get; init; } = [];

    /// <summary>"DDR4", "DDR5" or null.</summary>
    public string? MemoryType { get; init; }

    public RamPlatform Platform { get; init; } = RamPlatform.Unknown;

    public PcMaker BoardMaker { get; init; }

    /// <summary>"ASUS ROG STRIX Z790-E GAMING WIFI", or null.</summary>
    public string? BoardName { get; init; }

    public string? BiosVersion { get; init; }

    public DateTime? BiosDate { get; init; }

    /// <summary>The board maker's BIOS download page, when known.</summary>
    public PlatformLink? SupportPage { get; init; }

    public int SlotCount { get; init; }

    public XmpEvaluation Xmp { get; init; } = new(XmpEvaluation.Unknown, null, null, string.Empty);

    public RamLayout Layout { get; init; } = new(ChannelMode.Unknown, 0, 0, false, true, null);

    public IReadOnlyList<DimmTemperature> DimmTemperatures { get; init; } = [];

    /// <summary>True for a brand PC or laptop (Dell, HP, Lenovo…), whose BIOS rarely offers memory settings.</summary>
    public bool IsBrandPc { get; init; }

    public DateTimeOffset Captured { get; init; } = DateTimeOffset.Now;

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The slowest configured speed of all modules, in MT/s.</summary>
    public int? ConfiguredMts => Xmp.ConfiguredMhz;

    /// <summary>The highest rating found (SMBIOS or part number), in MT/s.</summary>
    public int? RatedMts => Xmp.MaxMhz;

    /// <summary>Largest module in bytes, or null.</summary>
    public long? ModuleBytes => Modules.Select(m => m.CapacityBytes).Where(c => c is > 0).Max();

    /// <summary>Highest rank count reported, or null.</summary>
    public int? MaxRanks => Modules.Select(m => m.Ranks).Where(r => r is > 0).Max();
}
