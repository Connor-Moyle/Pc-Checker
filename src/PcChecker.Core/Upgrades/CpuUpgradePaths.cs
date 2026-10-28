using System.Globalization;
using System.Text.RegularExpressions;
using PcChecker.Core.Hardware;
using static PcChecker.Core.Upgrades.UpgradeText;

namespace PcChecker.Core.Upgrades;

/// <summary>What PC Checker works out about a processor from its name: maker, socket, generation and model.</summary>
public sealed record CpuIdentity(
    bool IsIntel,
    bool IsAmd,
    string? Socket,
    int? IntelGeneration,
    string? Model,
    bool IsLaptop,
    bool IsCoreUltra = false,
    ZenGeneration Zen = ZenGeneration.Unknown)
{
    public static CpuIdentity Unknown { get; } = new(false, false, null, null, null, false);
}

/// <summary>A drop-in processor for the same socket. <paramref name="Tier"/> is a rough rank inside the socket table.</summary>
public sealed record CpuOption(string Name, int Tier, bool ForGaming, bool ForWork);

/// <summary>The upgrade path of one socket: the drop-in options and what to know before swapping.</summary>
public sealed record SocketPath(string Socket, bool EndOfPlatform, string BeforeYouSwap, IReadOnlyList<CpuOption> Options);

/// <summary>
/// The curated same-socket CPU upgrade table (AM4, AM5, LGA1700, LGA1851 and the older Intel sockets) and the parser
/// that finds a processor's socket from its name. No prices: they change daily and differ by country.
/// </summary>
public static partial class CpuUpgradePaths
{
    public const string Am4 = "AM4";
    public const string Am5 = "AM5";
    public const string Lga1700 = "LGA1700";
    public const string Lga1851 = "LGA1851";
    public const string Lga1200 = "LGA1200";
    public const string Lga1151Coffee = "LGA1151 (300-series)";
    public const string Lga1151Skylake = "LGA1151 (100/200-series)";
    public const string Lga1150 = "LGA1150";
    public const string Lga1155 = "LGA1155";

    /// <summary>The first 13th/14th gen microcode that fixes the Vmin Shift instability (0x12B).</summary>
    public const int RaptorLakeFixedMicrocode = 0x12B;

