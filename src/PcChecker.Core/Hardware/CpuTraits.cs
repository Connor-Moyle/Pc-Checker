using System.Globalization;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Hardware;

/// <summary>AMD Zen core generation (Unknown for Intel and anything the name does not reveal).</summary>
public enum ZenGeneration
{
    Unknown,
    Zen1,
    ZenPlus,
    Zen2,
    Zen3,
    Zen3Plus,
    Zen4,
    Zen5,
}

/// <summary>Where the processor lives.</summary>
public enum CpuFormFactor
{
    Unknown,
    Desktop,
    DesktopApu,
    Laptop,
    Handheld,
    Workstation,
}

/// <summary>How much of an X3D (3D V-Cache) chip can be tuned in the BIOS.</summary>
public enum X3dTuning
{
    NotX3d,

    /// <summary>Ryzen 5000X3D: no PBO, no Curve Optimizer on most boards.</summary>
    Locked,

    /// <summary>Ryzen 7000X3D: PBO and Curve Optimizer, no multiplier changes.</summary>
    PboAndCurveOptimizerOnly,

    /// <summary>Ryzen 9000X3D: fully unlocked.</summary>
    Unlocked,
}

/// <summary>Everything PC Checker derives from a processor name.</summary>
public sealed record CpuTraitsInfo(
    bool IsAmd,
    bool IsIntel,
    string? Model,
    ZenGeneration Generation,
    CpuFormFactor FormFactor,
    string? Socket,
    bool IsX3D,
    bool IsDualCcdX3D,
    X3dTuning X3dTuning,
    int? TjMaxC,
    int TctlOffsetC)
{
    public static CpuTraitsInfo Unknown { get; } = new(false, false, null, ZenGeneration.Unknown, CpuFormFactor.Unknown, null, false, false, X3dTuning.NotX3d, null, 0);

    /// <summary>Ryzen laptop, handheld or desktop APU: the parts ryzenadj supports (Dragon Range HX excluded).</summary>
    public bool IsRyzenMobileOrApu => IsAmd && !IsDragonRange && (FormFactor is CpuFormFactor.Laptop or CpuFormFactor.Handheld or CpuFormFactor.DesktopApu);

    /// <summary>Ryzen 7045/9055 HX laptop chips are desktop silicon; ryzenadj does not support them.</summary>
    public bool IsDragonRange { get; init; }

    /// <summary>Ryzen 7000/9000 desktop (AM5) parts that are not X3D: designed to boost until Tctl reaches 95 °C.</summary>
    public bool BoostsToTjMax => IsAmd && !IsX3D && (Generation is ZenGeneration.Zen4 or ZenGeneration.Zen5) && (FormFactor is CpuFormFactor.Desktop or CpuFormFactor.DesktopApu or CpuFormFactor.Workstation);

    /// <summary>The memory the platform uses (DDR4 on AM4, DDR5 on AM5), or null.</summary>
    public string? MemoryType => Socket switch
    {
        "AM4" => "DDR4",
        "AM5" => "DDR5",
        _ => null,
    };
}

