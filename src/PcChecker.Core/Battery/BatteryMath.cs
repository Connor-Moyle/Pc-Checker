using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Health;

namespace PcChecker.Core.Battery;

/// <summary>
/// Pure battery arithmetic: wear and health from the capacities, drain per stretch of use, how long a full battery
/// lasts now and when new, and how fast capacity is being lost. Capacities are milliwatt-hours.
/// </summary>
public static class BatteryMath
{
    /// <summary>Stretches shorter than this are too short for a meaningful drain rate.</summary>
    public static readonly TimeSpan MinimumRateDuration = TimeSpan.FromMinutes(5);

    /// <summary>Lost capacity in percent of the design capacity (same rule as the Health page).</summary>
    public static double? WearPercent(int? designMwh, int? fullChargeMwh) => HealthScoring.BatteryWear(designMwh, fullChargeMwh);

    /// <summary>Full-charge capacity in percent of the design capacity, capped at 100 (new packs often exceed it).</summary>
    public static double? HealthPercent(int? designMwh, int? fullChargeMwh)
    {
        if (designMwh is not > 0 || fullChargeMwh is not >= 0)
        {
            return null;
        }

        return Math.Clamp(100.0 * fullChargeMwh.Value / designMwh.Value, 0, 100);
    }

    /// <summary>Good, Warning (20 % worn or more) or Critical (40 % or more), as on the Health page.</summary>
    public static HealthStatus Status(double? wearPercent) => HealthScoring.BatteryStatus(wearPercent);

    /// <summary><paramref name="part"/> in percent of <paramref name="whole"/>, clamped to 0–100.</summary>
    public static double? PercentOf(int? part, int? whole)
    {
        if (part is not >= 0 || whole is not > 0)
        {
            return null;
        }

        return Math.Clamp(100.0 * part.Value / whole.Value, 0, 100);
    }

    /// <summary>Percentage points of a full charge lost during <paramref name="entry"/> (negative while charging).</summary>
    public static double? DrainPercent(BatteryUsageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.StartChargeMwh is not { } start || entry.EndChargeMwh is not { } end || entry.FullChargeCapacityMwh is not > 0)
        {
            return null;
        }

