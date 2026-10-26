namespace PcChecker.Core.Software;

/// <summary>One row of <c>winget list</c> / <c>winget upgrade</c> output.</summary>
public sealed record WingetEntry(string Name, string Id, string Version, string? Available, string? Source)
{
    public bool HasUpgrade => !string.IsNullOrWhiteSpace(Available);
}

/// <summary>
/// Pure parser for winget's fixed-width table. Column boundaries come from the header line ("Name  Id  Version
/// Available  Source"), so localized or reordered columns still parse as long as the header words are English.
/// </summary>
public static class WingetListParser
{
    private static readonly string[] ColumnNames = ["Name", "Id", "Version", "Available", "Source"];

    /// <summary>Parses the table; returns an empty list when the output has no recognisable header.</summary>
    public static IReadOnlyList<WingetEntry> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var lines = output.Replace("\r", string.Empty).Split('\n');
        int headerIndex = Array.FindIndex(lines, IsHeader);
        if (headerIndex < 0 || headerIndex + 1 >= lines.Length)
        {
            return [];
        }

        var header = lines[headerIndex];
        var columns = new List<(string Name, int Start)>();
        foreach (var column in ColumnNames)
        {
            int start = IndexOfWord(header, column);
            if (start >= 0)
            {
                columns.Add((column, start));
            }
        }

        columns.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (columns.Count < 3)
        {
            return [];
        }

        var entries = new List<WingetEntry>();
        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0 || line.All(c => c == '-'))
            {
                continue;
            }

            if (IsFooter(line))
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
            if (name.Length == 0 && id.Length == 0)
            {
                continue;
            }

            entries.Add(new WingetEntry(
                name,
                id,
                cells.GetValueOrDefault("Version", string.Empty),
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
