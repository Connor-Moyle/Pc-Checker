using System.Globalization;
using PcChecker.Core.History;
using PcChecker.Core.Logging;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>Lowest, highest and average of one recorded metric in the minutes before a crash, with the points for a chart.</summary>
public sealed record MetricSummary(string Metric, double Min, double Max, double Average, IReadOnlyList<HistoryPoint> Points);

/// <summary>"Before the crash": what PC Checker knows about the minutes, hours and day before one crash.</summary>
public sealed record CrashContext
{
    public required DateTimeOffset CrashTime { get; init; }

    /// <summary>True when PC Checker's History recorder has samples from the minutes before the crash.</summary>
    public bool HistoryRecorded { get; init; }

    public IReadOnlyList<MetricSummary> Metrics { get; init; } = [];

    /// <summary>True when the CPU or GPU reached its critical temperature limit before the crash.</summary>
    public bool Overheating { get; init; }

    /// <summary>True when it was hot (above the warning limit) but not at the critical limit.</summary>
    public bool RunningHot { get; init; }

    /// <summary>"CPU" or "GPU": the part that ran hottest compared with its limit.</summary>
    public string? HotPart { get; init; }

    public double? HotTemperature { get; init; }

    /// <summary>True when the CPU or GPU was busy (a game, render or benchmark) just before the crash.</summary>
    public bool HeavyLoad { get; init; }

    /// <summary>WHEA errors the hardware corrected by itself in the hour before.</summary>
    public int WheaCorrectedHourBefore { get; init; }

    /// <summary>Drive and file-system errors in the day before.</summary>
    public int DiskErrorsDayBefore { get; init; }

    /// <summary>How long Windows had been running, from the last start before the crash.</summary>
    public TimeSpan? Uptime { get; init; }

    /// <summary>Minutes since the PC woke from sleep, when it woke in the <see cref="CrashContextRules.WakeWindow"/> before.</summary>
    public double? MinutesSinceWake { get; init; }

    /// <summary>Changes PC Checker made in the <see cref="CrashContextRules.JournalWindow"/> before the crash, newest first.</summary>
    public IReadOnlyList<ActionRecord> ChangesBefore { get; init; } = [];

    /// <summary>The section's sentences, in order.</summary>
    public IReadOnlyList<CrashLine> Lines { get; init; } = [];
}

/// <summary>
/// Builds <see cref="CrashContext"/> from the History recorder's samples, the system events around the crash (WHEA,
/// disk, sleep and wake, starts) and the Action Journal. Pure: the page loads the data and hands it over.
/// </summary>
public static class CrashContextRules
{
    /// <summary>How far back the temperature and load window looks.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan WheaWindow = TimeSpan.FromHours(1);

    public static readonly TimeSpan DiskWindow = TimeSpan.FromDays(1);

    public static readonly TimeSpan WakeWindow = TimeSpan.FromMinutes(10);

    /// <summary>PC Checker changes this long before a crash are shown.</summary>
    public static readonly TimeSpan JournalWindow = TimeSpan.FromHours(6);

    /// <summary>Average load above this (percent) counts as heavy load.</summary>
    public const double HeavyGpuLoad = 70;

    public const double HeavyCpuLoad = 80;

    private static readonly string[] ShownMetrics = [HistoryMetrics.CpuTemp, HistoryMetrics.GpuTemp, HistoryMetrics.CpuLoad, HistoryMetrics.GpuLoad, HistoryMetrics.Power];

