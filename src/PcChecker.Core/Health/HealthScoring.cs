using PcChecker.Core.Common;

namespace PcChecker.Core.Health;

/// <summary>Pure scoring: turns health items into a 0..100 score and a status.</summary>
public static class HealthScoring
{
    /// <summary>Points removed per item by status. Critical items hurt a lot more than warnings.</summary>
    public const int WarningPenalty = 8;
    public const int CriticalPenalty = 30;
    public const int UnknownPenalty = 2;

    /// <summary>Computes a score from 100 by subtracting penalties; never below 0. No items means 100.</summary>
    public static int Score(IEnumerable<HealthItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        int score = 100;
        foreach (var item in items)
        {
            score -= item.Status switch
            {
                HealthStatus.Critical => CriticalPenalty,
                HealthStatus.Warning => WarningPenalty,
                HealthStatus.Unknown => UnknownPenalty,
                _ => 0,
            };
        }

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>Maps a score to a status: 80+ good, 50..79 warning, below 50 critical.</summary>
    public static HealthStatus StatusFromScore(int score) => score switch
    {
        >= 80 => HealthStatus.Good,
        >= 50 => HealthStatus.Warning,
        _ => HealthStatus.Critical,
    };

    /// <summary>Plain-English label for a score.</summary>
    public static string Describe(int score) => score switch
    {
        >= 95 => "Excellent",
        >= 80 => "Good",
        >= 65 => "Fair",
        >= 50 => "Needs attention",
        _ => "Poor",
    };

    /// <summary>The worst status among items (Unknown counts below Good).</summary>
    public static HealthStatus Worst(IEnumerable<HealthStatus> statuses)
    {
        var worst = HealthStatus.Good;
        bool any = false;
        foreach (var s in statuses)
        {
            any = true;
            if (Rank(s) > Rank(worst))
            {
                worst = s;
            }
        }

        return any ? worst : HealthStatus.Unknown;
    }

    private static int Rank(HealthStatus s) => s switch
    {
        HealthStatus.Critical => 3,
        HealthStatus.Warning => 2,
        HealthStatus.Unknown => 1,
        _ => 0,
    };

    /// <summary>Battery wear classification: ≥40 % critical, ≥20 % warning.</summary>
    public static HealthStatus BatteryStatus(double? wearPercent) => wearPercent switch
    {
        null => HealthStatus.Unknown,
        >= 40 => HealthStatus.Critical,
        >= 20 => HealthStatus.Warning,
        _ => HealthStatus.Good,
    };

    /// <summary>Computes wear percent from design and full-charge capacity.</summary>
    public static double? BatteryWear(int? designMwh, int? fullChargeMwh)
    {
        if (designMwh is null or <= 0 || fullChargeMwh is null or < 0)
        {
            return null;
        }

        return Math.Clamp(100.0 * (designMwh.Value - fullChargeMwh.Value) / designMwh.Value, 0, 100);
    }
}
