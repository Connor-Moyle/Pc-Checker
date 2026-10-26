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

    /// <summary>
    /// The words bcdedit prints for boolean elements. It localizes Yes/No to the Windows display language, so the
    /// common translations are accepted alongside the English, numeric and on/off spellings.
    /// </summary>
    private static readonly HashSet<string> YesWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "true", "1", "on",
        "ja", "oui", "sí", "si", "sim", "да", "так", "はい", "是", "예", "tak", "ano", "áno", "evet", "kyllä", "igen", "da",
        "jā", "taip", "jah", "ναι", "כן", "نعم", "हाँ", "ใช่", "có",
    };

    private static readonly HashSet<string> NoWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "false", "0", "off",
        "nein", "non", "não", "nao", "нет", "ні", "не", "いいえ", "否", "아니요", "아니오", "nie", "ne", "hayır", "ei", "nem", "nu",
        "nej", "nei", "nee", "nē", "όχι", "לא", "لا", "नहीं", "ไม่ใช่", "không",
    };

    /// <summary>Reads a Yes/No/true/false/1/0 value (in English or a localized spelling); null when the key is absent or the word is not recognised.</summary>
    public static bool? GetBool(IReadOnlyDictionary<string, string> values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var v))
        {
            return null;
        }

        var word = v.Trim();
        if (YesWords.Contains(word))
        {
            return true;
        }

        return NoWords.Contains(word) ? false : null;
    }

    /// <summary>
    /// Reads a boolean element that bcdedit omits when it is not set: false when the key is absent, its value when the
    /// word is recognised, and null when the key is present but the word is not (an unknown translation), so an
    /// unreadable value is reported as unknown instead of as "off".
    /// </summary>
    public static bool? GetFlag(IReadOnlyDictionary<string, string> values, string key) =>
        values is null || !values.ContainsKey(key) ? false : GetBool(values, key);

    public static int? GetInt(IReadOnlyDictionary<string, string> values, string key) =>
        values is not null && values.TryGetValue(key, out var v) && int.TryParse(v.Trim(), out int n) ? n : null;
}