    /// <summary>The samples that fall in the window before the crash (a minute after it is allowed for the last write).</summary>
    public static IReadOnlyList<HistorySample> Window(IEnumerable<HistorySample> samples, DateTimeOffset crashTime)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var from = crashTime - HistoryWindow;
        var to = crashTime + TimeSpan.FromMinutes(1);
        return samples.Where(s => s.Time >= from && s.Time <= to).OrderBy(s => s.Time).ToList();
    }

    public static CrashContext Build(CrashEvent crash, IEnumerable<HistorySample> samples, IEnumerable<CrashSystemEvent> events,
        IEnumerable<ActionRecord> journal, HistoryThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(crash);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(thresholds);
        var t = crash.Time;
        var window = Window(samples, t);
        var metrics = new List<MetricSummary>();
        foreach (var metric in ShownMetrics)
        {
            var points = window.Where(s => s.Get(metric) is not null).Select(s => new HistoryPoint(s.Time, s.Get(metric)!.Value)).ToList();
            if (points.Count > 0)
            {
                metrics.Add(new MetricSummary(metric, points.Min(p => p.Value), points.Max(p => p.Value), points.Average(p => p.Value), points));
            }
        }

        double? Max(string m) => metrics.FirstOrDefault(x => x.Metric == m)?.Max;
        double? Avg(string m) => metrics.FirstOrDefault(x => x.Metric == m)?.Average;

        // Hottest part relative to its limit.
        var parts = new List<(string Part, double Peak, double Warn, double Crit)>();
        if (Max(HistoryMetrics.CpuTemp) is { } cpuPeak)
        {
            parts.Add(("CPU", cpuPeak, thresholds.CpuWarnC, thresholds.CpuCritC));
        }

        if (Max(HistoryMetrics.GpuTemp) is { } gpuPeak)
        {
            parts.Add(("GPU", gpuPeak, thresholds.GpuWarnC, thresholds.GpuCritC));
        }

        bool overheating = parts.Any(p => p.Peak >= p.Crit);
        bool runningHot = !overheating && parts.Any(p => p.Peak >= p.Warn);
        var hottest = parts.Where(p => overheating ? p.Peak >= p.Crit : p.Peak >= p.Warn)
            .OrderByDescending(p => p.Crit > 0 ? p.Peak / p.Crit : p.Peak)
            .Select(p => ((string Part, double Peak)?)(p.Part, p.Peak))
            .FirstOrDefault();
        string? hotPart = hottest?.Part;
        double? hotTemp = hottest?.Peak;

        bool heavy = Avg(HistoryMetrics.GpuLoad) >= HeavyGpuLoad || Avg(HistoryMetrics.CpuLoad) >= HeavyCpuLoad;

        var list = events as IReadOnlyList<CrashSystemEvent> ?? events.ToList();
        int whea = list.Count(e => e.Kind == CrashSystemEventKind.WheaCorrected && e.Time <= t && e.Time >= t - WheaWindow);
        int disk = list.Count(e => e.Kind == CrashSystemEventKind.DiskError && e.Time <= t && e.Time >= t - DiskWindow);
        var boot = list.Where(e => e.Kind == CrashSystemEventKind.Boot && e.Time <= t).OrderByDescending(e => e.Time).FirstOrDefault();
        var wake = list.Where(e => e.Kind == CrashSystemEventKind.Wake && e.Time <= t && e.Time >= t - WakeWindow).OrderByDescending(e => e.Time).FirstOrDefault();
        var changes = journal.Where(j => j.Timestamp <= t && j.Timestamp >= t - JournalWindow && IsSuccess(j.Outcome))
            .OrderByDescending(j => j.Timestamp).Take(5).ToList();

        var context = new CrashContext
        {
            CrashTime = t,
            HistoryRecorded = window.Count > 0,
            Metrics = metrics,
            Overheating = overheating,
            RunningHot = runningHot,
            HotPart = hotPart,
            HotTemperature = hotTemp,
            HeavyLoad = heavy,
            WheaCorrectedHourBefore = whea,
            DiskErrorsDayBefore = disk,
            Uptime = boot is null ? null : t - boot.Time,
            MinutesSinceWake = wake is null ? null : Math.Max(0, (t - wake.Time).TotalMinutes),
            ChangesBefore = changes,
        };
        return context with { Lines = Describe(context, thresholds) };
    }

    private static bool IsSuccess(string? outcome) =>
        outcome is null || !(outcome.Contains("fail", StringComparison.OrdinalIgnoreCase) || outcome.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            || outcome.Contains("refused", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<CrashLine> Describe(CrashContext c, HistoryThresholds thresholds)
    {
        var lines = new List<CrashLine>();
        if (!c.HistoryRecorded)
        {
            lines.Add(CrashLine.Of(T("PC Checker was not recording at that time, so there are no temperatures or loads from before the crash. Leave History recording on (Settings) to see them next time.")));
        }
        else
        {
            foreach (var m in c.Metrics)
            {
                lines.Add(new CrashLine(T("{0}: {1} to {2} {3} in the 10 minutes before (average {4})."),
                    new LocalizedText(HistoryMetrics.LabelOf(m.Metric)), Num(m.Min), Num(m.Max), HistoryMetrics.UnitOf(m.Metric), Num(m.Average)));
            }

            if (c.Overheating && c.HotPart is { } part && c.HotTemperature is { } temp)
            {
                double limit = part == "CPU" ? thresholds.CpuCritC : thresholds.GpuCritC;
                lines.Add(new CrashLine(T("Overheating: the {0} reached {1} °C, above its {2} °C limit. Heat may have caused this crash."), part, Num(temp), Num(limit)));
            }
            else if (c.RunningHot && c.HotPart is { } warmPart && c.HotTemperature is { } warmTemp)
            {
                lines.Add(new CrashLine(T("The {0} ran hot ({1} °C) but stayed below its critical limit."), warmPart, Num(warmTemp)));
            }

            if (c.HeavyLoad)
            {
                lines.Add(CrashLine.Of(T("The PC was under heavy load (a game, render or benchmark) when it crashed.")));
            }
        }

        if (c.WheaCorrectedHourBefore > 0)
        {
            lines.Add(new CrashLine(T("The hardware corrected {0} errors by itself (WHEA) in the hour before: a sign of an unstable overclock, undervolt or memory setting."),
                c.WheaCorrectedHourBefore.ToString(CultureInfo.InvariantCulture)));
        }

        if (c.DiskErrorsDayBefore > 0)
        {
            lines.Add(new CrashLine(T("{0} drive or file-system errors were logged in the day before."), c.DiskErrorsDayBefore.ToString(CultureInfo.InvariantCulture)));
        }

        if (c.MinutesSinceWake is { } woke)
        {
            lines.Add(new CrashLine(T("The PC had woken from sleep {0} minutes before."), Math.Round(woke).ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var change in c.ChangesBefore)
        {
            lines.Add(new CrashLine(T("PC Checker made a change {0} before: {1} – {2} ({3})."),
                Ago(c.CrashTime - change.Timestamp), change.Module, change.Action, change.Target));
        }

        if (c.Uptime is { } up)
        {
            lines.Add(new CrashLine(T("Windows had been running for {0} since it last started."), Duration(up)));
        }

        return lines;
    }

    private static string Num(double value) => Math.Round(value).ToString("0", CultureInfo.CurrentCulture);

    /// <summary>"20 minutes", "3 hours".</summary>
    public static string Ago(TimeSpan span) =>
        span.TotalMinutes < 90
            ? Math.Max(1, (int)Math.Round(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min"
            : ((int)Math.Round(span.TotalHours)).ToString(CultureInfo.InvariantCulture) + " h";

    /// <summary>"45 min", "2 h 14 min", "3 days 4 h".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalHours < 1)
        {
            return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        }

        if (span.TotalDays < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h {span.Minutes} min");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays} d {span.Hours} h");
    }
}
