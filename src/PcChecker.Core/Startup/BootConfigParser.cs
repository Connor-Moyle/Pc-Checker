using System.Globalization;

namespace PcChecker.Core.Startup;

/// <summary>Facts parsed from <c>bcdedit /enum {bootmgr}</c>.</summary>
public sealed record BootManagerInfo(int? TimeoutSeconds, int EntryCount, bool MemoryDiagnosticScheduled, string? DefaultId, IReadOnlyList<string> DisplayOrder);

/// <summary>Facts parsed from <c>bcdedit /enum {current}</c>.</summary>
public sealed record BootLoaderInfo(string? Description, bool SafeMode, string? SafeModeKind, bool LegacyBootMenu, string? Identifier);

/// <summary>Parses bcdedit output and the boot-time event. Pure text handling so it can be unit-tested.</summary>
public static class BootConfigParser
{
    /// <summary>Splits bcdedit's "key   value" lines, folding indented continuation lines into the previous key (displayorder lists).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ParseEntries(string? output)
    {
        var entries = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return entries;
        }

        foreach (var raw in output.Replace("\r", string.Empty).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('-'))
            {
                continue;
            }

            if (char.IsWhiteSpace(raw[0]))
            {
                // Continuation of the previous key (e.g. a second displayorder entry).
                if (entries.Count > 0)
                {
                    var last = entries[^1];
                    entries[^1] = new KeyValuePair<string, string>(last.Key, last.Value + "\n" + raw.Trim());
                }

                continue;
            }

            int split = IndexOfWhitespace(raw);
            if (split < 0)
            {
                // Section title such as "Windows Boot Manager".
                continue;
            }

            string key = raw[..split].Trim();
            string value = raw[split..].Trim();
            if (key.Contains(' '))
            {
                continue;
            }

            entries.Add(new KeyValuePair<string, string>(key, value));
        }

        return entries;
    }

    public static BootManagerInfo ParseBootManager(string? output)
    {
        var entries = ParseEntries(output);
        int? timeout = null;
        string? defaultId = null;
        var display = new List<string>();
        bool memdiag = false;
        foreach (var (key, value) in entries)
        {
            switch (key.ToLowerInvariant())
            {
                case "timeout":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int t))
                    {
                        timeout = t;
                    }

                    break;
                case "default":
                    defaultId = value;
                    break;
                case "displayorder":
                    display.AddRange(value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "bootsequence":
                    memdiag |= value.Contains("{memdiag}", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        return new BootManagerInfo(timeout, display.Count, memdiag, defaultId, display);
    }

    public static BootLoaderInfo ParseBootLoader(string? output)
    {
        var entries = ParseEntries(output);
        string? description = null;
        string? identifier = null;
        string? safeboot = null;
        bool legacy = false;
        foreach (var (key, value) in entries)
        {
            switch (key.ToLowerInvariant())
            {
                case "description":
                    description = value;
                    break;
                case "identifier":
                    identifier = value;
                    break;
                case "safeboot":
                    safeboot = value;
                    break;
                case "bootmenupolicy":
                    legacy = value.Equals("Legacy", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        return new BootLoaderInfo(description, safeboot is not null, safeboot, legacy, identifier);
    }

    /// <summary>Boot time in seconds from the milliseconds value of Diagnostics-Performance event 100 ("BootTime"). Null when unparsable.</summary>
    public static double? ParseBootTimeSeconds(string? bootTimeMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(bootTimeMilliseconds))
        {
            return null;
        }

        var first = bootTimeMilliseconds.Trim().Split(['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null || !double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) || ms <= 0)
        {
            return null;
        }

        return Math.Round(ms / 1000.0, 1);
    }

    private static int IndexOfWhitespace(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
