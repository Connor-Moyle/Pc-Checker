using System.Text;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Assistant;

/// <summary>
/// Turns the light Markdown the assistant tends to produce into readable plain text for a WPF TextBlock:
/// headings become plain lines, emphasis markers and inline code ticks are removed, list markers are tidied
/// and code fences are dropped. Pure and unit-tested.
/// </summary>
public static partial class AssistantTextFormatter
{
    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])")]
    private static partial Regex Italic();

    [GeneratedRegex(@"(?<!\w)_(?!\s)(.+?)(?<!\s)_(?!\w)")]
    private static partial Regex UnderscoreItalic();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+")]
    private static partial Regex Bullet();

    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(markdown.Length);
        bool inFence = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                line = Heading().Replace(line, string.Empty);
                line = Bullet().Replace(line, "$1• ");
                line = Bold().Replace(line, "$1");
                line = Italic().Replace(line, "$1");
                line = UnderscoreItalic().Replace(line, "$1");
                line = InlineCode().Replace(line, "$1");
                line = Link().Replace(line, "$1 ($2)");
            }

            sb.Append(line.TrimEnd()).Append('\n');
        }

        // Collapse runs of blank lines and trim.
        var text = sb.ToString();
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
        {
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        }

        return text.Trim();
    }
}
