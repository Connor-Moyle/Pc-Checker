using System.Globalization;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Backups;

/// <summary>
/// Pure helpers for System Restore points: event types and origins in words, the WMI time stamp, and which point is the
/// newest. Restore point type names come from <see cref="RestorePointResultCodes.TypeName"/> so they read the same as on
/// the other pages.
/// </summary>
public static class RestorePointRules
{
    /// <summary>The descriptions PC Checker gives its own restore points start with this.</summary>
    public const string OwnDescriptionPrefix = "PC Checker";

    /// <summary>Restore points made by PC Checker's own Create button also carry this text.</summary>
    public const string ManualDescriptionSuffix = "(PC Checker)";

    /// <summary>The <c>EventType</c> of a point in words.</summary>
    public static string EventName(int eventType) => eventType switch
    {
        100 => "Change started",
        101 => "Change finished",
        102 => "Nested change started",
        103 => "Nested change finished",
        _ => "Event " + eventType.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Who most likely made a point, from its description and type.</summary>
    public static string Origin(SystemRestorePoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        var description = point.Description ?? string.Empty;
        if (description.StartsWith(OwnDescriptionPrefix, StringComparison.OrdinalIgnoreCase)
            || description.EndsWith(ManualDescriptionSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return "PC Checker";
        }

        return point.Type switch
        {
            17 or 18 => "Windows Update",
            7 => "Windows (scheduled)",
            0 or 1 => "An app installer",
            10 => "A driver installer",
            6 => "System Restore",
            16 => "You or a program",
            _ => "You or a program",
        };
    }

    /// <summary>
    /// Converts the <c>CreationTime</c> WMI gives (<c>yyyyMMddHHmmss.ffffff±UUU</c>) without depending on the computer's
    /// language; null when unreadable.
    /// </summary>
    public static DateTimeOffset? ParseWmiTime(string? value) => DmtfDateTime.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>The newest point by sequence number (Windows numbers them in order), or null.</summary>
    public static SystemRestorePoint? Newest(IEnumerable<SystemRestorePoint> points) =>
        (points ?? []).MaxBy(p => p.SequenceNumber);

    /// <summary>True when <paramref name="point"/> is the newest of <paramref name="points"/>.</summary>
    public static bool IsNewest(SystemRestorePoint point, IEnumerable<SystemRestorePoint> points) =>
        Newest(points) is { } newest && newest.SequenceNumber == point.SequenceNumber;

    /// <summary>Whole days since the newest point; null when there is none.</summary>
    public static int? DaysSinceNewest(IEnumerable<SystemRestorePoint> points, DateTimeOffset now) =>
        Newest(points) is { } newest ? Math.Max(0, (int)Math.Floor((now - newest.Created).TotalDays)) : null;
}
