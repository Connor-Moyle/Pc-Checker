namespace PcChecker.Core.Latency;

/// <summary>Parses <c>bcdedit /enum</c> output into a case-insensitive key → value map. Pure.</summary>
public static class BcdEditParser
{
    public static IReadOnlyDictionary<string, string> Parse(string? output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return map;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('-') || line.EndsWith(':'))
            {
                continue;
            }

            int split = line.IndexOfAny([' ', '\t']);
            if (split <= 0)
            {
                continue;
            }

            var key = line[..split].Trim();
            var value = line[split..].Trim();
            if (value.Length > 0 && !map.ContainsKey(key))
            {
                map[key] = value;
            }
        }

        return map;
    }

    /// <summary>Reads a Yes/No/true/false/1/0 value; null when the key is absent.</summary>
    public static bool? GetBool(IReadOnlyDictionary<string, string> values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var v))
        {
            return null;
        }

        return v.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "1" or "on" => true,
            "no" or "false" or "0" or "off" => false,
            _ => null,
        };
    }

    public static int? GetInt(IReadOnlyDictionary<string, string> values, string key) =>
        values is not null && values.TryGetValue(key, out var v) && int.TryParse(v.Trim(), out int n) ? n : null;
}
