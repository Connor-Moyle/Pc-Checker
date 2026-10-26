using System.Globalization;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Health;

/// <summary>Pure plain-text rendering of a <see cref="HealthReport"/> for the clipboard, support tickets and logs.</summary>
public static class HealthReportFormatter
{
    public static string ToText(HealthReport report, int maxErrors = 50)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("PC Checker — Health report");
        sb.AppendLine($"Captured: {report.Captured:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Score: {report.Score}/100 ({HealthScoring.Describe(report.Score)})");
        sb.AppendLine($"Critical: {report.Items.Count(i => i.Status == HealthStatus.Critical)}, warnings: {report.Items.Count(i => i.Status == HealthStatus.Warning)}, good: {report.Items.Count(i => i.Status == HealthStatus.Good)}");
        sb.AppendLine();

        sb.AppendLine("== Findings ==");
        foreach (var item in report.Items.OrderByDescending(i => Rank(i.Status)).ThenBy(i => i.Component, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"[{item.Status}] {item.Component}: {item.Title}");
            sb.AppendLine($"    {item.Detail}");
            if (!string.IsNullOrWhiteSpace(item.Recommendation))
            {
                sb.AppendLine($"    → {item.Recommendation}");
            }

            foreach (var (key, value) in item.Metrics)
            {
                sb.AppendLine($"    {key}: {value}");
            }
        }

        if (report.Items.Count == 0)
        {
            sb.AppendLine("(no findings)");
        }

        sb.AppendLine();
        sb.AppendLine("== Drives ==");
        foreach (var d in report.Disks)
        {
            sb.AppendLine($"{d.Model} ({d.MediaType}, {ByteSize.Format(d.SizeBytes)}) — {d.Status}" + (d.SmartStatusText is null ? string.Empty : $", SMART: {d.SmartStatusText}"));
            var facts = new List<string>();
            if (d.TemperatureC is { } t) { facts.Add($"{t:0} °C"); }
            if (d.PowerOnHours is { } h) { facts.Add($"{h:N0} h"); }
            if (d.WearPercent is { } w) { facts.Add($"wear {w:0}%"); }
            if (d.HostWritesGb is { } g) { facts.Add($"{g:N0} GB written"); }
            if (d.ReallocatedSectors is { } r) { facts.Add($"reallocated {r}"); }
            if (d.PendingSectors is { } p) { facts.Add($"pending {p}"); }
            if (facts.Count > 0)
            {
                sb.AppendLine("    " + string.Join(", ", facts));
            }
        }

        if (report.Disks.Count == 0)
        {
            sb.AppendLine("(no drives reported)");
        }

        if (report.Batteries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== Battery ==");
            foreach (var b in report.Batteries)
            {
                sb.AppendLine($"{b.Name}: wear {(b.WearPercent is { } w ? w.ToString("0", CultureInfo.InvariantCulture) + "%" : "unknown")}, cycles {b.CycleCount?.ToString("N0") ?? "unknown"}, {b.Status}");
            }
        }

        if (report.DeviceProblems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== Device problems ==");
            foreach (var p in report.DeviceProblems)
            {
                sb.AppendLine($"{p.Name} (code {p.ErrorCode}): {p.Description} → {p.Suggestion}");
            }
        }

        if (report.CrashDumps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== Crash dumps ==");
            foreach (var c in report.CrashDumps.OrderByDescending(c => c.Time))
            {
                sb.AppendLine($"{c.Time:yyyy-MM-dd HH:mm}  {ByteSize.Format(c.SizeBytes),10}  {c.Path}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"== Recent system errors ({report.SystemErrors.Count}) ==");
        foreach (var (source, count) in HealthItemRules.TopSources(report.SystemErrors, 5))
        {
            sb.AppendLine($"{count,5} × {source}");
        }

        foreach (var e in report.SystemErrors.Take(Math.Max(0, maxErrors)))
        {
            sb.AppendLine($"{e.Time:yyyy-MM-dd HH:mm} [{e.Log}] {e.Source} #{e.EventId}: {Truncate(e.Message, 160)}");
        }

        if (report.SystemErrors.Count > maxErrors)
        {
            sb.AppendLine($"… {report.SystemErrors.Count - maxErrors} more");
        }

        if (report.Reliability.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== Reliability history ==");
            foreach (var r in report.Reliability.Take(30))
            {
                sb.AppendLine($"{r.Time:yyyy-MM-dd HH:mm} {r.Type}: {r.Source} — {Truncate(r.Message, 120)}");
            }
        }

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== Checks that could not run ==");
            foreach (var w in report.Warnings)
            {
                sb.AppendLine("- " + w);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    private static int Rank(HealthStatus s) => s switch
    {
        HealthStatus.Critical => 3,
        HealthStatus.Warning => 2,
        HealthStatus.Unknown => 1,
        _ => 0,
    };
}