        return 100.0 * (start - end) / entry.FullChargeCapacityMwh.Value;
    }

    /// <summary>Drain in percentage points per hour, or null when the stretch is too short or has no charge readings.</summary>
    public static double? DrainPercentPerHour(BatteryUsageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Duration < MinimumRateDuration || DrainPercent(entry) is not { } drain)
        {
            return null;
        }

        return drain / entry.Duration.TotalHours;
    }

    /// <summary>
    /// Average power drawn from the battery while the PC was in use on battery, in watts, over the stretches that
    /// have charge readings. Null when there is not at least <see cref="MinimumRateDuration"/> of such use.
    /// </summary>
    public static double? AverageActiveDrainW(IEnumerable<BatteryUsageEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        double mwh = 0, hours = 0;
        foreach (var e in entries)
        {
            if (e.State != BatteryUsageState.Active || e.OnAc || e.StartChargeMwh is not { } start || e.EndChargeMwh is not { } end || end > start)
            {
                continue;
            }

            mwh += start - end;
            hours += e.Duration.TotalHours;
        }

        return hours * 3600 >= MinimumRateDuration.TotalSeconds && mwh > 0 ? mwh / 1000.0 / hours : null;
    }

    /// <summary>Scales a run time measured at <paramref name="fromMwh"/> to <paramref name="toMwh"/> of capacity.</summary>
    public static TimeSpan? ScaleRuntime(TimeSpan? runtime, int? fromMwh, int? toMwh)
    {
        if (runtime is not { } r || r <= TimeSpan.Zero || fromMwh is not > 0 || toMwh is not >= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(r.TotalSeconds * toMwh.Value / fromMwh.Value);
    }

    /// <summary>How long <paramref name="capacityMwh"/> lasts at a steady <paramref name="drainW"/>.</summary>
    public static TimeSpan? RuntimeFromDrain(int? capacityMwh, double? drainW)
    {
        if (capacityMwh is not > 0 || drainW is not { } w || !double.IsFinite(w) || w < 0.5)
        {
            return null;
        }

        return TimeSpan.FromHours(capacityMwh.Value / 1000.0 / w);
    }

    /// <summary>
    /// How long a full battery lasts in use, now and when new. Prefers Windows' own estimate from the report (based on
    /// how the PC has been used since Windows was installed), fills the missing half by scaling with the capacities,
    /// and falls back to the average drain seen in recent use.
    /// </summary>
    public static (TimeSpan? Now, TimeSpan? WhenNew) EstimateLife(BatteryRuntimeEstimate? estimate, int? designMwh, int? fullChargeMwh, double? averageDrainW)
    {
        TimeSpan? now = Positive(estimate?.ActiveAtFullCharge);
        TimeSpan? whenNew = Positive(estimate?.ActiveAtDesign);
        now ??= ScaleRuntime(whenNew, designMwh, fullChargeMwh);
        whenNew ??= ScaleRuntime(now, fullChargeMwh, designMwh);
        if (now is null && whenNew is null)
        {
            now = RuntimeFromDrain(fullChargeMwh, averageDrainW);
            whenNew = RuntimeFromDrain(designMwh, averageDrainW);
        }

        return (now, whenNew);
    }

    /// <summary>
    /// Average capacity lost per 30 days, in percent of the design capacity, from the first to the last capacity
    /// reading. Null with less than 60 days of history or when the capacity grew (recalibration).
    /// </summary>
    public static double? MonthlyLossPercent(IReadOnlyList<BatteryCapacityPoint> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var usable = history.Where(p => p.FullChargeCapacityMwh is > 0 && p.DesignCapacityMwh is > 0).OrderBy(p => p.Start).ToList();
        if (usable.Count < 2)
        {
            return null;
        }

        var first = usable[0];
        var last = usable[^1];
        double days = (last.End - first.Start).TotalDays;
        if (days < 60)
        {
            return null;
        }

        double lostPercent = 100.0 * (first.FullChargeCapacityMwh!.Value - last.FullChargeCapacityMwh!.Value) / last.DesignCapacityMwh!.Value;
        return lostPercent <= 0 ? null : lostPercent / days * 30;
    }

    /// <summary>The headline numbers for all packs together, with the life estimate and the capacity trend.</summary>
    public static BatterySummary Summarize(BatteryOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        var packs = overview.Packs;
        int? design = SumIfAll(packs.Select(p => p.DesignCapacityMwh));
        int? full = SumIfAll(packs.Select(p => p.FullChargeCapacityMwh));
        int? cycles = packs.Select(p => p.CycleCount).Where(c => c is > 0).Max();
        var (now, whenNew) = EstimateLife(overview.Report.CurrentEstimate, design, full, AverageActiveDrainW(overview.Report.RecentUsage));
        return new BatterySummary(design, full, HealthPercent(design, full), WearPercent(design, full), cycles, now, whenNew,
            MonthlyLossPercent(overview.Report.CapacityHistory));
    }

    /// <summary>"5 h 40 min", "45 min" or "2 d 3 h": short, for tiles and lists.</summary>
    public static string FormatDuration(TimeSpan? value)
    {
        if (value is not { } v || v < TimeSpan.Zero)
        {
            return "—";
        }

        if (v.TotalDays >= 1)
        {
            return v.Hours == 0 ? $"{(int)v.TotalDays} d" : $"{(int)v.TotalDays} d {v.Hours} h";
        }

        if (v.TotalHours >= 1)
        {
            return v.Minutes == 0 ? $"{(int)v.TotalHours} h" : $"{(int)v.TotalHours} h {v.Minutes} min";
        }

        return v.TotalMinutes >= 1 ? $"{(int)v.TotalMinutes} min" : "under 1 min";
    }

    /// <summary>Milliwatt-hours as watt-hours with one decimal ("57.0 Wh"), in the current culture.</summary>
    public static string FormatWh(int? mwh) =>
        mwh is { } v ? (v / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " Wh" : "—";

    private static TimeSpan? Positive(TimeSpan? value) => value is { } v && v > TimeSpan.Zero ? v : null;

    private static int? SumIfAll(IEnumerable<int?> values)
    {
        long sum = 0;
        bool any = false;
        foreach (var v in values)
        {
            if (v is not > 0)
            {
                return null;
            }

            sum += v.Value;
            any = true;
        }

        return any && sum <= int.MaxValue ? (int)sum : null;
    }
}
