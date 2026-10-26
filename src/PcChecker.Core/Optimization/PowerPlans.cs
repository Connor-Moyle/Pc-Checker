namespace PcChecker.Core.Optimization;

/// <summary>One Windows power scheme as listed by <c>powercfg /list</c>.</summary>
public sealed record PowerScheme(Guid Id, string Name, bool IsActive);

/// <summary>Well-known power plan ids plus pure parsers for <c>powercfg</c> output.</summary>
public static class PowerPlans
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static string NameFor(Guid id)
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

        if (id == Ultimate)
        {
            return "Ultimate Performance";
        }

        return "Custom plan";
    }

    /// <summary>Parses a single <c>Power Scheme GUID: &lt;guid&gt;  (Name) *</c> line (from /getactivescheme or /duplicatescheme).</summary>
    public static PowerScheme? ParseScheme(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        int guidStart = line.IndexOf("GUID:", StringComparison.OrdinalIgnoreCase);
        var rest = (guidStart >= 0 ? line[(guidStart + 5)..] : line).Trim();
        var token = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (token.Length == 0 || !Guid.TryParse(token[0], out var id))
        {
            return null;
        }

        string name = NameFor(id);
        int open = rest.IndexOf('(');
        int close = rest.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            var text = rest[(open + 1)..close].Trim();
            if (text.Length > 0)
            {
                name = text;
            }
        }

        bool active = rest.TrimEnd().EndsWith('*');
        return new PowerScheme(id, name, active);
    }

    /// <summary>Parses the whole <c>powercfg /list</c> output.</summary>
    public static IReadOnlyList<PowerScheme> ParseList(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var list = new List<PowerScheme>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains("GUID:", StringComparison.OrdinalIgnoreCase) && ParseScheme(line) is { } scheme)
            {
                list.Add(scheme);
            }
        }

        return list;
    }

    /// <summary>True when the plan name denotes a high-performance style plan.</summary>
    public static bool IsHighPerformanceName(string? name) =>
        name is not null && (name.Contains("High performance", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
}
