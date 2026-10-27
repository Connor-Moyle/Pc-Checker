using System.Globalization;

namespace PcChecker.Core.Cleanup;

/// <summary>What DISM /AnalyzeComponentStore reported. Null fields were missing or could not be read.</summary>
public sealed record ComponentStoreAnalysis(
    long? ActualSizeBytes,
    long? BackupsBytes,
    long? CacheBytes,
    int? ReclaimablePackages)
{
    /// <summary>
    /// A rough estimate of what /StartComponentCleanup can free: the backups and disabled features plus the cache and
    /// temporary data, or just the cache when DISM sees no reclaimable packages.
    /// </summary>
    public long EstimatedReclaimableBytes =>
        (ReclaimablePackages is > 0 ? BackupsBytes ?? 0 : 0) + (CacheBytes ?? 0);

    public bool CleanupRecommended => ReclaimablePackages is > 0;
}

/// <summary>Pure parsing and planning for the command categories (DISM, Disk Cleanup handlers, restore points).</summary>
public static class CleanupCommandRules
{
    /// <summary>The StateFlags number PC Checker uses for cleanmgr /sagerun (any value 0-9999 works; this one is ours).</summary>
    public const int SageRunNumber = 7316;

    /// <summary>Where Disk Cleanup handlers are registered.</summary>
    public const string VolumeCachesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    /// <summary>The value name cleanmgr reads for /sagerun:<paramref name="number"/> ("StateFlags0042").</summary>
    public static string StateFlagsValueName(int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 9999);
        return "StateFlags" + number.ToString("D4", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The handlers to flag: those of <paramref name="wanted"/> that exist on this build (the set differs between
    /// Windows versions), spelled as the registry spells them. Device Driver Packages and Language Pack are never
    /// flagged: the Drivers page and Windows' language settings handle those.
    /// </summary>
    public static IReadOnlyList<string> HandlersToRun(IEnumerable<string> wanted, IEnumerable<string> available)
    {
        var present = (available ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        var never = new[] { "Device Driver Packages", "Language Pack" };
        return (wanted ?? [])
            .Where(w => !never.Contains(w, StringComparer.OrdinalIgnoreCase))
            .Select(w => present.FirstOrDefault(p => p.Equals(w, StringComparison.OrdinalIgnoreCase)))
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads DISM /Online /Cleanup-Image /AnalyzeComponentStore output in any display language. DISM prints the same
    /// eight "name : value" lines in the same order everywhere (Explorer size, actual size, shared with Windows,
    /// backups and disabled features, cache and temporary data, date of last cleanup, reclaimable packages, cleanup
    /// recommended), so they are read by position rather than by their translated names.
    /// </summary>
    public static ComponentStoreAnalysis? ParseAnalyzeComponentStore(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var values = output.Split('\n')
            .Select(l => l.Trim('\r', ' ', '\t'))
            .Where(l => l.Contains(" : ", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf(" : ", StringComparison.Ordinal) + 3)..].Trim())
            .ToList();
        if (values.Count < 8)
        {
            return null;
        }

        // Use the last eight in case a tool version prints an extra line first.
        var v = values.Skip(values.Count - 8).ToList();
        int? packages = int.TryParse(new string(v[6].Where(char.IsAsciiDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && v[6].Any(char.IsAsciiDigit)
            ? n
            : null;
        return new ComponentStoreAnalysis(ParseSize(v[1]), ParseSize(v[3]), ParseSize(v[4]), packages);
    }

    /// <summary>
    /// Parses a size such as "8.27 GB", "8,27 GB", "512 MB", "1,5 Go" or "199,25 МБ" into bytes. Accepts either decimal
    /// separator and English, French and Cyrillic unit letters. Null when unreadable.
    /// </summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim().Replace(' ', ' ').Replace(' ', ' ');
        int i = 0;
        while (i < t.Length && (char.IsAsciiDigit(t[i]) || t[i] is '.' or ',' or ' ' or '\''))
        {
            i++;
        }

        var number = t[..i].Replace(" ", string.Empty).Replace("'", string.Empty);
        var unit = t[i..].Trim();
        if (number.Length == 0 || !number.Any(char.IsAsciiDigit))
        {
            return null;
        }

        // The last separator is the decimal one when two digits or fewer follow it (DISM prints two decimals);
        // any other separator groups thousands.
        int last = number.LastIndexOfAny(['.', ',']);
        string integerPart = number;
        string fraction = string.Empty;
        if (last >= 0 && number.Length - last - 1 <= 2)
        {
            integerPart = number[..last];
            fraction = number[(last + 1)..];
        }

        integerPart = integerPart.Replace(".", string.Empty).Replace(",", string.Empty);
        if (!decimal.TryParse(integerPart.Length == 0 ? "0" : integerPart, NumberStyles.None, CultureInfo.InvariantCulture, out var whole))
        {
            return null;
        }

        decimal value = whole;
        if (fraction.Length > 0 && decimal.TryParse(fraction, NumberStyles.None, CultureInfo.InvariantCulture, out var f))
        {
            value += f / (decimal)Math.Pow(10, fraction.Length);
        }

        long multiplier = unit.Length == 0 ? 1 : char.ToUpperInvariant(unit[0]) switch
        {
            'K' or 'К' => 1024L,
            'M' or 'М' => 1024L * 1024,
            'G' or 'Г' => 1024L * 1024 * 1024,
            'T' or 'Т' => 1024L * 1024 * 1024 * 1024,
            _ => 1,
        };
        return (long)Math.Round(value * multiplier);
    }

    /// <summary>How many deletions keep exactly the newest restore point (none when there is one or none).</summary>
    public static int RestorePointDeletionsNeeded(int restorePointCount) => Math.Max(0, restorePointCount - 1);

    /// <summary>
    /// The System Restore sequence numbers to remove so that only the newest point stays, oldest first. Only points
    /// the <c>SystemRestore</c> class lists are ever named, so shadow copies made by backup software are never touched.
    /// </summary>
    public static IReadOnlyList<uint> RestorePointsToRemove(IEnumerable<uint> sequenceNumbers)
    {
        var all = (sequenceNumbers ?? []).Distinct().OrderBy(n => n).ToList();
        return all.Count <= 1 ? [] : all.Take(all.Count - 1).ToList();
    }

    /// <summary>
    /// A rough estimate of what deleting all but the newest restore point frees: the used shadow storage minus an even
    /// share for the point that stays. Shadow copies share data, so the real figure can be lower.
    /// </summary>
    public static long RestorePointEstimate(long usedBytes, int restorePointCount)
    {
        if (restorePointCount <= 1 || usedBytes <= 0)
        {
            return 0;
        }

        return usedBytes - (usedBytes / restorePointCount);
    }
}
