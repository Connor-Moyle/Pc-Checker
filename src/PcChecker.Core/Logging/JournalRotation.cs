namespace PcChecker.Core.Logging;

/// <summary>Decides when the action journal file is rotated and what the archived copy is called. Pure and testable.</summary>
public static class JournalRotation
{
    /// <summary>The journal is archived once it reaches this size (5 MB).</summary>
    public const long DefaultLimitBytes = 5L * 1024 * 1024;

    /// <summary>True when appending <paramref name="incomingBytes"/> to a file of <paramref name="currentBytes"/> would reach the limit.</summary>
    public static bool ShouldRotate(long currentBytes, long incomingBytes, long limitBytes = DefaultLimitBytes)
    {
        if (limitBytes <= 0 || currentBytes <= 0)
        {
            return false;
        }

        return currentBytes + Math.Max(0, incomingBytes) >= limitBytes;
    }

    /// <summary>
    /// "C:\...\actions.jsonl" + timestamp → "C:\...\actions-20240101-120000.jsonl"; a <paramref name="sequence"/> above zero
    /// adds "-2", "-3"… so several rotations within one second get distinct names.
    /// </summary>
    public static string RotatedPath(string journalPath, DateTimeOffset timestamp, int sequence = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        var dir = Path.GetDirectoryName(journalPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(journalPath);
        var ext = Path.GetExtension(journalPath);
        if (string.IsNullOrEmpty(ext))
        {
            ext = ".jsonl";
        }

        var suffix = sequence > 0 ? "-" + (sequence + 1).ToString() : string.Empty;
        return Path.Combine(dir, $"{name}-{timestamp:yyyyMMdd-HHmmss}{suffix}{ext}");
    }

    /// <summary>Search pattern that finds archived copies next to the live journal ("actions-*.jsonl").</summary>
    public static string ArchivePattern(string journalPath)
    {
        var name = Path.GetFileNameWithoutExtension(journalPath);
        var ext = Path.GetExtension(journalPath);
        return $"{name}-*{(string.IsNullOrEmpty(ext) ? ".jsonl" : ext)}";
    }
}
