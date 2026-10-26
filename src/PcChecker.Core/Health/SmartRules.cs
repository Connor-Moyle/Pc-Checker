using PcChecker.Core.Common;

namespace PcChecker.Core.Health;

/// <summary>Pure rules that judge a drive from SMART attributes, wear and temperature.</summary>
public static class SmartRules
{
    public const int ReallocatedSectorsId = 5;
    public const int ReallocatedEventCountId = 196;
    public const int PendingSectorsId = 197;
    public const int UncorrectableId = 198;
    public const int TemperatureId = 194;
    public const int PowerOnHoursId = 9;
    public const int SsdWearIndicatorId = 177;
    public const int SsdLifeLeftId = 231;
    public const int MediaWearoutId = 233;

    public sealed record Verdict(HealthStatus Status, string Summary, IReadOnlyList<string> Reasons);

    /// <summary>Evaluates a disk: any failed attribute or reallocated/pending/uncorrectable sectors degrade the verdict.</summary>
    public static Verdict Evaluate(DiskHealth disk, double diskWarnC = 55, double diskCritC = 65)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var reasons = new List<string>();
        var status = HealthStatus.Good;

        void Raise(HealthStatus s, string reason)
        {
            reasons.Add(reason);
            if (Rank(s) > Rank(status))
            {
                status = s;
            }
        }

        if (!string.IsNullOrWhiteSpace(disk.SmartStatusText) && !disk.SmartStatusText.Equals("OK", StringComparison.OrdinalIgnoreCase)
            && !disk.SmartStatusText.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
        {
            Raise(HealthStatus.Critical, $"The drive reports its own status as \"{disk.SmartStatusText}\".");
        }

        foreach (var attr in disk.Attributes)
        {
            if (attr.Failed)
            {
                Raise(HealthStatus.Critical, $"SMART attribute {attr.Id} ({attr.Name}) has failed.");
            }
            else if (attr.Threshold is { } t && attr.Current is { } c && t > 0 && c <= t)
            {
                Raise(HealthStatus.Critical, $"SMART attribute {attr.Id} ({attr.Name}) is at or below its failure threshold.");
            }
        }

        if (disk.ReallocatedSectors is > 0)
        {
            Raise(disk.ReallocatedSectors >= 50 ? HealthStatus.Critical : HealthStatus.Warning,
                $"{disk.ReallocatedSectors} sectors have been remapped (the drive found bad spots).");
        }

        if (disk.PendingSectors is > 0)
        {
            Raise(disk.PendingSectors >= 10 ? HealthStatus.Critical : HealthStatus.Warning,
                $"{disk.PendingSectors} sectors are waiting to be remapped.");
        }

        if (disk.UncorrectableErrors is > 0)
        {
            Raise(HealthStatus.Critical, $"{disk.UncorrectableErrors} uncorrectable errors were recorded.");
        }

        if (disk.WearPercent is { } wear)
        {
            if (wear >= 90)
            {
                Raise(HealthStatus.Critical, $"The drive has used about {wear:0}% of its rated write life.");
            }
            else if (wear >= 70)
            {
                Raise(HealthStatus.Warning, $"The drive has used about {wear:0}% of its rated write life.");
            }
        }

        if (disk.TemperatureC is { } temp)
        {
            if (temp >= diskCritC)
            {
                Raise(HealthStatus.Critical, $"The drive is very hot ({temp:0} °C).");
            }
            else if (temp >= diskWarnC)
            {
                Raise(HealthStatus.Warning, $"The drive is running warm ({temp:0} °C).");
            }
        }

        if (disk.PowerOnHours is >= 50_000)
        {
            Raise(HealthStatus.Warning, $"The drive has been powered on for {disk.PowerOnHours:N0} hours; consider a backup plan.");
        }

        string summary = status switch
        {
            HealthStatus.Critical => "This drive shows signs of failure. Back up your data now.",
            HealthStatus.Warning => "This drive is showing early warning signs. Keep backups current.",
            _ => "No problems reported by the drive's self-check.",
        };
        return new Verdict(status, summary, reasons);
    }

    /// <summary>Reads wear from common SSD attributes (177/231/233) when the drive does not expose a direct value.</summary>
    public static double? WearFromAttributes(IEnumerable<SmartAttribute> attributes)
    {
        foreach (var a in attributes)
        {
            if (a.Id is SsdWearIndicatorId or SsdLifeLeftId or MediaWearoutId && a.Current is { } current)
            {
                return Math.Clamp(100 - current, 0, 100);
            }
        }

        return null;
    }

    private static int Rank(HealthStatus s) => s switch
    {
        HealthStatus.Critical => 3,
        HealthStatus.Warning => 2,
        HealthStatus.Unknown => 1,
        _ => 0,
    };
}
