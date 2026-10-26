using System.Globalization;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Overclocking;

/// <summary>One tunable reported by the Intel XTU command line.</summary>
public sealed record XtuTunable(int Id, string Name, string Value, string? Unit);

/// <summary>
/// Pure parser for Intel XTU CLI output. XTU versions format the tunable list differently
/// (<c>id : name : value</c>, <c>ID=.. Name=.. Value=..</c>, or tab separated), so the parser accepts all of them.
/// </summary>
public static partial class XtuOutputParser
{
    [GeneratedRegex(@"^\s*(?<id>\d+)\s*[:|\t]\s*(?<name>[^:|\t]+?)\s*[:|\t]\s*(?<value>[^:|\t]*?)\s*(?:[:|\t]\s*(?<unit>[^:|\t]*?)\s*)?$")]
    private static partial Regex Delimited();

    [GeneratedRegex(@"id\s*=\s*(?<id>\d+).*?name\s*=\s*""?(?<name>[^"",;]+?)""?\s*[,;].*?value\s*=\s*""?(?<value>[^"",;]*?)""?(?:\s*[,;].*?unit\s*=\s*""?(?<unit>[^"",;]*?)""?)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValue();

    public static IReadOnlyList<XtuTunable> Parse(string? output)
    {
        var result = new List<XtuTunable>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var kv = KeyValue().Match(line);
            if (kv.Success)
            {
                result.Add(new XtuTunable(int.Parse(kv.Groups["id"].Value, CultureInfo.InvariantCulture), kv.Groups["name"].Value.Trim(), kv.Groups["value"].Value.Trim(), Clean(kv.Groups["unit"].Value)));
                continue;
            }

            var d = Delimited().Match(line);
            if (d.Success && d.Groups["name"].Value.Trim().Length > 0)
            {
                result.Add(new XtuTunable(int.Parse(d.Groups["id"].Value, CultureInfo.InvariantCulture), d.Groups["name"].Value.Trim(), d.Groups["value"].Value.Trim(), Clean(d.Groups["unit"].Value)));
            }
        }

        return result;
    }

    /// <summary>Finds the tunable whose name matches one of the candidates (case-insensitive, whole-name or contains).</summary>
    public static XtuTunable? Find(IEnumerable<XtuTunable> tunables, params string[] nameCandidates)
    {
        ArgumentNullException.ThrowIfNull(tunables);
        var list = tunables.ToList();
        foreach (var candidate in nameCandidates)
        {
            var exact = list.FirstOrDefault(t => t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        foreach (var candidate in nameCandidates)
        {
            var contains = list.FirstOrDefault(t => t.Name.Contains(candidate, StringComparison.OrdinalIgnoreCase));
            if (contains is not null)
            {
                return contains;
            }
        }

        return null;
    }

    /// <summary>Names XTU has used for the all-core multiplier across versions.</summary>
    public static readonly string[] CoreRatioNames = ["Processor Core Ratio", "Performance Core Ratio", "Core Ratio", "Multi Core Ratio", "All Core Ratio"];

    /// <summary>Names XTU has used for the core voltage offset.</summary>
    public static readonly string[] VoltageOffsetNames = ["Core Voltage Offset", "Processor Core Voltage Offset", "Performance Core Voltage Offset"];

    /// <summary>Names XTU has used for the sustained (PL1) power limit.</summary>
    public static readonly string[] PowerLimitNames = ["Turbo Boost Power Max", "Processor Power Limit", "Long Duration Power Limit", "Package Power Limit 1"];

    /// <summary>Parses the numeric part of a value such as "53x", "-0.050 V" or "125 W".</summary>
    public static double? ParseNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var m = NumberPattern().Match(value);
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();

    private static string? Clean(string? unit) => string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
}
