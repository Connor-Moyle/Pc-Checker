using PcChecker.Core.Common;

namespace PcChecker.Core.Reports;

/// <summary>
/// Builds the standalone HTML report (inline CSS, dark theme, print friendly). Every section is included only when
/// the bundle carries that data, so a report from a single scan is still tidy. Pure and testable.
/// </summary>
public static class HtmlReportBuilder
{
    public static string Build(ReportBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var w = new HtmlWriter();
        var computer = bundle.Hardware?.System?.Name;
        var os = bundle.Hardware?.OperatingSystem;
        var title = string.IsNullOrWhiteSpace(computer) ? "PC Checker report" : $"PC Checker report — {computer}";

        w.Line("<!doctype html>")
            .Line("<html lang=\"en\">")
            .Line("<head>")
            .Line("<meta charset=\"utf-8\">")
            .Line("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Raw("<title>").Text(title).Line("</title>")
            .Line("<style>").Line(Css).Line("</style>")
            .Line("</head>")
            .Line("<body>")
            .Line("<header>");
        w.Heading(1, title);
        w.Raw("<p class=\"muted\">Generated ").Text(ReportText.DateTime(bundle.Generated))
            .Raw(" by PC Checker ").Text(bundle.AppVersion);
        if (os is not null)
        {
            w.Raw(" · ").Text(ReportText.Or(os.Caption, os.Name));
            if (!string.IsNullOrWhiteSpace(os.DisplayVersion))
            {
                w.Raw(" ").Text(os.DisplayVersion);
            }
        }

        w.Line("</p>");
        WriteNavigation(w, bundle);
        w.Line("</header>");
        w.Line("<main>");

        WriteSummary(w, bundle);
        if (bundle.Health is not null)
        {
            HtmlReportSections.Health(w, bundle.Health);
        }

        if (bundle.Hardware is not null)
        {
            HtmlReportSections.Hardware(w, bundle.Hardware);
        }

        if (bundle.Performance is not null)
        {
            HtmlReportSections.Performance(w, bundle.Performance);
        }

        if (bundle.Drivers is not null)
        {
            HtmlReportSections.Drivers(w, bundle.Drivers);
        }

        if (bundle.Software is not null)
        {
            HtmlReportSections.Software(w, bundle.Software);
        }

        if (bundle.Sensors is not null)
        {
            HtmlReportSections.Sensors(w, bundle.Sensors);
        }

        WriteWarnings(w, bundle);
        w.Line("</main>");
        w.Line("<footer class=\"muted\">PC Checker keeps this report on your PC; nothing in it is uploaded. Serial numbers appear as scanned.</footer>");
        w.Line("</body></html>");
        return w.ToString();
    }

    /// <summary>Sections present in the bundle, in report order (used for the navigation strip and by tests).</summary>
    public static IReadOnlyList<(string Id, string Title)> SectionsIn(ReportBundle bundle)
    {
        var list = new List<(string, string)> { ("summary", "Summary") };
        if (bundle.Health is not null) { list.Add(("health", "Health")); }
        if (bundle.Hardware is not null) { list.Add(("hardware", "Hardware")); }
        if (bundle.Performance is not null) { list.Add(("performance", "Performance")); }
        if (bundle.Drivers is not null) { list.Add(("drivers", "Drivers")); }
        if (bundle.Software is not null) { list.Add(("software", "Software")); }
        if (bundle.Sensors is not null) { list.Add(("sensors", "Temperatures")); }
        return list;
    }

    private static void WriteNavigation(HtmlWriter w, ReportBundle bundle)
    {
        w.Raw("<nav>");
        foreach (var (id, title) in SectionsIn(bundle))
        {
            w.Raw("<a href=\"#").Text(id).Raw("\">").Text(title).Raw("</a>");
        }

        w.Line("</nav>");
    }

    private static void WriteSummary(HtmlWriter w, ReportBundle bundle)
    {
        w.OpenSection("summary", "Summary");
        w.Line("<div class=\"tiles\">");
        if (bundle.Health is { } health)
        {
            Tile(w, "Health score", health.Score.ToString(System.Globalization.CultureInfo.InvariantCulture), ReportText.ScoreLabel(health.Score), ScoreClass(health.Score));
            int problems = health.Items.Count(i => i.Status is HealthStatus.Warning or HealthStatus.Critical);
            Tile(w, "Health findings", problems.ToString(System.Globalization.CultureInfo.InvariantCulture), problems == 0 ? "nothing needs attention" : "need attention", problems == 0 ? "good" : "warn");
        }

        if (bundle.Performance is { } perf)
        {
            Tile(w, "Performance score", perf.Score.ToString(System.Globalization.CultureInfo.InvariantCulture), ReportText.ScoreLabel(perf.Score), ScoreClass(perf.Score));
            Tile(w, "Slowdown findings", perf.Findings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{perf.StartupItems.Count(s => s.Enabled)} startup items enabled", perf.Findings.Count == 0 ? "good" : "warn");
        }

        if (bundle.Drivers is { } drivers)
        {
            int withProblems = drivers.Drivers.Count(d => d.HasProblem);
            Tile(w, "Drivers", drivers.Drivers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), withProblems == 0 ? "no device problems" : $"{withProblems} with problems", withProblems == 0 ? "good" : "bad");
        }

        if (bundle.Software is { } software)
        {
            Tile(w, "Installed programs", software.Programs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{software.Bundles.Count} bundles detected", null);
        }

        if (bundle.Sensors is { } sensors)
        {
            var head = SnapshotSummary.Summarize(sensors);
            Tile(w, "CPU temperature", ReportText.Number(head.CpuTempC, "0", "°C"), head.CpuName, TempClass(head.CpuTempC, 85, 95));
            Tile(w, "GPU temperature", ReportText.Number(head.GpuTempC, "0", "°C"), head.GpuName, TempClass(head.GpuTempC, 83, 92));
        }

        w.Line("</div>");

        var hw = bundle.Hardware;
        if (hw is not null)
        {
            var pairs = new List<KeyValuePair<string, string?>>
            {
                new("Computer", hw.System?.Name),
                new("Make and model", ReportText.Join(hw.System?.Manufacturer, hw.System?.Model)),
                new("Processor", hw.Processors.FirstOrDefault()?.Name),
                new("Graphics", string.Join(", ", hw.Graphics.Select(g => g.Name))),
                new("Memory", hw.TotalMemoryBytes is { } total ? $"{ByteSize.Format(total)} in {hw.MemoryModules.Count} module(s)" : null),
                new("Storage", string.Join(", ", hw.PhysicalDisks.Select(d => $"{ReportText.Or(d.Model, d.Name)} ({ReportText.Bytes(d.SizeBytes)})"))),
                new("Operating system", ReportText.Join(hw.OperatingSystem?.Caption ?? hw.OperatingSystem?.Name, hw.OperatingSystem?.DisplayVersion, hw.OperatingSystem?.BuildNumber is { } b ? "build " + b : null)),
                new("Last boot", ReportText.Date(hw.OperatingSystem?.LastBootUpTime)),
            };
            w.KeyValues(pairs);
        }

        w.CloseSection();
    }

    private static void WriteWarnings(HtmlWriter w, ReportBundle bundle)
    {
        var warnings = new List<string>();
        warnings.AddRange(bundle.Hardware?.Warnings ?? []);
        warnings.AddRange(bundle.Health?.Warnings ?? []);
        warnings.AddRange(bundle.Performance?.Warnings ?? []);
        warnings.AddRange(bundle.Drivers?.Warnings ?? []);
        warnings.AddRange(bundle.Software?.Warnings ?? []);
        if (warnings.Count == 0)
        {
            return;
        }

        w.OpenSection("warnings", "Scan notes", "Parts of the scan that could not be read completely.");
        w.Raw("<ul>");
        foreach (var warning in warnings.Distinct())
        {
            w.Raw("<li>").Text(warning).Raw("</li>");
        }

        w.Line("</ul>");
        w.CloseSection();
    }

    private static void Tile(HtmlWriter w, string label, string value, string? note, string? cssClass)
    {
        w.Raw("<div class=\"tile").Raw(cssClass is null ? string.Empty : " " + cssClass).Raw("\"><div class=\"label\">").Text(label)
            .Raw("</div><div class=\"value\">").Text(value).Raw("</div>");
        if (!string.IsNullOrWhiteSpace(note))
        {
            w.Raw("<div class=\"note\">").Text(note).Raw("</div>");
        }

        w.Line("</div>");
    }

    internal static string ScoreClass(int score) => score >= 75 ? "good" : score >= 50 ? "warn" : "bad";

    internal static string? TempClass(double? temp, double warn, double crit) =>
        temp is null ? null : temp >= crit ? "bad" : temp >= warn ? "warn" : "good";

    internal static string StatusClass(HealthStatus status) => status switch
    {
        HealthStatus.Good => "good",
        HealthStatus.Warning => "warn",
        HealthStatus.Critical => "bad",
        _ => "muted",
    };

    internal static string SeverityClass(Severity severity) => severity switch
    {
        Severity.Critical or Severity.High => "bad",
        Severity.Medium => "warn",
        _ => "muted",
    };

    private const string Css = """
        :root { color-scheme: dark; --bg: #0f1115; --card: #171a21; --line: #2a2f3a; --text: #e6e8ee; --muted: #9aa3b2; --good: #22c55e; --warn: #facc15; --bad: #ef4444; --accent: #4cc2ff; }
        * { box-sizing: border-box; }
        body { margin: 0; font: 14px/1.5 "Segoe UI", system-ui, sans-serif; background: var(--bg); color: var(--text); }
        header, main, footer { max-width: 1100px; margin: 0 auto; padding: 16px 24px; }
        h1 { font-size: 26px; margin: 0 0 4px; } h2 { font-size: 20px; margin: 0 0 6px; border-bottom: 1px solid var(--line); padding-bottom: 6px; } h3 { font-size: 15px; margin: 18px 0 6px; color: var(--muted); text-transform: uppercase; letter-spacing: .04em; }
        p { margin: 6px 0; } .muted { color: var(--muted); }
        nav { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 10px; } nav a { color: var(--accent); text-decoration: none; padding: 4px 10px; border: 1px solid var(--line); border-radius: 999px; }
        section { background: var(--card); border: 1px solid var(--line); border-radius: 12px; padding: 18px 20px; margin: 16px 0; }
        table { width: 100%; border-collapse: collapse; margin: 8px 0 12px; font-size: 13px; } th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--line); vertical-align: top; } thead th { color: var(--muted); font-weight: 600; }
        table.kv th { width: 220px; color: var(--muted); font-weight: 500; }
        .tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 12px; margin-bottom: 14px; }
        .tile { border: 1px solid var(--line); border-radius: 10px; padding: 12px 14px; border-left-width: 4px; } .tile .label { color: var(--muted); font-size: 12px; } .tile .value { font-size: 24px; font-weight: 600; } .tile .note { color: var(--muted); font-size: 12px; }
        .tile.good { border-left-color: var(--good); } .tile.warn { border-left-color: var(--warn); } .tile.bad { border-left-color: var(--bad); }
        td.good { color: var(--good); } td.warn { color: var(--warn); } td.bad { color: var(--bad); } td.muted { color: var(--muted); }
        .badge { display: inline-block; padding: 1px 8px; border-radius: 999px; font-size: 12px; border: 1px solid var(--line); } .badge.good { color: var(--good); } .badge.warn { color: var(--warn); } .badge.bad { color: var(--bad); }
        details { margin: 6px 0; } summary { cursor: pointer; color: var(--accent); }
        footer { font-size: 12px; }
        @media print { :root { color-scheme: light; --bg: #fff; --card: #fff; --line: #ccc; --text: #111; --muted: #555; } section { break-inside: avoid; } nav { display: none; } }
        """;
}
