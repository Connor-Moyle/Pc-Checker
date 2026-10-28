using System.Globalization;
using System.Text.RegularExpressions;
using PcChecker.Core.Localization;

namespace PcChecker.Core.RamTuning;

/// <summary>
/// Guesses which DRAM chips (IC) a module uses from its part number, maker, size and the speed and timings on its
/// label. Retail kits rarely name the chip, so most answers are "likely" with the reason, and anything that several
/// chip makers could build is reported as unknown rather than guessed.
/// <para>
/// Sources: Crucial is Micron's consumer brand (crucial.com) and Micron's, SK hynix's and Samsung's own module part
/// numbers start with MT, HM and M3 (the makers' part number decoders). The speed/timing bins follow the
/// community-maintained chip lists (overclock.net "B-die finder" for DDR4; the r/overclocking and overclock.net DDR5 IC
/// threads): retail DDR5 at 6600 MT/s and above, 6400 CL32 and 6000 CL30 with tRCD 38 or lower are SK hynix, and DDR4
/// with a true latency (CL × 2000 / MT/s) of 8.75 ns or lower (3200 CL14, 3600 CL14/15, 4000 CL15/16) is Samsung
/// B-die. Corsair's label version (3.xx Micron, 4.xx Samsung, 5.xx SK hynix) is not visible to Windows.
/// </para>
/// </summary>
public static partial class RamIcCatalog
{
    /// <summary>B-die bins have a true latency at or below this (3200 CL14 is exactly 8.75 ns).</summary>
    public const double BDieMaxTrueLatencyNs = 8.75;

