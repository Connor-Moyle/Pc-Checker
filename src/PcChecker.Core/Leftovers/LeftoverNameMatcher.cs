using System.Globalization;
using System.Text;

namespace PcChecker.Core.Leftovers;

/// <summary>
/// Compares folder and registry key names with installed program and publisher names. Names are normalised first:
/// accents, version numbers, bitness markers ("x64", "64-bit"), text in brackets and company suffixes ("Inc.",
/// "Corporation", "GmbH") are dropped and CamelCase is split, so "VideoLAN", "Notepad++ (64-bit x64)" and
/// "Western Digital Technologies, Inc." compare by what they really name. A match only ever hides a finding, so the
/// rules lean towards matching: when in doubt, the folder is treated as belonging to something installed. Pure.
/// </summary>
public static class LeftoverNameMatcher
{
    /// <summary>Words that say nothing about which product a name is: legal forms, bitness, installer words.</summary>
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "inc", "incorporated", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "gmbh", "ag", "sa", "sas",
        "srl", "bv", "nv", "ab", "oy", "plc", "pty", "kg", "kk", "spa", "the", "x64", "x86", "amd64", "arm64", "win64",
        "win32", "bit", "edition", "version", "setup", "installer", "r", "tm", "c",
    };

    /// <summary>Words too common to identify a product on their own; they never make a match by themselves.</summary>
    private static readonly HashSet<string> GenericTokens = new(StringComparer.Ordinal)
    {
        "software", "technologies", "technology", "systems", "system", "solutions", "studio", "studios", "games", "game",
        "media", "player", "free", "pro", "plus", "antivirus", "security", "browser", "launcher", "service", "services",
        "tools", "tool", "utility", "utilities", "manager", "center", "centre", "client", "helper", "runtime", "files",
        "file", "data", "common", "shared", "windows", "microsoft", "program", "programs", "update", "updater", "cache",
        "logs", "temp", "settings", "config", "user", "users", "desktop", "app", "apps", "application", "applications",
        "driver", "drivers", "and", "for", "with", "online", "digital", "entertainment", "electronics", "international",
        "group", "labs", "lab", "project", "team", "community", "foundation", "development", "interactive", "network",
        "networks", "suite", "package", "packages", "components", "component", "support", "agent", "core", "web", "cloud",
        "home", "office", "local", "global",
    };

    /// <summary>The meaningful lower-case words of a name, in order (noise and version numbers removed).</summary>
    public static IReadOnlyList<string> Tokens(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var text = RemoveBracketed(RemoveAccents(name));
        var tokens = new List<string>();
        var word = new StringBuilder();
        foreach (char c in text + " ")
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
                continue;
            }

            AddWord(word.ToString(), tokens);
            word.Clear();
        }

        return tokens;
    }

    /// <summary>Drops a noise word ("GmbH", "x64", "2022") whole, otherwise splits its CamelCase parts and drops noise parts.</summary>
    private static void AddWord(string word, List<string> tokens)
    {
        if (word.Length == 0 || IsNoise(word.ToLowerInvariant()))
        {
            return;
        }

        int start = 0;
        for (int i = 1; i <= word.Length; i++)
        {
            if (i == word.Length || IsCamelBoundary(word, i))
            {
                var part = word[start..i].ToLowerInvariant();
                if (!IsNoise(part))
                {
                    tokens.Add(part);
                }

                start = i;
            }
        }
    }

    /// <summary>The tokens joined without spaces: "Notepad++ (64-bit x64)" → "notepad", "Battle.net" → "battlenet".</summary>
    public static string Compact(string? name) => string.Concat(Tokens(name));

    /// <summary>Tokens that can identify a product on their own (three letters or more, not generic, not a number).</summary>
    public static IReadOnlyList<string> SignificantTokens(string? name) =>
        Tokens(name).Where(t => t.Length >= 3 && !GenericTokens.Contains(t) && !t.All(char.IsAsciiDigit)).ToList();

    /// <summary>True when every token of the name is generic ("Common Files", "Shared", "Data"), or there is none.</summary>
    public static bool IsGenericName(string? name)
    {
        var tokens = Tokens(name);
        return tokens.Count == 0 || tokens.All(t => GenericTokens.Contains(t) || t.Length < 3 || t.All(char.IsAsciiDigit));
    }

    /// <summary>
    /// A close match: the same name once normalised, one name the start of the other ("Epic Games" and "Epic Games
    /// Launcher"), one containing the other (five letters or more), or a typo-sized difference on a long name.
    /// </summary>
    public static bool IsStrongMatch(string? candidate, string? reference)
    {
        var c = Compact(candidate);
        var r = Compact(reference);
        if (c.Length < 2 || r.Length < 2)
        {
            return false;
        }

        if (c.Equals(r, StringComparison.Ordinal))
        {
            return true;
        }

        if (c.Length >= 4 && r.Length >= 4 && (r.StartsWith(c, StringComparison.Ordinal) || c.StartsWith(r, StringComparison.Ordinal)))
        {
            return true;
        }

        if ((c.Length >= 5 && r.Contains(c, StringComparison.Ordinal)) || (r.Length >= 5 && c.Contains(r, StringComparison.Ordinal)))
        {
            return true;
        }

        int shorter = Math.Min(c.Length, r.Length);
        return shorter >= 6 && Distance(c, r, 2) <= (shorter >= 10 ? 2 : 1);
    }

    /// <summary>
    /// Any match, strong or loose: besides <see cref="IsStrongMatch"/>, a shared significant word ("Corsair" and
    /// "CORSAIR iCUE5 Software") or an acronym ("WD" for "Western Digital").
    /// </summary>
    public static bool IsMatch(string? candidate, string? reference)
    {
        if (IsStrongMatch(candidate, reference))
        {
            return true;
        }

        var c = SignificantTokens(candidate);
        var r = SignificantTokens(reference);
        if (c.Count > 0 && r.Count > 0 && c.Any(t => r.Contains(t, StringComparer.Ordinal)))
        {
            return true;
        }

        return IsAcronymOf(Compact(candidate), Tokens(reference)) || IsAcronymOf(Compact(reference), Tokens(candidate));
    }

    /// <summary>Levenshtein distance, stopping early once it exceeds <paramref name="limit"/> (returns limit + 1 then).</summary>
    public static int Distance(string a, string b, int limit)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return limit + 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            int rowMin = current[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > limit)
            {
                return limit + 1;
            }

            (previous, current) = (current, previous);
        }

        return Math.Min(previous[b.Length], limit + 1);
    }

    private static bool IsAcronymOf(string acronym, IReadOnlyList<string> tokens)
    {
        if (acronym.Length is < 2 or > 5 || tokens.Count < 2 || !acronym.All(char.IsAsciiLetter))
        {
            return false;
        }

        var initials = string.Concat(tokens.Where(t => t.Length > 0 && char.IsAsciiLetter(t[0])).Select(t => t[0]));
        return initials.Equals(acronym, StringComparison.Ordinal);
    }

    private static bool IsNoise(string token) => NoiseTokens.Contains(token) || IsVersionToken(token);

    /// <summary>Version numbers and years ("24", "2022", "v12", "5b1") are dropped; a single digit ("7" in 7-Zip) is kept.</summary>
    private static bool IsVersionToken(string token)
    {
        if (token.All(char.IsAsciiDigit))
        {
            return token.Length >= 2;
        }

        return token.Length >= 2 && token[0] == 'v' && token[1..].All(char.IsAsciiDigit);
    }

    /// <summary>A new word starts at "aB" and at the last capital of "ABc" ("VideoLAN" → Video LAN, "HTMLHelp" → HTML Help).</summary>
    private static bool IsCamelBoundary(string text, int i)
    {
        char prev = text[i - 1];
        char c = text[i];
        if (char.IsLower(prev) && char.IsUpper(c))
        {
            return true;
        }

        return char.IsUpper(prev) && char.IsUpper(c) && i + 1 < text.Length && char.IsLower(text[i + 1]) &&
               i >= 2 && char.IsUpper(text[i - 2]);
    }

    private static string RemoveBracketed(string text)
    {
        var sb = new StringBuilder(text.Length);
        int depth = 0;
        foreach (char c in text)
        {
            if (c is '(' or '[' or '{')
            {
                depth++;
                sb.Append(' ');
                continue;
            }

            if (c is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
                sb.Append(' ');
                continue;
            }

            if (depth == 0)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string RemoveAccents(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
