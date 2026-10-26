using PcChecker.Core.Common;

namespace PcChecker.Core.Health;

/// <summary>Pure rules for logical-drive free space: below 10 % free is a warning, below 5 % is critical.</summary>
public static class FreeSpaceRules
{
    public const double WarningPercent = 10;
    public const double CriticalPercent = 5;

    public sealed record DriveSpace(string Name, long FreeBytes, long TotalBytes)
    {
        public double FreePercent => TotalBytes <= 0 ? 100 : Math.Clamp(100.0 * FreeBytes / TotalBytes, 0, 100);
    }

    public static HealthStatus Evaluate(long freeBytes, long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return HealthStatus.Unknown;
        }

        double percent = 100.0 * freeBytes / totalBytes;
        if (percent < CriticalPercent)
        {
            return HealthStatus.Critical;
        }

        return percent < WarningPercent ? HealthStatus.Warning : HealthStatus.Good;
    }

    /// <summary>One item per drive that is low on space, plus a single "all fine" item when none is.</summary>
    public static IReadOnlyList<HealthItem> Items(IReadOnlyList<DriveSpace> drives)
    {
        ArgumentNullException.ThrowIfNull(drives);
        var items = new List<HealthItem>();
        var fine = new List<string>();
        foreach (var d in drives)
        {
            var status = Evaluate(d.FreeBytes, d.TotalBytes);
            if (status is HealthStatus.Good or HealthStatus.Unknown)
            {
                if (status == HealthStatus.Good)
                {
                    fine.Add($"{d.Name} {d.FreePercent:0}% free");
                }

                continue;
            }

            var metrics = new Dictionary<string, string>
            {
                ["Free"] = ByteSize.Format(d.FreeBytes),
                ["Total"] = ByteSize.Format(d.TotalBytes),
                ["Free %"] = $"{d.FreePercent:0.0}%",
            };
            items.Add(new HealthItem("Storage", status,
                status == HealthStatus.Critical ? $"Drive {d.Name} is almost full" : $"Drive {d.Name} is running low on space",
                $"{ByteSize.Format(d.FreeBytes)} of {ByteSize.Format(d.TotalBytes)} is free ({d.FreePercent:0.0}%). Windows and games slow down and updates can fail when a drive is nearly full.",
                "Use the Cleanup page to remove temporary files, uninstall programs you no longer use, or move large files to another drive.",
                metrics));
        }

        if (items.Count == 0 && fine.Count > 0)
        {
            items.Add(new HealthItem("Storage", HealthStatus.Good, "Every drive has enough free space",
                string.Join(", ", fine) + ".", null, HealthItem.NoMetrics));
        }

        return items;
    }
}
