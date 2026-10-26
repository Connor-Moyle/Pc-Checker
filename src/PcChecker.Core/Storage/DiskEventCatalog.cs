using System.Text.RegularExpressions;
using PcChecker.Core.Common;

namespace PcChecker.Core.Storage;

/// <summary>Plain-English explanation of a disk-related System log event.</summary>
public sealed record DiskEventExplanation(long EventId, string Title, string Meaning, string Advice, Severity Severity);

/// <summary>Known disk/controller/file-system events and what they mean for a non-expert.</summary>
public static partial class DiskEventCatalog
{
    /// <summary>Event providers that report drive problems in the System log.</summary>
    public static readonly string[] Providers =
    [
        "disk", "Disk", "Ntfs", "ntfs", "storahci", "stornvme", "volmgr", "iaStorA", "iaStorAC", "iaStorAVC", "storport", "partmgr", "ReFS",
    ];

    /// <summary>Event ids worth showing.</summary>
    public static readonly long[] EventIds = [7, 11, 51, 55, 98, 129, 140, 153, 157];

    private static readonly Dictionary<long, DiskEventExplanation> Table = new()
    {
        [7] = new(7, "Bad block",
            "The drive reported a sector it could not read. Windows marks it as bad and avoids it from then on.",
            "One or two over a long time can be normal wear. Several in a short period mean the drive is failing: back up and replace it.",
            Severity.High),
        [11] = new(11, "Controller error",
            "The disk controller reported an error talking to the drive. Often a cable, port or power problem rather than the drive itself.",
            "Re-seat or replace the SATA/power cable, try another port, and update the storage driver. If it keeps happening, test the drive.",
            Severity.Medium),
        [51] = new(51, "Paging error",
            "An error occurred while reading or writing a page file or memory-mapped file on the drive.",
            "Frequent paging errors point at a failing drive or bad cable. Run a surface scan and check SMART on the Health page.",
            Severity.Medium),
        [55] = new(55, "File system corruption",
            "NTFS found a damaged structure on the volume and asked for a check.",
            "Run \"Check disk → Scan now\". If problems are found, schedule the full repair at the next restart.",
            Severity.High),
        [98] = new(98, "Volume check result",
            "Informational: NTFS reports the volume is healthy, or that a check is needed after it found a problem.",
            "If the message says the volume is healthy, nothing to do. Otherwise run a check disk scan.",
            Severity.Info),
        [129] = new(129, "Reset to device",
            "A command to the drive did not finish in time and the controller reset it. Common with power-saving problems, flaky cables or failing drives.",
            "Disable PCIe/SATA link power management in the power plan, update the storage driver, check cables. Repeated resets with a healthy drive often mean a driver or firmware issue.",
            Severity.Medium),
        [140] = new(140, "Flush failed",
            "Windows could not write cached data to the drive. Data in that cache may be lost.",
            "Check the connection (common on USB drives), and run a surface scan if it happens on an internal drive.",
            Severity.High),
        [153] = new(153, "I/O operation retried",
            "A read or write failed once and was retried successfully. Usually caused by a timeout or a marginal sector.",
            "Occasional retries are harmless. Many in a day mean the drive or cable is marginal: run a surface scan.",
            Severity.Low),
        [157] = new(157, "Disk surprise removed",
            "The drive disappeared while Windows was using it, without being safely removed.",
            "For USB drives, use \"Safely remove\". For internal drives this points at a loose cable, a power issue or a dying drive.",
            Severity.Medium),
    };

    /// <summary>Returns the explanation for an event id, or a generic one for unknown ids.</summary>
    public static DiskEventExplanation Explain(long eventId) =>
        Table.TryGetValue(eventId, out var found)
            ? found
            : new DiskEventExplanation(eventId, $"Event {eventId}", "A storage-related event from the System log.", "Check the message text; if it repeats, run a surface scan and a check disk scan.", Severity.Info);

    public static bool IsKnown(long eventId) => Table.ContainsKey(eventId);

    /// <summary>Highest severity among a batch of events (Info when empty).</summary>
    public static Severity Worst(IEnumerable<DiskErrorEvent> events)
    {
        var worst = Severity.Info;
        foreach (var e in events ?? [])
        {
            var s = Explain(e.EventId).Severity;
            if (s > worst)
            {
                worst = s;
            }
        }

        return worst;
    }

    [GeneratedRegex(@"\\Device\\(Harddisk\d+\\DR\d+|HarddiskVolume\d+|Ide\\[^\s,.]+|Scsi\\[^\s,.]+|RaidPort\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DevicePattern();

    [GeneratedRegex(@"\b([A-Z]):(?=[\s\.\)]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DriveLetterPattern();

    /// <summary>Pulls the device path or drive letter out of an event message, when present.</summary>
    public static string? ExtractDevice(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var m = DevicePattern().Match(message);
        if (m.Success)
        {
            return m.Value;
        }

        var d = DriveLetterPattern().Match(message);
        return d.Success ? d.Groups[1].Value.ToUpperInvariant() + ":" : null;
    }
}
