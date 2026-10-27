using System.Globalization;
using System.Net;
using System.Text;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Battery;

/// <summary>
/// Demo battery: a two-year-old 57 Wh laptop battery that has worn to 86 % of its design capacity over 318 cycles,
/// with two years of capacity history, three days of recent use and Windows-style life estimates. Pure .NET; the
/// "full report" is a small HTML page in the demo folder, never in the user's Documents.
/// </summary>
public sealed class DemoBatteryService : IBatteryService
{
    public const int DesignMwh = 57_000;
    public const int FullChargeMwh = 49_240;
    public const int Cycles = 318;
    private const int HistoryDays = 728;
    private const double ActiveDrainW = 8.6;
    private const double StandbyDrainW = 0.22;

    public BatteryOverview? Last { get; private set; }

    public string ReportsFolder => Path.Combine(JsonSettingsService.DefaultDirectory(), "demo", "reports");

    public async Task<BatteryOverview> LoadAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading the battery", 5));
        await DemoData.DelayAsync(cancellationToken, 150, 300).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Reading Windows' battery report", 40));
        await DemoData.DelayAsync(cancellationToken, 250, 500).ConfigureAwait(false);
        Last = Build(DateTimeOffset.Now);
        progress?.Report(new ProgressUpdate("Done", 100));
        return Last;
    }

    public async Task<OperationResult<string>> SaveFullReportAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        var overview = Last ?? Build(DateTimeOffset.Now);
        try
        {
            Directory.CreateDirectory(ReportsFolder);
            var path = Path.Combine(ReportsFolder, BatterySources.ReportFileName(DateTime.Now));
            await File.WriteAllTextAsync(path, Html(overview), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return OperationResult<string>.Ok(path, "The sample battery report was saved to " + path + ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<string>.Fail("The sample report could not be written: " + ex.Message, ex);
        }
    }

    /// <summary>Builds the sample overview deterministically for <paramref name="now"/> (also used by tests).</summary>
    public static BatteryOverview Build(DateTimeOffset now)
    {
        var today = now.DateTime.Date;
        var pack = new BatteryPackInfo("L21M4PC2", "Celxpert", "Lithium polymer", DesignMwh, FullChargeMwh, Cycles);
        var (capacity, usage, estimates) = BuildHistory(today);
        var recent = BuildRecentUsage(now.DateTime);
        var runtime = new BatteryRuntimeEstimate(
            TimeSpan.FromHours(FullChargeMwh / 1000.0 / ActiveDrainW), TimeSpan.FromHours(FullChargeMwh / 1000.0 / StandbyDrainW),
            TimeSpan.FromHours(DesignMwh / 1000.0 / ActiveDrainW), TimeSpan.FromHours(DesignMwh / 1000.0 / StandbyDrainW));
        var report = new BatteryReportData(now.DateTime, "Mobile", [pack], capacity, recent, usage, estimates, runtime);

        int remaining = (int)(FullChargeMwh * 0.76);
        var live = new BatteryLiveStatus(76, BatteryPowerState.Discharging, 7.8, remaining, TimeSpan.FromHours(remaining / 1000.0 / 7.8));
        return new BatteryOverview(true, PcFormFactor.Laptop, [pack], live, report, now, []);
    }

    private static (List<BatteryCapacityPoint>, List<BatteryUsagePeriod>, List<BatteryLifeEstimate>) BuildHistory(DateTime today)
    {
        var random = DemoData.CreateRandom(5700);
        var capacity = new List<BatteryCapacityPoint>();
        var usage = new List<BatteryUsagePeriod>();
        var estimates = new List<BatteryLifeEstimate>();
        var first = today.AddDays(-HistoryDays);
        const int startMwh = 57_820; // new packs often hold slightly more than their design capacity

        // Weekly rows for the older history and daily rows for the last four weeks, as Windows' report does.
        for (var start = first; start < today;)
        {
            bool daily = start >= today.AddDays(-28);
            var end = daily ? start.AddDays(1) : start.AddDays(7);
            if (end > today)
            {
                end = today;
            }

            double age = (end - first).TotalDays / HistoryDays;
            int full = end == today
                ? FullChargeMwh
                : (int)Math.Round(startMwh - (startMwh - FullChargeMwh) * Math.Pow(age, 0.85) + DemoData.Jitter(random, 0, 140));
            int cycles = (int)Math.Round(Cycles * age);
            capacity.Add(new BatteryCapacityPoint(start, end, DesignMwh, full, cycles));

            double days = (end - start).TotalDays;
            var activeDc = TimeSpan.FromHours(Math.Max(0, DemoData.Jitter(random, 1.3, 0.5)) * days);
            var standbyDc = TimeSpan.FromHours(Math.Max(0, DemoData.Jitter(random, 5.5, 1.5)) * days);
            var activeAc = TimeSpan.FromHours(Math.Max(0, DemoData.Jitter(random, 3.2, 1.0)) * days);
            var standbyAc = TimeSpan.FromHours(Math.Max(0, DemoData.Jitter(random, 9.0, 2.0)) * days);
            usage.Add(new BatteryUsagePeriod(start, end, activeDc, standbyDc, activeAc, standbyAc));

            double drain = DemoData.Jitter(random, ActiveDrainW, 0.6);
            estimates.Add(new BatteryLifeEstimate(start, end,
                full, TimeSpan.FromHours(full / 1000.0 / drain), TimeSpan.FromHours(full / 1000.0 / StandbyDrainW),
                DesignMwh, TimeSpan.FromHours(DesignMwh / 1000.0 / drain), TimeSpan.FromHours(DesignMwh / 1000.0 / StandbyDrainW)));
            start = end;
        }

        return (capacity, usage, estimates);
    }

    private static List<BatteryUsageEntry> BuildRecentUsage(DateTime now)
    {
        // One typical day: work on battery, a break, plugged in at a desk, the commute home, the evening, overnight.
        (double Hours, BatteryUsageState State, bool Ac)[] day =
        [
            (1.75, BatteryUsageState.Active, false),
            (0.9, BatteryUsageState.Standby, false),
            (3.2, BatteryUsageState.Active, true),
            (2.4, BatteryUsageState.Active, false),
            (2.0, BatteryUsageState.Standby, false),
            (1.1, BatteryUsageState.Active, false),
            (12.65, BatteryUsageState.Standby, true),
        ];

        var random = DemoData.CreateRandom(4800);
        var raw = new List<(DateTime Start, double Hours, BatteryUsageState State, bool Ac, double StartPercent)>();
        var at = now.Date.AddDays(-3).AddHours(8).AddMinutes(5);
        double percent = 82;
        for (int d = 0; d < 4 && at < now; d++)
        {
            foreach (var (hours, state, ac) in day)
            {
                if (at >= now)
                {
                    break;
                }

                double h = Math.Min(hours, (now - at).TotalHours);
                raw.Add((at, h, state, ac, percent));
                double rate = ac ? 32 : state == BatteryUsageState.Active ? DemoData.Jitter(random, 15.5, 2.5) : 0.45;
                percent = Math.Clamp(ac ? percent + rate * h : percent - rate * h, 6, 100);
                at = at.AddHours(hours);
            }
        }

        var entries = new List<BatteryUsageEntry>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            double endPercent = i + 1 < raw.Count ? raw[i + 1].StartPercent : 76;
            entries.Add(new BatteryUsageEntry(r.Start, TimeSpan.FromHours(r.Hours), r.State, r.Ac,
                (int)(FullChargeMwh * r.StartPercent / 100), (int)(FullChargeMwh * endPercent / 100), FullChargeMwh));
        }

        return entries;
    }

    private static string Html(BatteryOverview overview)
    {
        var c = CultureInfo.InvariantCulture;
        var summary = BatteryMath.Summarize(overview);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Battery report (sample)</title>")
            .Append("<style>body{font-family:Segoe UI,sans-serif;margin:32px;color:#222}table{border-collapse:collapse}td,th{padding:4px 12px;border-bottom:1px solid #ddd;text-align:left}</style></head><body>")
            .Append("<h1>Battery report</h1><p>Sample data from PC Checker's demo mode. On a real laptop this is Windows' own report.</p>")
            .Append("<h2>Installed battery</h2><table>");
        foreach (var pack in overview.Packs)
        {
            Row(html, "Name", pack.Name);
            Row(html, "Manufacturer", pack.Manufacturer ?? "—");
            Row(html, "Chemistry", pack.Chemistry ?? "—");
            Row(html, "Design capacity", pack.DesignCapacityMwh?.ToString("N0", c) + " mWh");
            Row(html, "Full charge capacity", pack.FullChargeCapacityMwh?.ToString("N0", c) + " mWh");
            Row(html, "Cycle count", pack.CycleCount?.ToString(c) ?? "—");
        }

        html.Append("</table><h2>Battery life estimate</h2><table>");
        Row(html, "At full charge", BatteryMath.FormatDuration(summary.LifeNow));
        Row(html, "At design capacity", BatteryMath.FormatDuration(summary.LifeWhenNew));
        html.Append("</table><h2>Battery capacity history</h2><table><tr><th>Period</th><th>Full charge capacity</th><th>Design capacity</th></tr>");
        foreach (var p in overview.Report.CapacityHistory.TakeLast(20))
        {
            html.Append("<tr><td>").Append(p.Start.ToString("yyyy-MM-dd", c)).Append(" – ").Append(p.End.ToString("yyyy-MM-dd", c))
                .Append("</td><td>").Append(p.FullChargeCapacityMwh?.ToString("N0", c)).Append(" mWh</td><td>")
                .Append(p.DesignCapacityMwh?.ToString("N0", c)).Append(" mWh</td></tr>");
        }

        return html.Append("</table></body></html>").ToString();
    }

    private static void Row(StringBuilder html, string key, string value) =>
        html.Append("<tr><th>").Append(WebUtility.HtmlEncode(key)).Append("</th><td>").Append(WebUtility.HtmlEncode(value)).Append("</td></tr>");
}