    [GeneratedRegex(@"^F5-(?<s>\d{4})J(?<cl>\d{2})(?<rcd>\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GSkillDdr5();

    [GeneratedRegex(@"^F[34]-(?<s>\d{4})C(?<cl>\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GSkillDdr4();

    [GeneratedRegex(@"^KF[3-5](?<s>\d{2})C(?<cl>\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Kingston();

    [GeneratedRegex(@"X[345]M\d[A-Z](?<s>\d{4})C(?<cl>\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Corsair();

    [GeneratedRegex(@"^(?:C[PT]|BL)\d*K?\d+G(?<s>\d{2})C(?<cl>\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Crucial();

    [GeneratedRegex(@"(?<!\d)(?<s>\d{4})H?C(?<cl>\d{2})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Generic();

    /// <summary>Rated speed, CAS latency and (G.Skill DDR5 only) tRCD from a part number.</summary>
    public static PartNumberTimings ParseTimings(string? partNumber)
    {
        var p = (partNumber ?? string.Empty).Trim();
        if (p.Length < 6)
        {
            return default;
        }

        if (GSkillDdr5().Match(p) is { Success: true } g5)
        {
            return new PartNumberTimings(Num(g5, "s"), Num(g5, "cl"), Num(g5, "rcd"));
        }

        if (GSkillDdr4().Match(p) is { Success: true } g4)
        {
            return new PartNumberTimings(Num(g4, "s"), Num(g4, "cl"), null);
        }

        if (Kingston().Match(p) is { Success: true } k)
        {
            return new PartNumberTimings(Num(k, "s") * 100, Num(k, "cl"), null);
        }

        if (Corsair().Match(p) is { Success: true } c)
        {
            return new PartNumberTimings(Num(c, "s"), Num(c, "cl"), null);
        }

        if (Crucial().Match(p) is { Success: true } cr)
        {
            return new PartNumberTimings(Num(cr, "s") * 100, Num(cr, "cl"), null);
        }

        return Generic().Match(p) is { Success: true } x ? new PartNumberTimings(Num(x, "s"), Num(x, "cl"), null) : default;
    }

    /// <summary>The brand behind a module from its maker field or part number ("G.Skill", "Crucial"…), or null.</summary>
    public static string? Brand(string? partNumber, string? manufacturer)
    {
        var p = (partNumber ?? string.Empty).Trim().ToUpperInvariant();
        var m = (manufacturer ?? string.Empty).Trim().ToUpperInvariant();
        if (p.StartsWith("F5-", StringComparison.Ordinal) || p.StartsWith("F4-", StringComparison.Ordinal) || m.Contains("G.SKILL") || m.Contains("G SKILL") || m.Contains("GSKILL") || m == "04CD")
        {
            return "G.Skill";
        }

        if (p.StartsWith("CMK", StringComparison.Ordinal) || p.StartsWith("CMH", StringComparison.Ordinal) || p.StartsWith("CMT", StringComparison.Ordinal)
            || p.StartsWith("CMW", StringComparison.Ordinal) || p.StartsWith("CMP", StringComparison.Ordinal) || p.StartsWith("CMN", StringComparison.Ordinal)
            || p.StartsWith("CMG", StringComparison.Ordinal) || p.StartsWith("CMSX", StringComparison.Ordinal) || m.Contains("CORSAIR"))
        {
            return "Corsair";
        }

        if (p.StartsWith("KF", StringComparison.Ordinal) || p.StartsWith("KVR", StringComparison.Ordinal) || p.StartsWith("KCP", StringComparison.Ordinal) || m.Contains("KINGSTON"))
        {
            return "Kingston";
        }

        if (Crucial().IsMatch(p) || m.Contains("CRUCIAL"))
        {
            return "Crucial";
        }

        if (p.StartsWith("MTC", StringComparison.Ordinal) || p.StartsWith("MTA", StringComparison.Ordinal) || m.Contains("MICRON"))
        {
            return "Micron";
        }

        if (p.StartsWith("HMC", StringComparison.Ordinal) || p.StartsWith("HMA", StringComparison.Ordinal) || m.Contains("HYNIX"))
        {
            return "SK hynix";
        }

        if (p.StartsWith("M32", StringComparison.Ordinal) || p.StartsWith("M37", StringComparison.Ordinal) || p.StartsWith("M47", StringComparison.Ordinal) || m.Contains("SAMSUNG"))
        {
            return "Samsung";
        }

        if (m.Contains("TEAM") || p.StartsWith("FF", StringComparison.Ordinal) || p.StartsWith("CTCE", StringComparison.Ordinal) || p.StartsWith("TF", StringComparison.Ordinal))
        {
            return "TeamGroup";
        }

        return null;
    }

    /// <summary>
    /// The chip guess for one module. <paramref name="moduleBytes"/> tells 24 Gbit DDR5 (24 and 48 GB modules) from
    /// 16 Gbit. Never claims more than the label shows.
    /// </summary>
    public static RamIcGuess Guess(string? partNumber, string? manufacturer, string? memoryType, long? moduleBytes, Translator? tr = null)
    {
        tr ??= Translator.English;
        string? brand = Brand(partNumber, manufacturer);
        var timings = ParseTimings(partNumber);
        bool ddr5 = memoryType?.Contains("DDR5", StringComparison.OrdinalIgnoreCase) == true
            || (partNumber ?? string.Empty).StartsWith("F5-", StringComparison.OrdinalIgnoreCase) || (partNumber ?? string.Empty).StartsWith("KF5", StringComparison.OrdinalIgnoreCase)
            || (partNumber ?? string.Empty).StartsWith("MTC", StringComparison.OrdinalIgnoreCase) || (partNumber ?? string.Empty).StartsWith("HMC", StringComparison.OrdinalIgnoreCase);
        bool ddr4 = !ddr5 && (memoryType?.Contains("DDR4", StringComparison.OrdinalIgnoreCase) == true
            || (partNumber ?? string.Empty).StartsWith("F4-", StringComparison.OrdinalIgnoreCase) || (partNumber ?? string.Empty).StartsWith("KF4", StringComparison.OrdinalIgnoreCase));
        bool gb24 = moduleBytes is { } b && (b == 24L << 30 || b == 48L << 30);

        // Brands that only use one chip maker.
        switch (brand)
        {
            case "Crucial" or "Micron":
                return ddr5
                    ? new RamIcGuess(DramIc.MicronDdr5, IcConfidence.Known, "Micron", tr.T("Crucial and Micron modules use Micron's own chips."))
                    : new RamIcGuess(DramIc.MicronDdr4, IcConfidence.Known, "Micron", tr.T("Crucial and Micron modules use Micron's own chips."));
            case "SK hynix" when ddr5:
                return Hynix(gb24, IcConfidence.Known, tr.T("SK hynix's own module part number."), tr);
            case "Samsung" when ddr5:
                return new RamIcGuess(DramIc.SamsungDdr5, IcConfidence.Known, "Samsung", tr.T("Samsung's own module part number."));
        }

        string corsairHint = brand == "Corsair"
            ? " " + tr.T("Corsair prints the chip maker in the version on the label: 3.xx Micron, 4.xx Samsung, 5.xx SK hynix.")
            : string.Empty;

        if (ddr5 && timings.RatedMts is { } mts && timings.Cl is { } cl)
        {
            bool tightRcd = timings.Trcd is null || timings.Trcd <= 38;
            if (mts >= 6600 || (mts >= 6400 && cl <= 32) || (mts >= 6000 && cl <= 30 && tightRcd))
            {
                return Hynix(gb24, IcConfidence.Likely,
                    tr.F("DDR5-{0} CL{1} kits are built with SK hynix chips; other chips do not reach these timings at stock voltage.", mts, cl) + corsairHint, tr);
            }
        }

        if (ddr4 && timings.RatedMts is { } mts4 && timings.Cl is { } cl4 && mts4 >= 3000)
        {
            double trueLatency = cl4 * 2000.0 / mts4;
            if (trueLatency <= BDieMaxTrueLatencyNs + 0.001)
            {
                return new RamIcGuess(DramIc.SamsungBDie, IcConfidence.Likely, "Samsung B-die",
                    tr.F("DDR4-{0} CL{1} is a Samsung B-die speed bin; other DDR4 chips do not reach it.", mts4, cl4) + corsairHint);
            }
        }

        return RamIcGuess.Unknown(tr.T("The label's speed and timings fit chips from more than one maker, so the chip is unknown.")
            + " " + tr.T("HWiNFO's memory section shows the chip maker (DRAM Manufacturer).") + corsairHint);
    }

    /// <summary>The DDR5 tRFC (refresh) to try for a chip, in nanoseconds: a safe start and a tighter value that is fine on most kits; null when PC Checker has no safe number.</summary>
    public static (int StartNs, int TightNs)? TrfcNs(DramIc ic) => ic switch
    {
        // SK hynix 16 Gbit (M/A-die): about 160 ns runs on most kits; Buildzoid's "low effort" Ryzen 7000 timings use
        // tRFC 500 at DDR5-6000 (167 ns). Start at 180 ns. JEDEC is 295 ns for 16 Gbit.
        DramIc.HynixDdr5Gen16 => (180, 160),

        // SK hynix 24 Gbit M-die (JEDEC 410 ns) has no well-documented easy value yet, so it stays on Auto.
        // Samsung 8 Gbit B-die (DDR4): 280 ns is a common, easy value at 1.4–1.5 V; 260 ns if stable.
        DramIc.SamsungBDie => (280, 260),
        _ => null,
    };

    /// <summary>A refresh time in nanoseconds as memory clocks at <paramref name="mts"/> (clock = MT/s ÷ 2), rounded up.</summary>
    public static int NsToClocks(double ns, int mts) => (int)Math.Ceiling(ns * mts / 2000.0);

    private static RamIcGuess Hynix(bool gb24, IcConfidence confidence, string reason, Translator tr) => gb24
        ? new RamIcGuess(DramIc.HynixDdr5Gen24, confidence, "SK hynix 24 Gbit (M-die)", reason)
        : new RamIcGuess(DramIc.HynixDdr5Gen16, confidence, tr.T("SK hynix 16 Gbit (M-die or A-die)"), reason);

    private static int Num(Match m, string group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
}
