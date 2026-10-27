using System.Globalization;
using System.Text.RegularExpressions;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Memory;

/// <summary>One installed module as the speed rules see it.</summary>
public readonly record struct ModuleSpeed(int? ConfiguredMhz, int? SmbiosSpeedMhz, string? PartNumber);

/// <summary>The shared XMP/EXPO verdict the Memory and Overclock pages both use.</summary>
public sealed record MemorySpeedVerdict(string State, int? ConfiguredMhz, int? RatedMhz, bool RatedFromPartNumber, bool JedecOnly)
{
    public const string Enabled = "Enabled";
    public const string Disabled = "Disabled";
    public const string Unknown = "Unknown";
}

/// <summary>
/// Pure memory speed rules. Many AM4/AM5 boards report the JEDEC base speed of the SPD in SMBIOS (DDR5-4800/5600,
/// DDR4-2133/3200) instead of the EXPO/XMP rating, so "configured = rated" does not prove the profile is on. The
/// rating is also decoded from the part number (G.Skill F5-6000J…, Kingston KF560C36…, Corsair …M2B6000C36,
/// Crucial CP16G56C46U5) and a JEDEC speed with no faster rating found is reported as unknown.
/// </summary>
public static partial class MemorySpeedRules
{
    /// <summary>Speeds within this fraction of the rating count as "running at rated speed".</summary>
    public const double SpeedTolerance = 0.03;

    public static IReadOnlySet<int> Ddr4JedecMhz { get; } = new HashSet<int> { 1600, 1866, 2133, 2400, 2666, 2933, 3200 };

    public static IReadOnlySet<int> Ddr5JedecMhz { get; } = new HashSet<int> { 4000, 4400, 4800, 5200, 5600 };

