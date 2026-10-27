using System.Text.RegularExpressions;

namespace PcChecker.Core.Power;

/// <summary>Parses the output of <c>powercfg /list</c> and <c>powercfg /getactivescheme</c>. Pure, testable.</summary>
public static partial class PowerPlanParser
{
    /// <summary>Well-known Windows plan ids, so the UI can describe them even when a localized name is unfamiliar.</summary>
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>The legacy "AMD Ryzen Balanced" plan older AMD chipset drivers installed.</summary>
    public static readonly Guid AmdRyzenBalanced = new("9897998c-92de-4669-853f-b7cd3ecb2790");

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)\s*(\*?)", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeLine();

    /// <summary>Parses every "Power Scheme GUID: … (Name) *" line. The trailing asterisk marks the active plan.</summary>
    public static IReadOnlyList<PowerPlanInfo> Parse(string? powercfgListOutput)
    {
        if (string.IsNullOrWhiteSpace(powercfgListOutput))
        {
            return [];
        }

        var plans = new List<PowerPlanInfo>();
        var seen = new HashSet<Guid>();
        foreach (var rawLine in powercfgListOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            var match = SchemeLine().Match(line);
            if (!match.Success || !Guid.TryParse(match.Groups[1].Value, out var id) || !seen.Add(id))
            {
                continue;
            }

            string name = match.Groups[2].Value.Trim();
            if (name.Length == 0)
            {
                name = DescribeKnown(id) ?? id.ToString();
            }

            plans.Add(new PowerPlanInfo(id, name, match.Groups[3].Value == "*"));
        }

        return plans;
    }

    /// <summary>Parses "Power Scheme GUID: … (Name)" from <c>powercfg /getactivescheme</c>; null when not found.</summary>
    public static PowerPlanInfo? ParseActive(string? getActiveSchemeOutput)
    {
        var plans = Parse(getActiveSchemeOutput);
        return plans.Count == 0 ? null : plans[0] with { IsActive = true };
    }

    /// <summary>
    /// True when the plan is the well-known plan <paramref name="knownId"/>. powercfg translates plan names
    /// ("Ausbalanciert", "Économie d'énergie"), so the GUID decides; the English name is only a fallback for an unknown
    /// GUID or a copied plan that kept the English name.
    /// </summary>
    public static bool IsPlan(Guid? id, string? name, Guid knownId, string englishName) =>
        id == knownId || (!string.IsNullOrEmpty(name) && name.Contains(englishName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Plain-English description of the well-known plans; null for custom ones.</summary>
    public static string? DescribeKnown(Guid id)
    {
        if (id == Balanced)
        {
            return "Balanced";
        }

        if (id == HighPerformance)
        {
            return "High performance";
        }

        if (id == PowerSaver)
        {
            return "Power saver";
        }

        if (id == UltimatePerformance)
        {
            return "Ultimate Performance";
        }

        if (id == AmdRyzenBalanced)
        {
            return "AMD Ryzen Balanced";
        }

        return null;
    }

    /// <summary>One sentence about what a plan does, for the selector's helper text.</summary>
    public static string Explain(Guid id)
    {
        if (id == Balanced)
        {
            return "Lets Windows scale speed up and down as needed. The best choice for most PCs and laptops.";
        }

        if (id == HighPerformance)
        {
            return "Keeps the processor ready at full speed. Slightly more power use and heat; can help games and heavy work.";
        }

        if (id == PowerSaver)
        {
            return "Favours battery life over speed. Good for light work on a laptop away from the charger.";
        }

        if (id == UltimatePerformance)
        {
            return "Removes small power-saving pauses for the lowest latency. Meant for workstations; uses the most power.";
        }

        if (id == AmdRyzenBalanced)
        {
            return "AMD's older plan for Ryzen 1000–3000. Current AMD chipset drivers tune the Windows Balanced plan instead, so Balanced is the better choice now.";
        }

        return "A custom plan created by you, your PC maker or another tool.";
    }
}
