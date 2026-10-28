using System.Globalization;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Upgrades;

/// <summary>A rough class of graphics card inside its generation.</summary>
public enum GpuTier
{
    Unknown,
    Integrated,
    Entry,
    Mainstream,
    UpperMid,
    High,
    Enthusiast,
}

/// <summary>How old a graphics card's generation is.</summary>
public enum GpuAge
{
    Unknown,
    Current,
    Recent,
    Aging,
    Old,
}

/// <summary>Whether the card's memory (VRAM) suits the screen it drives.</summary>
public enum VramFit
{
    Unknown,
    Fine,
    Tight,
    TooLittle,
}

/// <summary>What the model name says about a graphics card.</summary>
public sealed record GpuClass(string Vendor, string Generation, int? ReleaseYear, GpuTier Tier, int? BoardPowerW);

/// <summary>
/// Pure graphics card rules: generation and release year from the model name (rough buckets, not a benchmark),
/// typical board power from a curated table, VRAM against the screen resolution, and a narrowed PCIe link.
/// </summary>
public static partial class GpuRules
{
    [GeneratedRegex(@"\bRTX\s*(?<gen>20|30|40|50)(?<model>[5-9]0)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NvidiaRtx();

    [GeneratedRegex(@"\bGTX\s*(?<gen>16|10|9)(?<model>[5-8]0)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NvidiaGtx();

    [GeneratedRegex(@"\bRX\s*(?<num>[5-9]\d{3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonRdna();

    [GeneratedRegex(@"\bRX\s*(?<num>[45][5-9]0)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonPolaris();

    [GeneratedRegex(@"\bArc\s*(?<gen>[AB])(?<tier>[3-9])\d0\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelArc();

    /// <summary>Typical total board power (W) of common desktop cards. Longest matching name wins.</summary>
    private static readonly (string Name, int Watts)[] BoardPower =
    [
        ("RTX 5090", 575), ("RTX 5080", 360), ("RTX 5070 TI", 300), ("RTX 5070", 250), ("RTX 5060 TI", 180), ("RTX 5060", 145),
        ("RTX 4090", 450), ("RTX 4080 SUPER", 320), ("RTX 4080", 320), ("RTX 4070 TI SUPER", 285), ("RTX 4070 TI", 285),
        ("RTX 4070 SUPER", 220), ("RTX 4070", 200), ("RTX 4060 TI", 165), ("RTX 4060", 115),
        ("RTX 3090 TI", 450), ("RTX 3090", 350), ("RTX 3080 TI", 350), ("RTX 3080", 320), ("RTX 3070 TI", 290), ("RTX 3070", 220),
        ("RTX 3060 TI", 200), ("RTX 3060", 170), ("RTX 3050", 130),
        ("RTX 2080 TI", 250), ("RTX 2080 SUPER", 250), ("RTX 2080", 215), ("RTX 2070 SUPER", 215), ("RTX 2070", 175),
        ("RTX 2060 SUPER", 175), ("RTX 2060", 160),
        ("GTX 1660 TI", 120), ("GTX 1660 SUPER", 125), ("GTX 1660", 120), ("GTX 1650", 75),
        ("GTX 1080 TI", 250), ("GTX 1080", 180), ("GTX 1070 TI", 180), ("GTX 1070", 150), ("GTX 1060", 120), ("GTX 1050", 75),
        ("RX 9070 XT", 304), ("RX 9070", 220), ("RX 9060 XT", 180),
        ("RX 7900 XTX", 355), ("RX 7900 XT", 315), ("RX 7900 GRE", 260), ("RX 7800 XT", 263), ("RX 7700 XT", 245), ("RX 7600 XT", 190), ("RX 7600", 165),
        ("RX 6950 XT", 335), ("RX 6900 XT", 300), ("RX 6800 XT", 300), ("RX 6800", 250), ("RX 6750 XT", 250), ("RX 6700 XT", 230),
        ("RX 6650 XT", 180), ("RX 6600 XT", 160), ("RX 6600", 132), ("RX 6500 XT", 107),
        ("RX 5700 XT", 225), ("RX 5700", 180), ("RX 5600 XT", 150), ("RX 5500 XT", 130),
        ("RX 590", 225), ("RX 580", 185), ("RX 570", 150), ("RX 480", 150), ("RX 470", 120),
        ("ARC B580", 190), ("ARC B570", 150), ("ARC A770", 225), ("ARC A750", 225), ("ARC A580", 185), ("ARC A380", 75),
    ];

    /// <summary>Generation, year, tier and board power from the card's name. Integrated graphics are recognised by the caller.</summary>
    public static GpuClass Classify(string? name, bool isIntegrated = false)
    {
        var n = Regex.Replace((name ?? string.Empty).ToUpperInvariant(), @"\s+", " ").Trim();
        int? power = BoardPowerOf(n);
        if (isIntegrated)
        {
            return new GpuClass(VendorOf(n), "Integrated graphics", null, GpuTier.Integrated, 0);
        }

        if (NvidiaRtx().Match(n) is { Success: true } rtx)
        {
            int gen = Int(rtx.Groups["gen"].Value);
            int year = gen switch { 20 => 2018, 30 => 2020, 40 => 2022, _ => 2025 };
            return new GpuClass("NVIDIA", $"GeForce RTX {gen} series", year, TierFromDigit(Int(rtx.Groups["model"].Value) / 10, n), power);
        }

        if (NvidiaGtx().Match(n) is { Success: true } gtx)
        {
            int gen = Int(gtx.Groups["gen"].Value);
            int year = gen switch { 16 => 2019, 10 => 2016, _ => 2014 };
            return new GpuClass("NVIDIA", $"GeForce GTX {(gen == 9 ? "900" : gen + "")} series", year,
                TierFromDigit(Int(gtx.Groups["model"].Value) / 10, n), power);
        }

        if (RadeonRdna().Match(n) is { Success: true } rdna)
        {
            string num = rdna.Groups["num"].Value;
            int series = num[0] - '0';
            int year = series switch { 9 => 2025, 7 => 2022, 6 => 2020, _ => 2019 };
            // RX 9060/9070 put the class in the tens digit, older cards in the hundreds digit (RX 7800 XT).
            int digit = series == 9 ? num[2] - '0' : num[1] - '0';
            return new GpuClass("AMD", $"Radeon RX {series}000 series", year, TierFromDigit(digit, n), power);
        }

        if (RadeonPolaris().Match(n) is { Success: true } polaris)
        {
            string num = polaris.Groups["num"].Value;
            return new GpuClass("AMD", $"Radeon RX {num[0]}00 series", num[0] == '4' ? 2016 : 2017, GpuTier.Mainstream, power);
        }

        if (IntelArc().Match(n) is { Success: true } arc)
        {
            bool battlemage = arc.Groups["gen"].Value == "B";
            int digit = Int(arc.Groups["tier"].Value);
            return new GpuClass("Intel", battlemage ? "Arc B series" : "Arc A series", battlemage ? 2024 : 2022,
                digit >= 7 ? GpuTier.UpperMid : digit >= 5 ? GpuTier.Mainstream : GpuTier.Entry, power);
        }

        return new GpuClass(VendorOf(n), "Unknown generation", null, GpuTier.Unknown, power);
    }

    /// <summary>Current (up to 2 years), recent (up to 4), aging (up to 6) or old, from the generation's release year.</summary>
    public static GpuAge AgeOf(GpuClass gpu, int currentYear)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        if (gpu.ReleaseYear is not { } year)
        {
            return GpuAge.Unknown;
        }

        return (currentYear - year) switch
        {
            <= 2 => GpuAge.Current,
            <= 4 => GpuAge.Recent,
            <= 6 => GpuAge.Aging,
            _ => GpuAge.Old,
        };
    }

    /// <summary>Typical board power for the tier when the table does not know the card.</summary>
    public static int FallbackBoardPower(GpuTier tier) => tier switch
    {
        GpuTier.Integrated => 0,
        GpuTier.Entry => 120,
        GpuTier.Mainstream => 170,
        GpuTier.UpperMid => 230,
        GpuTier.High => 320,
        GpuTier.Enthusiast => 450,
        _ => 200,
    };

    /// <summary>Whole gigabytes of video memory (8 GB cards report 8 589 934 592 bytes or a hair less).</summary>
    public static int? VramGb(long? bytes) =>
        bytes is > 0 ? (int)Math.Round(bytes.Value / (1024d * 1024 * 1024)) : null;

    /// <summary>
    /// VRAM against the screen: 4K wants 12 GB or more (8 GB is too little), 1440p wants more than 8 GB (8 GB is tight
    /// in new games), 1080p wants 8 GB (6 GB is tight, 4 GB or less too little).
    /// </summary>
    public static VramFit EvaluateVram(int? vramGb, int? screenWidth)
    {
        if (vramGb is not { } gb || screenWidth is not { } width || width <= 0)
        {
            return VramFit.Unknown;
        }

        if (width >= 3800)
        {
            return gb <= 8 ? VramFit.TooLittle : gb < 12 ? VramFit.Tight : VramFit.Fine;
        }

        if (width >= 2500)
        {
            return gb < 8 ? VramFit.TooLittle : gb <= 8 ? VramFit.Tight : VramFit.Fine;
        }

        return gb <= 4 ? VramFit.TooLittle : gb < 8 ? VramFit.Tight : VramFit.Fine;
    }

    /// <summary>True when the card runs with fewer PCIe lanes than it supports (x8 instead of x16, for example).</summary>
    public static bool IsLinkNarrowed(PcieLinkInfo? link) =>
        link is { CurrentWidth: { } current, MaxWidth: { } max } && current > 0 && current < max;

    /// <summary>"PCIe 4.0 x16" from the generation and width, or null.</summary>
    public static string? DescribeLink(int? gen, int? width)
    {
        string? version = gen switch { 1 => "1.1", 2 => "2.0", 3 => "3.0", 4 => "4.0", 5 => "5.0", 6 => "6.0", _ => null };
        return version is null && width is null ? null
            : version is null ? $"PCIe x{width}"
            : width is null ? $"PCIe {version}" : $"PCIe {version} x{width}";
    }

    private static int? BoardPowerOf(string upperName)
    {
        foreach (var (name, watts) in BoardPower.OrderByDescending(p => p.Name.Length))
        {
            if (Regex.IsMatch(upperName, @"(?<![A-Z0-9])" + Regex.Escape(name).Replace("\\ ", "\\s*", StringComparison.Ordinal) + @"(?![A-Z0-9])"))
            {
                return watts;
            }
        }

        return null;
    }

    private static GpuTier TierFromDigit(int digit, string name)
    {
        bool boosted = name.Contains(" TI", StringComparison.Ordinal) || name.Contains("SUPER", StringComparison.Ordinal)
            || name.Contains(" XT", StringComparison.Ordinal);
        return digit switch
        {
            >= 9 => GpuTier.Enthusiast,
            8 => GpuTier.High,
            7 => boosted ? GpuTier.High : GpuTier.UpperMid,
            6 => boosted ? GpuTier.UpperMid : GpuTier.Mainstream,
            5 => GpuTier.Entry,
            _ => GpuTier.Entry,
        };
    }

    private static string VendorOf(string upperName) =>
        upperName.Contains("NVIDIA", StringComparison.Ordinal) || upperName.Contains("GEFORCE", StringComparison.Ordinal) ? "NVIDIA"
        : upperName.Contains("AMD", StringComparison.Ordinal) || upperName.Contains("RADEON", StringComparison.Ordinal) ? "AMD"
        : upperName.Contains("INTEL", StringComparison.Ordinal) ? "Intel" : "Unknown";

    private static int Int(string text) => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
