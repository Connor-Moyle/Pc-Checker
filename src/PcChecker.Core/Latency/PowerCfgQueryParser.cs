using System.Globalization;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Latency;

/// <summary>
/// Parses <c>powercfg /q</c> output for the current AC and DC setting indexes. Pure. powercfg prints localized labels
/// ("Current AC Power Setting Index", "Aktueller Index der Netzstromeinstellung", …), so the parser goes by shape: the
/// current indexes are the last two lines whose value is an eight-digit hex number, printed AC first, then DC. Range
/// settings (core parking, for example) print their "Minimum/Maximum Possible Setting" and "Possible Settings increment"
/// as eight-digit hex values too, but always before the current indexes.
/// </summary>
public static partial class PowerCfgQueryParser
{
    [GeneratedRegex(@"^\s*0x[0-9A-Fa-f]{8}\s*$")]
    private static partial Regex HexIndex();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidToken();

    /// <summary>Returns the AC and DC indexes found in the text, or nulls when missing.</summary>
    public static (int? Ac, int? Dc) ParseIndexes(string? output)
    {
        int? ac = null, dc = null;
        if (string.IsNullOrWhiteSpace(output))
        {
            return (ac, dc);
        }

        // English labels first (they also allow decimal values), then the locale-independent shape.
        var hexValues = new List<int>();
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
            else if (IsCurrentIndexLine(line) && ParseValue(line) is { } hex)
            {
                hexValues.Add(hex);
            }
        }

        if (ac is null && dc is null && hexValues.Count >= 2)
        {
            // The current AC and DC indexes always come last; earlier hex lines are the possible minimum, maximum and increment.
            ac = hexValues[^2];
            dc = hexValues[^1];
        }

        return (ac, dc);
    }

    /// <summary>
    /// Extracts the active scheme GUID from <c>powercfg /getactivescheme</c>. The label is localized ("Power Scheme GUID:",
    /// "GUID du mode de gestion de l'alimentation :"), so the first GUID in the text is taken, and the name is whatever
    /// sits in the last pair of parentheses after it.
    /// </summary>
    public static (Guid? Id, string? Name) ParseActiveScheme(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return (null, null);
        }

        var match = GuidToken().Match(output);
        if (!match.Success || !Guid.TryParse(match.Value, out var g))
        {
            return (null, null);
        }

        var rest = output[(match.Index + match.Length)..];
        string? name = null;
        int open = rest.IndexOf('(');
        int close = rest.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            name = rest[(open + 1)..close].Trim();
        }

        return (g, name);
    }

    /// <summary>True for a "Current … Index: 0x00000001" line in any language: a label, a colon and an 8-digit hex value.</summary>
    private static bool IsCurrentIndexLine(string line)
    {
        int colon = line.LastIndexOf(':');
        return colon > 0 && HexIndex().IsMatch(line[(colon + 1)..]);
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
