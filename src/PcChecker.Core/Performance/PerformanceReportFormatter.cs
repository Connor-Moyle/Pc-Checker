using System.Globalization;
using System.Text;
using System.Text.Json;
using PcChecker.Core.Common;

namespace PcChecker.Core.Performance;

/// <summary>Turns a <see cref="PerformanceReport"/> into shareable Markdown or JSON. Pure.</summary>
public static class PerformanceReportFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IncludeFields = true };

    public static string BuildMarkdown(PerformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("# PC Checker — performance report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Captured: {report.Captured:yyyy-MM-dd HH:mm}  ");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Performance score: **{report.Score}/100**  ");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Findings: {report.Findings.Count} ({report.Findings.Count(f => f.Severity >= Severity.High)} high or critical)");
        sb.AppendLine();

        sb.AppendLine("## System snapshot");
        sb.AppendLine();
        sb.AppendLine("| Item | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Last boot | {(report.LastBootSeconds is { } b ? $"{b:0.#} s" : "unknown")} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Memory in use | {report.MemoryUsedPercent:0}% of {ByteSize.Format(report.MemoryTotalBytes)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Uptime | {(report.Uptime is { } up ? PerformanceTextParsers.FormatUptime(up) : "unknown")} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Power plan | {report.ActivePowerPlan ?? "unknown"} |");
        foreach (var (drive, free, total, media) in report.Drives)
        {
            double pct = total > 0 ? 100.0 * free / total : 0;
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Drive {drive} ({media}) | {ByteSize.Format(free)} free of {ByteSize.Format(total)} ({pct:0}%) |");
        }

        sb.AppendLine();
        sb.AppendLine("## Findings");
        sb.AppendLine();
        if (report.Findings.Count == 0)
        {
            sb.AppendLine("No slowdowns found. Nice.");
        }

        foreach (var f in report.Findings)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### [{f.Severity}] {f.Title}");
            sb.AppendLine();
            sb.AppendLine(f.Detail);
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"*Impact:* {f.Impact}");
            if (f.ActionIds.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"*Suggested actions:* {string.Join(", ", f.ActionIds)}");
            }

            sb.AppendLine();
        }

        if (report.StartupItems.Count > 0)
        {
            sb.AppendLine("## Startup programs");
            sb.AppendLine();
            sb.AppendLine("| Program | Publisher | Where | Enabled |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var s in report.StartupItems.OrderByDescending(s => s.Enabled).ThenBy(s => s.Name))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(s.Name)} | {Escape(s.Publisher ?? "")} | {Escape(s.Kind)} | {(s.Enabled ? "yes" : "no")} |");
            }

            sb.AppendLine();
        }

        if (report.TopProcesses.Count > 0)
        {
            sb.AppendLine("## Busiest processes");
            sb.AppendLine();
            sb.AppendLine("| Process | CPU | Memory |");
            sb.AppendLine("|---|---|---|");
            foreach (var p in report.TopProcesses)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(p.Name)} (PID {p.Pid}) | {p.CpuPercent:0.#}% | {ByteSize.Format(p.WorkingSetBytes)} |");
            }

            sb.AppendLine();
        }

        var thirdParty = report.Services.Where(s => !s.IsMicrosoft && ServiceClassifier.IsAutomatic(s.StartMode)).ToList();
        if (thirdParty.Count > 0)
        {
            sb.AppendLine("## Third-party services that start automatically");
            sb.AppendLine();
            foreach (var s in thirdParty.OrderBy(s => s.DisplayName))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(s.DisplayName)} ({s.State})");
            }

            sb.AppendLine();
        }

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("## Notes");
            sb.AppendLine();
            foreach (var w in report.Warnings)
            {
                sb.AppendLine("- " + w);
            }
        }

        return sb.ToString();
    }

    public static string BuildJson(PerformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var payload = new
        {
            report.Captured,
            report.Score,
            Findings = report.Findings.Select(f => new { f.Id, Category = f.Category.ToString(), Severity = f.Severity.ToString(), f.Title, f.Detail, f.Impact, f.ActionIds }),
            report.StartupItems,
            Services = report.Services,
            report.TopProcesses,
            report.LastBootSeconds,
            report.MemoryUsedPercent,
            report.MemoryTotalBytes,
            report.ActivePowerPlan,
            UptimeSeconds = report.Uptime?.TotalSeconds,
            Drives = report.Drives.Select(d => new { d.Drive, d.Free, d.Total, d.MediaType }),
            report.Warnings,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string Escape(string text) => text.Replace("|", "\\|");
}
