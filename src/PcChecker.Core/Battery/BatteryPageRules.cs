namespace PcChecker.Core.Battery;

/// <summary>What the Battery page shows instead of, or as, its content.</summary>
public enum BatteryPageState
{
    /// <summary>Nothing loaded yet.</summary>
    NotLoaded,

    /// <summary>No battery at all: a desktop PC.</summary>
    NoBattery,

    /// <summary>The only "battery" is a UPS (battery backup) plugged in over USB; its health is not this PC's battery.</summary>
    UpsOnly,

    /// <summary>A laptop or tablet battery to show.</summary>
    Ready,
}

/// <summary>Pure rules for the Battery page: when to show an empty state, what counts as a UPS, which sections have data.</summary>
public static class BatteryPageRules
{
    private static readonly string[] UpsWords =
    [
        "ups", "back-ups", "smart-ups", "apc", "cyberpower", "eaton", "tripp lite", "tripp-lite", "vertiv", "liebert", "powerwalker",
        "salicru", "riello", "bluewalker", "uninterruptible",
    ];

    /// <summary>Maps Win32_ComputerSystem.PCSystemType (1 desktop, 2 mobile, 3 workstation, 4–7 servers and appliances, 8 slate).</summary>
    public static PcFormFactor FormFactor(int? pcSystemType) => pcSystemType switch
    {
        1 or 3 => PcFormFactor.Desktop,
        2 or 8 => PcFormFactor.Laptop,
        4 or 5 or 6 or 7 => PcFormFactor.Server,
        _ => PcFormFactor.Unknown,
    };

    /// <summary>True when a pack's name or manufacturer reads like a UPS rather than a laptop battery.</summary>
    public static bool LooksLikeUps(BatteryPackInfo pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var text = $" {pack.Name} {pack.Manufacturer} ".ToLowerInvariant();
        return UpsWords.Any(w => w.Length <= 3
            ? text.Contains(" " + w + " ", StringComparison.Ordinal) || text.Contains(" " + w + "-", StringComparison.Ordinal)
            : text.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>
    /// The page state. A desktop or server whose batteries all look like a UPS (or that reports a battery at all, since
    /// desktops have none of their own) is <see cref="BatteryPageState.UpsOnly"/>; a laptop with a battery is Ready.
    /// </summary>
    public static BatteryPageState Classify(BatteryOverview? overview)
    {
        if (overview is null)
        {
            return BatteryPageState.NotLoaded;
        }

        if (!overview.HasBattery || overview.Packs.Count == 0)
        {
            return BatteryPageState.NoBattery;
        }

        bool allUps = overview.Packs.All(LooksLikeUps);
        if (allUps)
        {
            return BatteryPageState.UpsOnly;
        }

        return overview.FormFactor is PcFormFactor.Desktop or PcFormFactor.Server ? BatteryPageState.UpsOnly : BatteryPageState.Ready;
    }

    /// <summary>True when the capacity chart has enough readings to draw a line.</summary>
    public static bool CanDrawCapacityChart(IReadOnlyList<BatteryCapacityPoint> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return history.Where(p => p.FullChargeCapacityMwh is > 0).Select(p => p.Start.Date).Distinct().Count() >= BatteryChartBuilder.MinimumPoints;
    }

    /// <summary>
    /// The recent-usage rows worth listing, newest first: stretches of at least a minute that are not the report's
    /// own marker, at most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<BatteryUsageEntry> RecentRows(IReadOnlyList<BatteryUsageEntry> entries, int max = 30)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .Where(e => e.State != BatteryUsageState.ReportGenerated && e.Duration >= TimeSpan.FromMinutes(1))
            .OrderByDescending(e => e.Start)
            .Take(Math.Max(0, max))
            .ToList();
    }

    /// <summary>
    /// The tips that apply: replacement when worn, a charge limit when the PC mostly runs plugged in, battery saver
    /// and heat always. Returned as stable ids; the page turns them into text.
    /// </summary>
    public static IReadOnlyList<string> Tips(BatterySummary summary, IReadOnlyList<BatteryUsagePeriod> usage)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(usage);
        var tips = new List<string>();
        if (summary.WearPercent is >= 40)
        {
            tips.Add(TipIds.Replace);
        }

        if (MostlyPluggedIn(usage))
        {
            tips.Add(TipIds.ChargeLimit);
        }

        tips.Add(TipIds.BatterySaver);
        tips.Add(TipIds.Heat);
        if (!tips.Contains(TipIds.ChargeLimit))
        {
            tips.Add(TipIds.ChargeLimit);
        }

        return tips;
    }

    /// <summary>True when, over the last four weeks of usage history, at least 80 % of the time on was spent plugged in.</summary>
    public static bool MostlyPluggedIn(IReadOnlyList<BatteryUsagePeriod> usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.Count == 0)
        {
            return false;
        }

        var since = usage.Max(u => u.End).AddDays(-28);
        var recent = usage.Where(u => u.End >= since).ToList();
        double ac = recent.Sum(u => u.OnAc.TotalHours);
        double dc = recent.Sum(u => u.OnBattery.TotalHours);
        return ac + dc >= 10 && ac / (ac + dc) >= 0.8;
    }

    /// <summary>Ids for <see cref="Tips"/>.</summary>
    public static class TipIds
    {
        public const string Replace = "replace";
        public const string ChargeLimit = "charge-limit";
        public const string BatterySaver = "battery-saver";
        public const string Heat = "heat";
    }
}
