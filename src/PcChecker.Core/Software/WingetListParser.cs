namespace PcChecker.Core.Software;

/// <summary>One row of <c>winget list</c> / <c>winget upgrade</c> output.</summary>
public sealed record WingetEntry(string Name, string Id, string Version, string? Available, string? Source)
{
    public bool HasUpgrade => !string.IsNullOrWhiteSpace(Available);
}

/// <summary>
/// Pure parser for winget's fixed-width table. Column boundaries come from the header line ("Name  Id  Version
/// Available  Source"). winget translates the header, so when no English header is found the line just above the
/// "-----" separator is taken as the header and its words are read in winget's fixed column order.
/// </summary>
public static class WingetListParser
{
    private static readonly string[] ColumnNames = ["Name", "Id", "Version", "Available", "Source"];

    /// <summary>Parses the table; returns an empty list when the output has no recognisable header.</summary>
    public static IReadOnlyList<WingetEntry> Parse(string? output) => TryParse(output) ?? [];

    /// <summary>Parses the table; returns null when the output has no recognisable table (as opposed to an empty one).</summary>
    public static IReadOnlyList<WingetEntry>? TryParse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output.Split('\n').Select(VisibleText).ToArray();
        bool localized = false;
        int headerIndex = Array.FindIndex(lines, IsHeader);
        List<(string Name, int Start)> columns;
        if (headerIndex >= 0)
        {
            var header = lines[headerIndex];
            columns = [];
            foreach (var column in ColumnNames)
            {
                int start = IndexOfWord(header, column);
                if (start >= 0)
                {
                    columns.Add((column, start));
                }
            }

            columns.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        else
        {
            headerIndex = lines.Length < 2 ? -1 : Array.FindIndex(lines, 1, IsSeparator) - 1;
            if (headerIndex < 0 || string.IsNullOrWhiteSpace(lines[headerIndex]))
            {
                return null;
            }

            var starts = WordStarts(lines[headerIndex]);
            if (starts.Count > ColumnNames.Length)
            {
                return null;
            }

            columns = starts.Select((start, i) => (ColumnNames[i], start)).ToList();
            localized = true;
        }

        if (columns.Count < 3 || headerIndex + 1 >= lines.Length)
        {
            return null;
        }

        var entries = new List<WingetEntry>();
        bool inRows = false;
        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length > 0 && line.Trim().All(c => c == '-'))
            {
                continue;
            }

            if (line.Length == 0)
            {
                if (localized && inRows)
                {
                    break; // the translated summary and any second table come after a blank line
                }

                continue;
            }

            if (IsFooter(line) || (i + 1 < lines.Length && IsSeparator(lines[i + 1])))
            {
                break;
            }

            var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < columns.Count; c++)
            {
                int start = Math.Min(columns[c].Start, line.Length);
                int end = c + 1 < columns.Count ? Math.Min(columns[c + 1].Start, line.Length) : line.Length;
                cells[columns[c].Name] = end > start ? line[start..end].Trim() : string.Empty;
            }

            var name = cells.GetValueOrDefault("Name", string.Empty);
            var id = cells.GetValueOrDefault("Id", string.Empty);
            var version = cells.GetValueOrDefault("Version", string.Empty);
            if (name.Length == 0 && id.Length == 0)
            {
                continue;
            }

            if (localized && (id.Length == 0 || version.Length == 0))
            {
                continue; // a translated summary line such as "2 Aktualisierungen verfügbar."
            }

            inRows = true;
            entries.Add(new WingetEntry(
                name,
                id,
                version,
                NullIfEmpty(cells.GetValueOrDefault("Available")),
                NullIfEmpty(cells.GetValueOrDefault("Source"))));
        }

        return entries;
    }

    /// <summary>Keeps the rows that have a newer version available.</summary>
    public static IReadOnlyList<WingetEntry> UpgradesOnly(IEnumerable<WingetEntry> entries) =>
        entries.Where(e => e.HasUpgrade && !string.Equals(e.Available, e.Version, StringComparison.OrdinalIgnoreCase)).ToList();

    private static bool IsHeader(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return IndexOfWord(line, "Name") >= 0 && IndexOfWord(line, "Id") >= 0 && IndexOfWord(line, "Version") >= 0;
    }

    private static bool IsSeparator(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed.All(c => c == '-');
    }

    /// <summary>What a console shows for a line: a carriage return (winget's progress spinner) rewinds to column 0.</summary>
    private static string VisibleText(string line)
    {
        var text = line.TrimEnd('\r');
        int cr = text.LastIndexOf('\r');
        return cr >= 0 ? text[(cr + 1)..] : text;
    }

    private static List<int> WordStarts(string line)
    {
        var starts = new List<int>();
        for (int i = 0; i < line.Length; i++)
        {
            if (!char.IsWhiteSpace(line[i]) && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                starts.Add(i);
            }
        }

        return starts;
    }

    private static bool IsFooter(string line) =>
        line.Contains("upgrades available", StringComparison.OrdinalIgnoreCase)
        || line.Contains("package(s) have version numbers", StringComparison.OrdinalIgnoreCase)
        || line.Contains("No installed package found", StringComparison.OrdinalIgnoreCase)
        || line.Contains("No applicable update", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("The following packages", StringComparison.OrdinalIgnoreCase);

    private static int IndexOfWord(string line, string word)
    {
        int from = 0;
        while (from <= line.Length - word.Length)
        {
            int idx = line.IndexOf(word, from, StringComparison.Ordinal);
            if (idx < 0)
            {
                return -1;
            }

            bool startOk = idx == 0 || char.IsWhiteSpace(line[idx - 1]);
            bool endOk = idx + word.Length >= line.Length || char.IsWhiteSpace(line[idx + word.Length]);
            if (startOk && endOk)
            {
                return idx;
            }

            from = idx + 1;
        }

        return -1;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
