using System.Text.RegularExpressions;

namespace PcChecker.Core.Common;

/// <summary>Tolerant version parsing and comparison ("31.0.15.3623", "2024.1", "v1.2", "1.2.3-beta").</summary>
public static partial class VersionText
{
    [GeneratedRegex(@"\d+(?:\.\d+)*")]
    private static partial Regex NumberRun();

    /// <summary>Tries to extract a <see cref="Version"/> from free-form text. Single numbers become "N.0".</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = NumberRun().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var parts = match.Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new int[Math.Min(4, Math.Max(2, parts.Length))];
        for (int i = 0; i < numbers.Length; i++)
        {
            if (i < parts.Length)
            {
                // Clamp absurdly long components rather than failing.
                numbers[i] = int.TryParse(parts[i], out var n) ? n : int.MaxValue;
            }
        }

        version = numbers.Length switch
        {
            2 => new Version(numbers[0], numbers[1]),
            3 => new Version(numbers[0], numbers[1], numbers[2]),
            _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
        return true;
    }

    /// <summary>
    /// Compares two version strings. Unparseable values sort before parseable ones; two unparseable values compare as text.
    /// Returns negative when a &lt; b, zero when equal, positive when a &gt; b.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        bool okA = TryParse(a, out var va);
        bool okB = TryParse(b, out var vb);
        if (okA && okB)
        {
            int c = Normalize(va).CompareTo(Normalize(vb));
            return c;
        }

        if (okA)
        {
            return 1;
        }

        if (okB)
        {
            return -1;
        }

        return string.Compare(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build, v.Revision < 0 ? 0 : v.Revision);
}