/// <summary>
/// Pure parser of processor names such as "AMD Ryzen 9 9950X3D 16-Core Processor" (and the Win32_Processor
/// description "AMD64 Family 25 Model 97 Stepping 2") into the traits the AMD-specific rules need.
/// </summary>
public static partial class CpuTraits
{
    [GeneratedRegex(@"Ryzen\s+(?:Threadripper\s+(?:PRO\s+)?)?(?:\d\s+(?:PRO\s+)?)?(?<num>\d{4})(?<suffix>[A-Z0-9]*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClassicModel();

    [GeneratedRegex(@"Ryzen\s+AI\s+(?:(?<max>Max\+?)\s+)?(?:PRO\s+)?(?:\d\s+)?(?:(?<hx>HX)\s+)?(?:PRO\s+)?(?<num>\d{3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex AiModel();

    [GeneratedRegex(@"Ryzen\s+Z(?<z>[12])(?:\s+(?<variant>Extreme|Go))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex HandheldModel();

    [GeneratedRegex(@"Family\s+(?<f>\d+)\s+Model\s+(?<m>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FamilyModel();

    /// <summary>Parses <paramref name="name"/>; <paramref name="identifier"/> ("AMD64 Family 25 Model 97 …") fills gaps.</summary>
    public static CpuTraitsInfo Parse(string? name, string? identifier = null)
    {
        var n = (name ?? string.Empty).Trim();
        bool amd = n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)
            || (identifier ?? string.Empty).Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase)
            || (n.Length == 0 && (identifier ?? string.Empty).StartsWith("AMD64 Family", StringComparison.OrdinalIgnoreCase) && FamilyFromIdentifier(identifier) >= 23);
        bool intel = !amd && n.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        if (!amd)
        {
            return CpuTraitsInfo.Unknown with { IsIntel = intel };
        }

        bool threadripper = n.Contains("Threadripper", StringComparison.OrdinalIgnoreCase);
        var result = ParseAi(n) ?? ParseHandheld(n) ?? ParseClassic(n, threadripper);
        result ??= CpuTraitsInfo.Unknown with { IsAmd = true };
        if (result.Generation == ZenGeneration.Unknown && FromIdentifier(identifier) is { } gen)
        {
            result = result with { Generation = gen };
        }

        return result;
    }

    public static bool IsAmd(string? name) => Parse(name).IsAmd;

    public static bool IsX3D(string? name) => Parse(name).IsX3D;

    /// <summary>Ryzen 9 X3D chips with one V-Cache CCD and one frequency CCD (7900X3D, 7950X3D, 9900X3D, 9950X3D, 7945HX3D).</summary>
    public static bool IsDualCcdX3D(string? name) => Parse(name).IsDualCcdX3D;

    public static bool IsRyzenMobileOrApu(string? name) => Parse(name).IsRyzenMobileOrApu;

    /// <summary>Short platform line for the Hardware page, for example "Zen 4 · AM5 · 3D V-Cache".</summary>
    public static string? Describe(CpuTraitsInfo traits)
    {
        ArgumentNullException.ThrowIfNull(traits);
        if (!traits.IsAmd)
        {
            return null;
        }

        var parts = new List<string>();
        if (GenerationName(traits.Generation) is { } g)
        {
            parts.Add(g);
        }

        if (!string.IsNullOrEmpty(traits.Socket))
        {
            parts.Add(traits.Socket);
        }

        parts.Add(traits.FormFactor switch
        {
            CpuFormFactor.Laptop => "Laptop",
            CpuFormFactor.Handheld => "Handheld",
            CpuFormFactor.DesktopApu => "Desktop APU",
            CpuFormFactor.Workstation => "Workstation",
            _ => string.Empty,
        });
        if (traits.IsX3D)
        {
            parts.Add(traits.IsDualCcdX3D ? "3D V-Cache (two CCDs)" : "3D V-Cache");
        }

        var text = string.Join(" · ", parts.Where(p => p.Length > 0));
        return text.Length == 0 ? null : text;
    }

    public static string? GenerationName(ZenGeneration generation) => generation switch
    {
        ZenGeneration.Zen1 => "Zen",
        ZenGeneration.ZenPlus => "Zen+",
        ZenGeneration.Zen2 => "Zen 2",
        ZenGeneration.Zen3 => "Zen 3",
        ZenGeneration.Zen3Plus => "Zen 3+",
        ZenGeneration.Zen4 => "Zen 4",
        ZenGeneration.Zen5 => "Zen 5",
        _ => null,
    };

    /// <summary>Zen generation from a Win32_Processor description such as "AMD64 Family 25 Model 97 Stepping 2".</summary>
    public static ZenGeneration? FromIdentifier(string? identifier)
    {
        var m = FamilyModel().Match(identifier ?? string.Empty);
        if (!m.Success)
        {
            return null;
        }

        int family = int.Parse(m.Groups["f"].Value, CultureInfo.InvariantCulture);
        int model = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        return family switch
        {
            23 when model is 0x08 or 0x18 => ZenGeneration.ZenPlus,
            23 when model < 0x30 => ZenGeneration.Zen1,
            23 => ZenGeneration.Zen2,
            25 when model is (>= 0x10 and <= 0x1F) or (>= 0x60 and <= 0x7F) or >= 0xA0 => ZenGeneration.Zen4,
            25 when model is >= 0x40 and <= 0x4F => ZenGeneration.Zen3Plus,
            25 => ZenGeneration.Zen3,
            26 => ZenGeneration.Zen5,
            _ => null,
        };
    }

    private static int FamilyFromIdentifier(string? identifier)
    {
        var m = FamilyModel().Match(identifier ?? string.Empty);
        return m.Success ? int.Parse(m.Groups["f"].Value, CultureInfo.InvariantCulture) : 0;
    }

    private static CpuTraitsInfo? ParseAi(string name)
    {
        var m = AiModel().Match(name);
        if (!m.Success)
        {
            return null;
        }

        bool max = m.Groups["max"].Success;
        return new CpuTraitsInfo(true, false, (max ? "AI Max " : "AI ") + m.Groups["num"].Value, ZenGeneration.Zen5, CpuFormFactor.Laptop,
            max ? "FP11" : "FP8", false, false, X3dTuning.NotX3d, 100, 0);
    }

    private static CpuTraitsInfo? ParseHandheld(string name)
    {
        var m = HandheldModel().Match(name);
        if (!m.Success)
        {
            return null;
        }

        var variant = m.Groups["variant"].Value;
        var gen = m.Groups["z"].Value == "2" && variant.Equals("Extreme", StringComparison.OrdinalIgnoreCase) ? ZenGeneration.Zen5
            : m.Groups["z"].Value == "2" && variant.Equals("Go", StringComparison.OrdinalIgnoreCase) ? ZenGeneration.Zen3Plus
            : ZenGeneration.Zen4;
        var model = ("Z" + m.Groups["z"].Value + " " + variant).Trim();
        return new CpuTraitsInfo(true, false, model, gen, CpuFormFactor.Handheld, null, false, false, X3dTuning.NotX3d, 100, 0);
    }

    private static CpuTraitsInfo? ParseClassic(string name, bool threadripper)
    {
        var m = ClassicModel().Match(name);
        if (!m.Success)
        {
            return null;
        }

        int num = int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture);
        string suffix = m.Groups["suffix"].Value.ToUpperInvariant();
        string model = num.ToString(CultureInfo.InvariantCulture) + suffix;
        int series = num / 1000;
        int third = num / 10 % 10;
        bool x3d = suffix.EndsWith("X3D", StringComparison.Ordinal);
        bool ryzen9 = name.Contains("Ryzen 9", StringComparison.OrdinalIgnoreCase);

        if (threadripper)
        {
            var gen = series switch { 1 => ZenGeneration.Zen1, 2 => ZenGeneration.ZenPlus, 3 => ZenGeneration.Zen2, 5 => ZenGeneration.Zen3, 7 => ZenGeneration.Zen4, 9 => ZenGeneration.Zen5, _ => ZenGeneration.Unknown };
            var socket = series switch
            {
                1 or 2 => "TR4",
                3 when suffix.Contains("WX", StringComparison.Ordinal) => "sWRX8",
                3 => "sTRX4",
                5 => "sWRX8",
                7 or 9 => "sTR5",
                _ => null,
            };
            int offset = series is 1 or 2 ? 27 : 0;
            return new CpuTraitsInfo(true, false, model, gen, CpuFormFactor.Workstation, socket, false, false, X3dTuning.NotX3d, gen == ZenGeneration.Unknown ? null : 95, offset);
        }

        bool laptop = suffix.StartsWith('U') || suffix.StartsWith('H') || suffix is "HS" or "HX" or "HX3D";
        bool apu = !laptop && (suffix.StartsWith('G') || suffix == "GE");
        bool dragonRange = laptop && suffix.StartsWith("HX", StringComparison.Ordinal) && ((series == 7 && third == 4 && num % 10 == 5) || (series == 9 && third == 5 && num % 10 == 5));
        var form = laptop ? CpuFormFactor.Laptop : apu ? CpuFormFactor.DesktopApu : CpuFormFactor.Desktop;

        ZenGeneration generation;
        string? sock;
        if (laptop)
        {
            generation = series switch
            {
                2 or 3 => ZenGeneration.ZenPlus,
                4 => ZenGeneration.Zen2,
                5 => ZenGeneration.Zen3,
                6 => ZenGeneration.Zen3Plus,
                7 => third switch { 2 => ZenGeneration.Zen2, 3 => ZenGeneration.Zen3, 4 => ZenGeneration.Zen4, _ => ZenGeneration.Zen3Plus },
                8 => ZenGeneration.Zen4,
                9 => ZenGeneration.Zen5,
                _ => ZenGeneration.Unknown,
            };
            sock = dragonRange ? "FL1" : generation switch
            {
                ZenGeneration.ZenPlus => "FP5",
                ZenGeneration.Zen2 or ZenGeneration.Zen3 => "FP6",
                ZenGeneration.Zen3Plus => "FP7",
                ZenGeneration.Zen4 => "FP7/FP8",
                ZenGeneration.Zen5 => "FP8",
                _ => null,
            };
        }
        else
        {
            generation = series switch
            {
                1 => ZenGeneration.Zen1,
                2 => apu ? ZenGeneration.Zen1 : ZenGeneration.ZenPlus,
                3 => apu ? ZenGeneration.ZenPlus : ZenGeneration.Zen2,
                4 => ZenGeneration.Zen2,
                5 => ZenGeneration.Zen3,
                7 or 8 => ZenGeneration.Zen4,
                9 => ZenGeneration.Zen5,
                _ => ZenGeneration.Unknown,
            };
            sock = series switch
            {
                >= 1 and <= 5 => "AM4",
                7 or 8 or 9 => "AM5",
                _ => null,
            };
        }

        bool dualCcd = x3d && ryzen9;
        var tuning = !x3d ? X3dTuning.NotX3d
            : generation == ZenGeneration.Zen3 ? X3dTuning.Locked
            : generation == ZenGeneration.Zen4 ? X3dTuning.PboAndCurveOptimizerOnly
            : X3dTuning.Unlocked;

        return new CpuTraitsInfo(true, false, model, generation, form, sock, x3d, dualCcd, tuning,
            TjMax(generation, form, x3d, name), TctlOffset(model)) { IsDragonRange = dragonRange };
    }

    private static int? TjMax(ZenGeneration generation, CpuFormFactor form, bool x3d, string name)
    {
        if (form == CpuFormFactor.Laptop)
        {
            return generation switch
            {
                ZenGeneration.Zen3Plus or ZenGeneration.Zen4 or ZenGeneration.Zen5 => 100,
                ZenGeneration.Unknown => null,
                _ => 105,
            };
        }

        return generation switch
        {
            ZenGeneration.Zen5 => 95,
            ZenGeneration.Zen4 => x3d ? 89 : 95,
            ZenGeneration.Zen3 => x3d ? 90 : name.Contains("Ryzen 5", StringComparison.OrdinalIgnoreCase) ? 95 : 90,
            ZenGeneration.Unknown => null,
            _ => 95,
        };
    }

    /// <summary>
    /// The offset older Ryzen chips add to Tctl (the fan-control value) on top of the real die temperature
    /// (LibreHardwareMonitor's own table: 1600X/1700X/1800X +20 °C, 2700X +10 °C).
    /// </summary>
    private static int TctlOffset(string model) => model switch
    {
        "1600X" or "1700X" or "1800X" => 20,
        "2700X" => 10,
        _ => 0,
    };
}
