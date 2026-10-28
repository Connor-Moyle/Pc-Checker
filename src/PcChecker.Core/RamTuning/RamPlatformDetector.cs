using System.Globalization;
using System.Text.RegularExpressions;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.RamTuning;

/// <summary>
/// Works out the memory platform from the processor name and the memory type. AMD goes through the shared
/// <see cref="CpuTraits"/> parser (generation, socket, X3D, laptop); Intel desktop names are read here because
/// <see cref="CpuTraits"/> only covers AMD. Pure and locale independent.
/// </summary>
public static partial class RamPlatformDetector
{
    // "13th Gen Intel(R) Core(TM) i7-13700K", "Intel(R) Core(TM) i9-14900KS", "Intel Core i5-12400F".
    [GeneratedRegex(@"\bi[3579]-(?<gen>1[0-4])(?<num>\d{2,3})(?<suffix>[A-Z]*\d?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntelCore();

    // "Intel(R) Core(TM) Ultra 9 285K", "Intel Core Ultra 7 265KF", "Intel(R) Core(TM) Ultra 7 155H".
    [GeneratedRegex(@"\bUltra\s+[3579]\s+(?<num>\d{3})(?<suffix>[A-Z]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntelUltra();

    /// <summary>The platform for <paramref name="cpuName"/> with <paramref name="memoryType"/> ("DDR4"/"DDR5") fitted.</summary>
    public static RamPlatform Detect(string? cpuName, string? memoryType)
    {
        var name = (cpuName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return RamPlatform.Unknown;
        }

        var traits = CpuTraits.Parse(name);
        if (traits.IsAmd)
        {
            return Amd(name, traits);
        }

        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || IntelCore().IsMatch(name) || IntelUltra().IsMatch(name))
        {
            return Intel(name, memoryType);
        }

        return RamPlatform.Unknown with { CpuName = name };
    }

    /// <summary>Intel's desktop generation in a Core i3/i5/i7/i9 name (12, 13, 14), or null.</summary>
    public static int? IntelGeneration(string? cpuName)
    {
        var m = IntelCore().Match(cpuName ?? string.Empty);
        return m.Success ? int.Parse(m.Groups["gen"].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>True for an Intel laptop suffix (H, HX, HK, U, P, V, Y).</summary>
    public static bool IsIntelMobileSuffix(string suffix) =>
        suffix.Length > 0 && suffix.ToUpperInvariant()[0] is 'H' or 'U' or 'P' or 'V' or 'Y';

    private static RamPlatform Amd(string name, CpuTraitsInfo traits)
    {
        var kind = traits.FormFactor switch
        {
            CpuFormFactor.Laptop or CpuFormFactor.Handheld => RamPlatformKind.Mobile,
            CpuFormFactor.Workstation => RamPlatformKind.OtherDesktop,
            _ when traits.Socket == "AM5" => RamPlatformKind.Am5,
            _ when traits.Socket == "AM4" && traits.Generation is ZenGeneration.Zen2 or ZenGeneration.Zen3 => RamPlatformKind.Am4,
            _ when traits.Socket == "AM4" => RamPlatformKind.Am4Legacy,
            _ => RamPlatformKind.OtherDesktop,
        };

        return new RamPlatform(kind, name, traits.Generation, null, traits.IsX3D, traits.Socket);
    }

    private static RamPlatform Intel(string name, string? memoryType)
    {
        if (IntelUltra().Match(name) is { Success: true } ultra)
        {
            int number = int.Parse(ultra.Groups["num"].Value, CultureInfo.InvariantCulture);
            string suffix = ultra.Groups["suffix"].Value;

            // Core Ultra 200S desktop parts: 2xx with no suffix, K, KF, F or T. Series 1 and the H/HX/V parts are laptops.
            bool desktop = number is >= 200 and < 300 && !IsIntelMobileSuffix(suffix);
            return new RamPlatform(desktop ? RamPlatformKind.IntelArrowLake : RamPlatformKind.Mobile, name, ZenGeneration.Unknown, null, false,
                desktop ? "LGA1851" : null);
        }

        if (IntelCore().Match(name) is { Success: true } core)
        {
            int gen = int.Parse(core.Groups["gen"].Value, CultureInfo.InvariantCulture);
            string suffix = core.Groups["suffix"].Value;
            if (IsIntelMobileSuffix(suffix))
            {
                return new RamPlatform(RamPlatformKind.Mobile, name, ZenGeneration.Unknown, gen, false, null);
            }

            if (gen is >= 12 and <= 14)
            {
                bool ddr4 = memoryType?.Contains("DDR4", StringComparison.OrdinalIgnoreCase) == true;
                return new RamPlatform(ddr4 ? RamPlatformKind.IntelLga1700Ddr4 : RamPlatformKind.IntelLga1700Ddr5, name, ZenGeneration.Unknown, gen, false, "LGA1700");
            }

            return new RamPlatform(RamPlatformKind.OtherDesktop, name, ZenGeneration.Unknown, gen, false, null);
        }

        return new RamPlatform(RamPlatformKind.OtherDesktop, name, ZenGeneration.Unknown, null, false, null);
    }
}
