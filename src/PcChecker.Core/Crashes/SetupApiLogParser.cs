using System.Globalization;

namespace PcChecker.Core.Crashes;

/// <summary>One device or driver-package install found in %windir%\INF\setupapi.dev.log.</summary>
public sealed record SetupApiInstall(DateTime LocalTime, string Title, bool Succeeded);

/// <summary>
/// Reads install sections from Windows' device setup log (setupapi.dev.log). The log is always English and its
/// section lines have a fixed shape:
/// <code>
/// &gt;&gt;&gt;  [Device Install (Hardware initiated) - PCI\VEN_10DE&amp;DEV_2684…]
/// &gt;&gt;&gt;  Section start 2026/10/20 14:22:31.123
/// &lt;&lt;&lt;  [Exit status: SUCCESS]
/// </code>
/// Only "Device Install" and "Install Driver Package" sections are kept: those are the moments a driver changed.
/// Pure; the caller reads only the end of the file.
/// </summary>
public static class SetupApiLogParser
{
    private const string Open = ">>>  [";
    private const string Start = ">>>  Section start ";
    private const string Exit = "<<<  [Exit status: ";

    public static IReadOnlyList<SetupApiInstall> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new List<SetupApiInstall>();
        string? title = null;
        DateTime? start = null;
        foreach (var raw in lines)
        {
            if (raw is null)
            {
                continue;
            }

            var line = raw.TrimEnd();
            if (line.StartsWith(Open, StringComparison.Ordinal) && line.EndsWith(']'))
            {
                Flush(succeeded: true);
                var t = line[Open.Length..^1].Trim();
                title = IsDriverChange(t) ? t : null;
                start = null;
            }
            else if (title is not null && line.StartsWith(Start, StringComparison.Ordinal))
            {
                start = ParseTime(line[Start.Length..]);
            }
            else if (title is not null && line.StartsWith(Exit, StringComparison.Ordinal))
            {
                bool ok = !line.Contains("FAILURE", StringComparison.OrdinalIgnoreCase);
                Flush(ok);
            }
        }

        Flush(succeeded: true);
        return result;

        void Flush(bool succeeded)
        {
            if (title is not null && start is { } when)
            {
                result.Add(new SetupApiInstall(when, title, succeeded));
            }

            title = null;
            start = null;
        }
    }

    private static bool IsDriverChange(string title) =>
        title.StartsWith("Device Install", StringComparison.OrdinalIgnoreCase)
        || title.StartsWith("Install Driver Package", StringComparison.OrdinalIgnoreCase)
        || title.StartsWith("Install Driver Update", StringComparison.OrdinalIgnoreCase);

    /// <summary>"2026/10/20 14:22:31.123" (local time) or null.</summary>
    public static DateTime? ParseTime(string text)
    {
        var v = text.Trim();
        return DateTime.TryParseExact(v, ["yyyy/MM/dd HH:mm:ss.fff", "yyyy/MM/dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;
    }

    /// <summary>The device or package an install title names: the text after " - ".</summary>
    public static string Subject(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        int dash = title.IndexOf(" - ", StringComparison.Ordinal);
        return dash >= 0 ? title[(dash + 3)..].Trim() : title;
    }
}