    [GeneratedRegex(@"\bi([3579])-(\d{4,5})([A-Z]{0,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCore();

    [GeneratedRegex(@"\bCore\s+Ultra\s+([579])\s+(\d{3})([A-Z]{0,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelUltra();

    [GeneratedRegex(@"\bRyzen\s+([3579])\s+(?:PRO\s+)?(\d{4})(X3D|XT|X|GE|G|F)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenModel();

    [GeneratedRegex(@"\(R\)|\(TM\)|®|™", RegexOptions.IgnoreCase)]
    private static partial Regex Marks();

    private static readonly Dictionary<string, (int Tier, string Name)> KnownTiers = BuildKnownTiers();

    private static readonly IReadOnlyDictionary<string, SocketPath> Paths = new Dictionary<string, SocketPath>(StringComparer.OrdinalIgnoreCase)
    {
        [Am4] = new(Am4, true,
            T("Update the BIOS before you swap (Ryzen 5000 needs a 2021 or newer BIOS). Some 300-series boards never got support; check your board's CPU support list."),
            [
                new("Ryzen 7 5700X3D", 7, true, false),
                new("Ryzen 7 5800X3D", 8, true, false),
                new("Ryzen 9 5900X", 6, false, true),
                new("Ryzen 9 5950X", 7, false, true),
            ]),
        [Am5] = new(Am5, false,
            T("Update the BIOS before you swap: Ryzen 9000 chips need a 2024 or newer BIOS. Your memory and cooler carry over."),
            [
                new("Ryzen 7 7800X3D", 8, true, false),
                new("Ryzen 7 9800X3D", 9, true, false),
                new("Ryzen 9 9950X", 8, false, true),
                new("Ryzen 9 9950X3D", 9, true, true),
            ]),
        [Lga1700] = new(Lga1700, true,
            T("Update the BIOS first: 13th and 14th gen chips need microcode 0x12B or newer to avoid the known instability problem. 14th gen is the last generation for this socket."),
            [
                new("Core i5-14600K", 5, true, false),
                new("Core i7-14700K", 7, true, true),
                new("Core i9-14900K", 8, true, true),
            ]),
        [Lga1851] = new(Lga1851, false,
            T("Update the BIOS before you swap. Core Ultra 200S chips are strongest in work apps; for games an X3D Ryzen on AM5 is usually faster."),
            [
                new("Core Ultra 5 245K", 5, false, true),
                new("Core Ultra 7 265K", 6, true, true),
                new("Core Ultra 9 285K", 7, true, true),
            ]),
        [Lga1200] = new(Lga1200, true,
            T("11th gen needs a 500-series board, or a BIOS update on Z490/H470. H410 and B460 boards do not take 11th gen. A newer platform is a much bigger step."),
            [
                new("Core i7-11700K", 4, true, true),
                new("Core i9-10900K", 4, true, true),
                new("Core i9-11900K", 5, true, false),
            ]),
        [Lga1151Coffee] = new(Lga1151Coffee, true,
            T("Only 300-series boards (Z370, B360, H370, Z390…) take 8th and 9th gen chips; update the BIOS first. A newer platform is a much bigger step."),
            [
                new("Core i7-9700K", 4, true, false),
                new("Core i9-9900K", 5, true, true),
            ]),
        [Lga1151Skylake] = new(Lga1151Skylake, true,
            T("100/200-series boards stop at 7th gen. A drop-in upgrade gains little; a new board, CPU and memory is the real step."),
            [
                new("Core i7-7700K", 3, true, false),
            ]),
        [Lga1150] = new(Lga1150, true,
            T("This platform is about ten years old. A drop-in processor will not make a real difference; plan a new board, CPU and memory."), []),
        [Lga1155] = new(Lga1155, true,
            T("This platform is over a decade old. Plan a new board, CPU and memory."), []),
    };

    /// <summary>Every socket the table knows.</summary>
    public static IReadOnlyCollection<string> Sockets => Paths.Keys.ToList();

    /// <summary>The path for <paramref name="socket"/>, or null for a socket the table does not know.</summary>
    public static SocketPath? PathFor(string? socket) =>
        socket is not null && Paths.TryGetValue(socket, out var path) ? path : null;

    /// <summary>Works out maker, socket and generation from a processor name (and the WMI socket text, if it names one).</summary>
    public static CpuIdentity Identify(string? name, string? wmiSocket = null)
    {
        var clean = Marks().Replace(name ?? string.Empty, " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        if (clean.Length == 0)
        {
            return CpuIdentity.Unknown;
        }

        var traits = CpuTraits.Parse(clean);
        if (traits.IsAmd)
        {
            var ryzen = RyzenModel().Match(clean);
            string? model = ryzen.Success
                ? $"Ryzen {ryzen.Groups[1].Value} {ryzen.Groups[2].Value}{ryzen.Groups[3].Value.ToUpperInvariant()}"
                : null;
            bool laptop = traits.FormFactor is CpuFormFactor.Laptop or CpuFormFactor.Handheld;
            string? socket = traits.Socket ?? KnownSocket(wmiSocket);
            return new CpuIdentity(false, true, laptop ? null : socket, null, model, laptop, false, traits.Generation);
        }

        if (!clean.Contains("Intel", StringComparison.OrdinalIgnoreCase) && !IntelCore().IsMatch(clean) && !IntelUltra().IsMatch(clean))
        {
            return CpuIdentity.Unknown with { Socket = KnownSocket(wmiSocket) };
        }

        var ultra = IntelUltra().Match(clean);
        if (ultra.Success)
        {
            int number = int.Parse(ultra.Groups[2].Value, CultureInfo.InvariantCulture);
            string suffix = ultra.Groups[3].Value.ToUpperInvariant();
            bool desktop = number is >= 200 and < 300 && suffix is "K" or "KF" or "F" or "" && !clean.Contains("Mobile", StringComparison.OrdinalIgnoreCase);
            bool laptopUltra = !desktop;
            return new CpuIdentity(true, false, desktop ? Lga1851 : null, null,
                $"Core Ultra {ultra.Groups[1].Value} {number}{suffix}", laptopUltra, true);
        }

        var core = IntelCore().Match(clean);
        if (!core.Success)
        {
            return new CpuIdentity(true, false, KnownSocket(wmiSocket), null, null, false);
        }

        string digits = core.Groups[2].Value;
        string suffixCore = core.Groups[3].Value.ToUpperInvariant();
        int generation = digits.Length == 5 ? int.Parse(digits[..2], CultureInfo.InvariantCulture) : int.Parse(digits[..1], CultureInfo.InvariantCulture);
        bool isLaptop = suffixCore.Length > 0 && (suffixCore.StartsWith('H') || suffixCore.StartsWith('U') || suffixCore.StartsWith('P')
            || suffixCore.StartsWith('Y') || suffixCore.StartsWith('M') || suffixCore == "G1" || suffixCore.StartsWith("G7") || suffixCore.StartsWith("G4"));
        string? coreSocket = isLaptop ? null : generation switch
        {
            >= 12 and <= 14 => Lga1700,
            10 or 11 => Lga1200,
            8 or 9 => Lga1151Coffee,
            6 or 7 => Lga1151Skylake,
            4 or 5 => Lga1150,
            2 or 3 => Lga1155,
            _ => null,
        };
        return new CpuIdentity(true, false, coreSocket ?? (isLaptop ? null : KnownSocket(wmiSocket)), generation,
            $"Core i{core.Groups[1].Value}-{digits}{suffixCore}", isLaptop);
    }

    /// <summary>The rough tier of a known processor inside its socket table, or null when it is not in the table.</summary>
    public static int? TierOf(CpuIdentity cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        if (cpu.Model is null)
        {
            return null;
        }

        string key = Key(cpu.Model);
        if (KnownTiers.TryGetValue(key, out var exact))
        {
            return exact.Tier;
        }

        // "13700KF" ranks like "13700K".
        string trimmed = key.EndsWith('F') && !key.EndsWith("X3D", StringComparison.Ordinal) ? key[..^1] : key;
        return KnownTiers.TryGetValue(trimmed, out var near) ? near.Tier : null;
    }

    /// <summary>
    /// The drop-in upgrades for <paramref name="cpu"/>: the socket's options ranked above the current chip (all of them
    /// when the current chip is not in the table). Empty for laptops and unknown sockets.
    /// </summary>
    public static IReadOnlyList<CpuOption> UpgradesFrom(CpuIdentity cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        if (cpu.IsLaptop || PathFor(cpu.Socket) is not { } path)
        {
            return [];
        }

        int? current = TierOf(cpu);
        string currentKey = cpu.Model is null ? string.Empty : Key(cpu.Model);
        return path.Options
            .Where(o => Key(o.Name) != currentKey && (current is null || o.Tier > current))
            .OrderByDescending(o => o.ForGaming)
            .ThenBy(o => o.Tier)
            .ToList();
    }

    /// <summary>
    /// True for 13th and 14th gen desktop Core i5/i7/i9 chips whose microcode (as "0x12B") is older than the fix for
    /// the known instability. False when it is fixed or does not apply; null when the revision is unknown.
    /// </summary>
    public static bool? NeedsRaptorLakeMicrocode(CpuIdentity cpu, string? microcode)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        bool affected = cpu.IsIntel && !cpu.IsLaptop && cpu.IntelGeneration is 13 or 14
            && cpu.Model is { } m && !m.StartsWith("Core i3", StringComparison.OrdinalIgnoreCase);
        if (!affected)
        {
            return false;
        }

        var text = (microcode ?? string.Empty).Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var revision) && revision > 0
            ? revision < RaptorLakeFixedMicrocode
            : null;
    }

    /// <summary>True when Windows 11 lists this processor family as supported; null when unknown.</summary>
    public static bool? IsWindows11Supported(CpuIdentity cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        if (cpu.IsIntel)
        {
            if (cpu.IsCoreUltra)
            {
                return true;
            }

            return cpu.IntelGeneration is { } g ? g >= 8 : null;
        }

        if (cpu.IsAmd)
        {
            return cpu.Zen switch
            {
                ZenGeneration.Unknown => null,
                ZenGeneration.Zen1 => false,
                _ => true,
            };
        }

        return null;
    }

    private static string? KnownSocket(string? wmiSocket)
    {
        if (string.IsNullOrWhiteSpace(wmiSocket))
        {
            return null;
        }

        var compact = wmiSocket.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        foreach (var socket in new[] { Am4, Am5, Lga1700, Lga1851, Lga1200 })
        {
            if (compact.Contains(socket, StringComparison.Ordinal))
            {
                return socket;
            }
        }

        return null;
    }

    /// <summary>"Core i7-13700K" → "I13700K", "Ryzen 7 5800X3D" → "R5800X3D", "Core Ultra 9 285K" → "U285K".</summary>
    private static string Key(string model)
    {
        var upper = model.ToUpperInvariant();
        string last = upper.Split(' ')[^1];
        if (upper.StartsWith("CORE ULTRA", StringComparison.Ordinal))
        {
            return "U" + last;
        }

        if (upper.StartsWith("RYZEN", StringComparison.Ordinal))
        {
            return "R" + last;
        }

        int dash = upper.IndexOf('-', StringComparison.Ordinal);
        return "I" + (dash >= 0 ? upper[(dash + 1)..] : last);
    }

    private static Dictionary<string, (int, string)> BuildKnownTiers()
    {
        var tiers = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
        // Keys as Key() builds them: "R" for Ryzen, "I" for Core i, "U" for Core Ultra.
        void Add(string prefix, int tier, params string[] numbers)
        {
            foreach (var n in numbers)
            {
                tiers[prefix + n] = (tier, prefix + n);
            }
        }

        // AM4
        Add("R", 1, "1600", "1600X", "1700", "1700X", "1800X", "2600", "2600X");
        Add("R", 2, "2700", "2700X", "3600", "3600X", "3700X", "3800X", "3900X", "3950X");
        Add("R", 4, "5500", "5600", "5600X", "5600G", "5700G");
        Add("R", 5, "5700X", "5800X");
        Add("R", 6, "5900X");
        Add("R", 7, "5950X", "5700X3D");
        Add("R", 8, "5800X3D");
        // AM5
        Add("R", 4, "7600", "7600X", "8600G", "8700G");
        Add("R", 5, "9600X", "7700", "7700X", "9700X");
        Add("R", 6, "7900", "7900X", "9900X");
        Add("R", 7, "7950X", "7900X3D");
        Add("R", 8, "7800X3D", "9950X", "7950X3D", "9900X3D");
        Add("R", 9, "9800X3D", "9950X3D");
        // LGA1700
        Add("I", 1, "12100", "12100F", "13100", "13100F", "14100", "14100F");
        Add("I", 2, "12400", "12400F", "12500");
        Add("I", 3, "13400", "13400F", "14400", "14400F", "12600K", "12600KF");
        Add("I", 4, "13500", "14500", "12700", "12700K", "12700KF");
        Add("I", 5, "13600K", "13600KF", "14600K", "14600KF", "12900K", "12900KF", "13700");
        Add("I", 6, "13700K", "13700KF", "12900KS");
        Add("I", 7, "14700K", "14700KF", "13900K", "13900KF");
        Add("I", 8, "14900K", "14900KF", "13900KS", "14900KS");
        // LGA1851
        Add("", 4, "U225", "U225F", "U235");
        Add("", 5, "U245K", "U245KF", "U245");
        Add("", 6, "U265K", "U265KF", "U265", "U265F");
        Add("", 7, "U285K", "U285");
        // LGA1200
        Add("I", 1, "10100", "10400", "10400F", "11400", "11400F");
        Add("I", 2, "10600K", "10700", "11600K", "11500");
        Add("I", 3, "10700K", "10900", "11700");
        Add("I", 4, "10900K", "11700K");
        Add("I", 5, "11900K");
        // LGA1151 (300-series)
        Add("I", 1, "8100", "8400", "9100F", "9400", "9400F");
        Add("I", 2, "8600K", "9600K", "8700");
        Add("I", 3, "8700K", "9700");
        Add("I", 4, "9700K", "9700KF");
        Add("I", 5, "9900K", "9900KF", "9900KS");
        // LGA1151 (100/200-series)
        Add("I", 1, "6100", "7100", "6400", "6500", "7400", "7500");
        Add("I", 2, "6600K", "7600K", "6700");
        Add("I", 3, "6700K", "7700", "7700K");
        return tiers;
    }
}