    [GeneratedRegex(@"^F[345]-(?<s>\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex GSkill();

    [GeneratedRegex(@"^KF[345](?<s>\d{2})C\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex KingstonFury();

    [GeneratedRegex(@"X[345]M\d[A-Z](?<s>\d{4})C\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex Corsair();

    [GeneratedRegex(@"^(?:C[PT]|BL)\d*K?\d+G(?<s>\d{2})C\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex Crucial();

    [GeneratedRegex(@"(?<!\d)(?<s>\d{4})H?C\d{2}(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Generic();

    /// <summary>The rated MT/s encoded in a module part number, or null when the format is not recognised.</summary>
    public static int? RatedFromPartNumber(string? partNumber)
    {
        var p = (partNumber ?? string.Empty).Trim();
        if (p.Length < 6)
        {
            return null;
        }

        if (GSkill().Match(p) is { Success: true } g)
        {
            return Plausible(int.Parse(g.Groups["s"].Value, CultureInfo.InvariantCulture));
        }

        if (KingstonFury().Match(p) is { Success: true } k)
        {
            return Plausible(int.Parse(k.Groups["s"].Value, CultureInfo.InvariantCulture) * 100);
        }

        if (Corsair().Match(p) is { Success: true } c)
        {
            return Plausible(int.Parse(c.Groups["s"].Value, CultureInfo.InvariantCulture));
        }

        if (Crucial().Match(p) is { Success: true } cr)
        {
            return Plausible(int.Parse(cr.Groups["s"].Value, CultureInfo.InvariantCulture) * 100);
        }

        return Generic().Match(p) is { Success: true } x ? Plausible(int.Parse(x.Groups["s"].Value, CultureInfo.InvariantCulture)) : null;
    }

    /// <summary>True when <paramref name="mhz"/> is a JEDEC base speed of the memory type (both lists when the type is unknown).</summary>
    public static bool IsJedecSpeed(int mhz, string? memoryType)
    {
        bool ddr5 = memoryType?.Contains("DDR5", StringComparison.OrdinalIgnoreCase) == true;
        bool ddr4 = memoryType?.Contains("DDR4", StringComparison.OrdinalIgnoreCase) == true;
        bool Near(IReadOnlySet<int> set) => set.Any(j => Math.Abs(j - mhz) <= 10);
        return ddr5 ? Near(Ddr5JedecMhz) : ddr4 ? Near(Ddr4JedecMhz) : Near(Ddr5JedecMhz) || Near(Ddr4JedecMhz);
    }

    /// <summary>
    /// The verdict: the slowest module's configured speed against the highest rating (SMBIOS or part number).
    /// A JEDEC speed that matches SMBIOS with no faster rating found is Unknown ("check the BIOS"), not Enabled.
    /// </summary>
    public static MemorySpeedVerdict Evaluate(IEnumerable<ModuleSpeed> modules, string? memoryType)
    {
        ArgumentNullException.ThrowIfNull(modules);
        int? configured = null, smbios = null, part = null;
        foreach (var m in modules)
        {
            if (m.ConfiguredMhz is { } c && c > 0)
            {
                configured = configured is null ? c : Math.Min(configured.Value, c);
            }

            if (m.SmbiosSpeedMhz is { } s && s > 0)
            {
                smbios = smbios is null ? s : Math.Max(smbios.Value, s);
            }

            if (RatedFromPartNumber(m.PartNumber) is { } p)
            {
                part = part is null ? p : Math.Max(part.Value, p);
            }
        }

        int? rated = smbios is null && part is null ? null : Math.Max(smbios ?? 0, part ?? 0);
        bool fromPart = part is not null && part > (smbios ?? 0);
        if (configured is null || rated is null or 0)
        {
            return new MemorySpeedVerdict(MemorySpeedVerdict.Unknown, configured, rated, fromPart, false);
        }

        if (configured < rated * (1 - SpeedTolerance))
        {
            return new MemorySpeedVerdict(MemorySpeedVerdict.Disabled, configured, rated, fromPart, false);
        }

        bool jedecOnly = part is null && IsJedecSpeed(configured.Value, memoryType) && configured <= rated * (1 + SpeedTolerance);
        return new MemorySpeedVerdict(jedecOnly ? MemorySpeedVerdict.Unknown : MemorySpeedVerdict.Enabled, configured, rated, fromPart, jedecOnly);
    }

    /// <summary>
    /// Infinity Fabric guidance for a Ryzen desktop at the given memory speed (null for other CPUs): the 1:1 sweet
    /// spot (AM5 DDR5-6000 with UCLK = MEMCLK and FCLK about 2000–2100 MHz; AM4 DDR4-3600 with FCLK 1800 MHz).
    /// </summary>
    public static string? AmdFabricAdvice(string? cpuName, string? memoryType, int? configuredMhz)
    {
        var traits = CpuTraits.Parse(cpuName);
        if (!traits.IsAmd || configuredMhz is not { } mts || mts <= 0 || traits.FormFactor is CpuFormFactor.Laptop or CpuFormFactor.Handheld)
        {
            return null;
        }

        string type = memoryType ?? traits.MemoryType ?? string.Empty;
        if (traits.Socket == "AM5" || (type.Contains("DDR5", StringComparison.OrdinalIgnoreCase) && traits.Generation >= ZenGeneration.Zen4))
        {
            return mts switch
            {
                < 5900 => $"At {mts} MT/s the memory controller runs 1:1 with the memory (UCLK = MEMCLK). On Ryzen 7000/9000, DDR5-6000 with EXPO is the usual sweet spot and still runs 1:1.",
                <= 6100 => $"{mts} MT/s is the Ryzen 7000/9000 sweet spot: the memory controller runs 1:1 (UCLK = MEMCLK). Infinity Fabric (FCLK) at Auto or about 2000–2100 MHz.",
                <= 6400 => $"At {mts} MT/s many Ryzen 7000/9000 chips drop the memory controller to half speed (UCLK 1:2), which can be slower than DDR5-6000 1:1. If the BIOS allows, set \"UCLK DIV1 MODE\" to UCLK=MEMCLK and test stability, or use 6000.",
                _ => $"Above 6400 MT/s the memory controller runs at half speed (1:2). That only pays off at about 8000 MT/s; otherwise DDR5-6000 at 1:1 is faster.",
            };
        }

        if (traits.Socket == "AM4")
        {
            if (traits.Generation is ZenGeneration.Zen1 or ZenGeneration.ZenPlus)
            {
                return "On Ryzen 1000/2000 the Infinity Fabric runs at half the memory speed, so faster memory (up to about 3200–3466 MT/s) helps noticeably.";
            }

            return mts switch
            {
                <= 3600 => $"At {mts} MT/s the Infinity Fabric can run 1:1 (FCLK = {mts / 2} MHz). DDR4-3600 with FCLK 1800 MHz is the usual sweet spot on Ryzen 3000/5000.",
                <= 3800 => $"{mts} MT/s runs 1:1 only if the CPU's Infinity Fabric is stable at FCLK {mts / 2} MHz. WHEA bus/interconnect errors on the Health page mean it is not: go back to 3600 / 1800 MHz.",
                _ => $"Above about 3800 MT/s the Infinity Fabric decouples (2:1), which adds latency. DDR4-3600 to 3800 at 1:1 is usually faster.",
            };
        }

        return null;
    }

    /// <summary>The AM5 memory-training note for the first boot after EXPO, or null on other platforms.</summary>
    public static string? TrainingNote(string? cpuName, string? memoryType)
    {
        var traits = CpuTraits.Parse(cpuName);
        bool ddr5 = memoryType?.Contains("DDR5", StringComparison.OrdinalIgnoreCase) == true || traits.MemoryType == "DDR5";
        return traits.IsAmd && ddr5 && traits.FormFactor is not (CpuFormFactor.Laptop or CpuFormFactor.Handheld)
            ? "The first start after turning EXPO on can show a black screen for 1–3 minutes while the memory trains. Do not switch the PC off during that time. Turning on \"Memory Context Restore\" in the BIOS makes later starts quick."
            : null;
    }

    private static int? Plausible(int mts) => mts is >= 1333 and <= 12000 ? mts : null;
}
