using System.Globalization;

namespace PcChecker.Core.Latency;

/// <summary>Parses <c>powercfg /q</c> output for the current AC and DC setting indexes. Pure.</summary>
public static class PowerCfgQueryParser
{
    /// <summary>Returns the AC and DC indexes found in the text, or nulls when missing.</summary>
    public static (int? Ac, int? Dc) ParseIndexes(string? output)
    {
        int? ac = null, dc = null;
        if (string.IsNullOrWhiteSpace(output))
        {
            return (ac, dc);
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains("AC Power Setting Index", StringComparison.OrdinalIgnoreCase))
            {
                ac ??= ParseValue(line);
            }
            else if (line.Contains("DC Power Setting Index", StringComparison.OrdinalIgnoreCase))
            {
                dc ??= ParseValue(line);
            }
        }

        return (ac, dc);
    }

    /// <summary>Extracts the active scheme GUID from <c>powercfg /getactivescheme</c>.</summary>
    public static (Guid? Id, string? Name) ParseActiveScheme(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return (null, null);
        }

        int start = output.IndexOf("GUID:", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return (null, null);
        }

        var rest = output[(start + 5)..].Trim();
        var token = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        Guid? id = token.Length > 0 && Guid.TryParse(token[0], out var g) ? g : null;
        string? name = null;
        int open = rest.IndexOf('(');
        int close = rest.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            name = rest[(open + 1)..close].Trim();
        }

        return (id, name);
    }

    private static int? ParseValue(string line)
    {
        int colon = line.LastIndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var text = line[(colon + 1)..].Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) ? hex : null;
        }

        return int.TryParse(text, out int dec) ? dec : null;
    }
}
