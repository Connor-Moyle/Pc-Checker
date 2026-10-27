using System.Globalization;
using System.Text;

namespace PcChecker.Core.Shell;

/// <summary>
/// Scores how well a typed query matches a piece of interface text. Case, accents and punctuation are ignored.
/// From best to weakest: the whole text, the start of the text, every typed word at the start of (or inside) a word,
/// the initials of the words ("wf" for Windows Features), and finally the letters in order starting at a word.
/// Returns <see cref="NoMatch"/> (0) when the text does not match at all.
/// </summary>
public static class FuzzyMatcher
{
    public const int NoMatch = 0;
    public const int ExactScore = 1000;
    public const int PrefixScore = 900;
    public const int InitialsScore = 700;
    public const int InitialsPrefixScore = 650;
    public const int WordsBaseScore = 400;
    public const int SubsequenceMaxScore = 300;

    /// <summary>Lower-case, accents removed, every run of non-letters and non-digits turned into one space.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lower = text.ToLowerInvariant();
        string decomposed;
        try
        {
            decomposed = lower.Normalize(NormalizationForm.FormD);
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            decomposed = lower;
        }

        var sb = new StringBuilder(decomposed.Length);
        bool pendingSpace = false;
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                pendingSpace = false;
                sb.Append(c == 'ß' ? "ss" : c.ToString());
            }
            else
            {
                pendingSpace = true;
            }
        }

        return sb.ToString();
    }

    /// <summary>The words of already-normalized text.</summary>
    public static string[] Words(string normalized) =>
        normalized.Length == 0 ? [] : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The first letter of every word ("Keyboard &amp; Mouse" → "km").</summary>
    public static string Initials(string normalized) => string.Concat(Words(normalized).Select(w => w[0]));

    /// <summary>Scores <paramref name="query"/> against <paramref name="candidate"/>; 0 means no match.</summary>
    public static int Score(string? query, string? candidate) => ScoreNormalized(Normalize(query), Normalize(candidate));

    /// <summary>As <see cref="Score"/>, for text that has already been through <see cref="Normalize"/>.</summary>
    public static int ScoreNormalized(string query, string candidate)
    {
        if (query.Length == 0 || candidate.Length == 0)
        {
            return NoMatch;
        }

        if (candidate == query)
        {
            return ExactScore;
        }

        if (candidate.StartsWith(query, StringComparison.Ordinal))
        {
            // Shorter texts first: "Fans" before "Fans and lighting".
            return PrefixScore - Math.Min(99, candidate.Length - query.Length);
        }

        var words = Words(candidate);
        var tokens = Words(query);
        int best = NoMatch;

        int wordsScore = ScoreWords(tokens, words);
        if (wordsScore > best)
        {
            best = wordsScore;
        }

        var compactQuery = query.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compactQuery.Length >= 2 && words.Length >= 2)
        {
            var initials = string.Concat(words.Select(w => w[0]));
            if (initials == compactQuery)
            {
                best = Math.Max(best, InitialsScore);
            }
            else if (initials.StartsWith(compactQuery, StringComparison.Ordinal))
            {
                best = Math.Max(best, InitialsPrefixScore - (initials.Length - compactQuery.Length));
            }
        }

        if (best == NoMatch && compactQuery.Length >= 2)
        {
            best = ScoreSubsequence(compactQuery, words);
        }

        return best;
    }

    /// <summary>
    /// Every typed word must match a word of the text: whole word 100, start of a word 80, inside a word 40 (typed
    /// words of three letters or more only). The average is scaled into 400–600, a little less for long texts.
    /// </summary>
    private static int ScoreWords(string[] tokens, string[] words)
    {
        if (tokens.Length == 0 || words.Length == 0)
        {
            return NoMatch;
        }

        int total = 0;
        int lastIndex = -1;
        bool inOrder = true;
        foreach (var token in tokens)
        {
            int bestToken = 0, bestIndex = -1;
            for (int i = 0; i < words.Length; i++)
            {
                var w = words[i];
                int s = w == token ? 100
                    : w.StartsWith(token, StringComparison.Ordinal) ? 80
                    : token.Length >= 3 && w.Contains(token, StringComparison.Ordinal) ? 40
                    : 0;
                if (s > bestToken)
                {
                    bestToken = s;
                    bestIndex = i;
                }
            }

            if (bestToken == 0)
            {
                return NoMatch;
            }

            if (bestIndex < lastIndex)
            {
                inOrder = false;
            }

            lastIndex = bestIndex;
            total += bestToken;
        }

        int average = total / tokens.Length;
        int extraWords = Math.Max(0, words.Length - tokens.Length);
        return WordsBaseScore + (average * 2) + (inOrder ? 10 : 0) - Math.Min(50, extraWords * 5);
    }

    /// <summary>
    /// The typed letters appear in order, the first one at the start of a word ("tmpfl" in "Temporary files"). Tighter
    /// matches score higher, never above <see cref="SubsequenceMaxScore"/>.
    /// </summary>
    private static int ScoreSubsequence(string compactQuery, string[] words)
    {
        var text = string.Join(' ', words);
        int bestScore = NoMatch;
        for (int start = 0; start < text.Length; start++)
        {
            bool wordStart = start == 0 || text[start - 1] == ' ';
            if (!wordStart || text[start] != compactQuery[0])
            {
                continue;
            }

            int q = 1, pos = start + 1, gaps = 0;
            while (q < compactQuery.Length && pos < text.Length)
            {
                if (text[pos] == compactQuery[q])
                {
                    q++;
                }
                else if (text[pos] != ' ')
                {
                    gaps++;
                }

                pos++;
            }

            if (q < compactQuery.Length)
            {
                break; // later starts only see less of the text
            }

            int score = Math.Max(1, SubsequenceMaxScore - (gaps * 10) - (start * 2));
            bestScore = Math.Max(bestScore, Math.Min(SubsequenceMaxScore, score));
        }

        return bestScore;
    }
}
