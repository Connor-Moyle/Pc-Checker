using System.Globalization;
using System.Text;

namespace PcChecker.Core.Scheduling;

/// <summary>
/// Parses <c>schtasks /Query /TN &lt;name&gt; /FO CSV /V /NH</c>. Column headers and status words are translated on
/// non-English Windows, so fields are read by fixed position (the verbose CSV layout is the same in every language)
/// and dates are parsed with the user's culture first, then a few common ones.
/// </summary>
public static class SchtasksCsvParser
{
    // Verbose CSV columns (0-based): HostName, TaskName, Next Run Time, Status, Logon Mode, Last Run Time, Last Result,
    // Author, Task To Run, Start In, Comment, Scheduled Task State, Idle Time, Power Management, Run As User, ...
    public const int ColumnTaskName = 1;
    public const int ColumnNextRun = 2;
    public const int ColumnStatus = 3;
    public const int ColumnLastRun = 5;
    public const int ColumnLastResult = 6;
    public const int ColumnTaskToRun = 8;
    public const int MinimumColumns = 9;

    private static readonly string[] FallbackCultures = ["en-US", "en-GB", "de-DE", "fr-FR", "es-ES", "it-IT", "nl-NL", "pt-BR", "pl-PL", "ru-RU", "ja-JP", "zh-CN", "sv-SE"];

    /// <summary>Splits CSV text into rows of fields (quoted fields, doubled quotes and CRLF/LF line ends).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ParseCsv(string? text)
    {
        var rows = new List<IReadOnlyList<string>>();
        if (string.IsNullOrEmpty(text))
        {
            return rows;
        }

        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    any = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    field.Append(c);
                    any = true;
                    break;
            }
        }

        EndRow();
        return rows;

        void EndRow()
        {
            if (any || field.Length > 0)
            {
                fields.Add(field.ToString());
                rows.Add(fields.ToArray());
            }

            fields.Clear();
            field.Clear();
            any = false;
        }
    }

    /// <summary>
    /// Reads the first data row for <paramref name="taskName"/>. A task with several triggers prints one row per
    /// trigger; the first one carries the next run time. Returns a "missing" status when the output has no data row.
    /// </summary>
    public static ScheduledTaskStatus ParseQuery(string taskName, string? output, CultureInfo? culture = null)
    {
        foreach (var row in ParseCsv(output))
        {
            if (row.Count < MinimumColumns || IsHeader(row))
            {
                continue;
            }

            string? nextText = Clean(row[ColumnNextRun]);
            string? lastText = Clean(row[ColumnLastRun]);
            long? lastResult = long.TryParse(Clean(row[ColumnLastResult]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
            return new ScheduledTaskStatus(
                Clean(row[ColumnTaskName]) ?? taskName,
                true,
                Clean(row[ColumnStatus]),
                ParseDate(nextText, culture),
                ParseDate(lastText, culture),
                lastResult,
                Clean(row[ColumnTaskToRun]),
                nextText,
                lastText);
        }

        return ScheduledTaskStatus.Missing(taskName);
    }

    /// <summary>
    /// Parses a schtasks date. "N/A", "Never" and their translations return null (they contain no digits), as does the
    /// "11/30/1999" placeholder Task Scheduler shows for a task that has never run.
    /// </summary>
    public static DateTime? ParseDate(string? text, CultureInfo? culture = null)
    {
        var value = Clean(text);
        if (value is null || !value.Any(char.IsDigit))
        {
            return null;
        }

        foreach (var c in Cultures(culture))
        {
            if (DateTime.TryParse(value, c, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                return parsed.Year < 2000 ? null : parsed;
            }
        }

        return null;
    }

    private static IEnumerable<CultureInfo> Cultures(CultureInfo? first)
    {
        if (first is not null)
        {
            yield return first;
        }

        yield return CultureInfo.CurrentCulture;
        foreach (var name in FallbackCultures)
        {
            CultureInfo? c;
            try
            {
                c = CultureInfo.GetCultureInfo(name);
            }
            catch (CultureNotFoundException)
            {
                c = null;
            }

            if (c is not null)
            {
                yield return c;
            }
        }

        yield return CultureInfo.InvariantCulture;
    }

    private static bool IsHeader(IReadOnlyList<string> row) =>
        row[0].Equals("HostName", StringComparison.OrdinalIgnoreCase) || row[ColumnTaskName].Equals("TaskName", StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? value)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
