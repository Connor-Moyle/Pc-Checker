using System.Globalization;
using System.Text;

namespace PcChecker.Core.Benchmark;

/// <summary>
/// Pure text for benchmark results: numbers with sensible precision, signed percentage changes, durations, and the
/// plain-text export of a run or a comparison for sharing in a forum post or support ticket. Pass a culture for
/// on-screen text; the exports always use the invariant culture so they read the same everywhere.
/// </summary>
public static class BenchmarkFormatter
{
    private const int NameWidth = 32;
    private const int NumberWidth = 10;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>"1,234", or "—" when there is no score.</summary>
    public static string Score(int? score, IFormatProvider? culture = null) =>
        score is > 0 ? score.Value.ToString("N0", culture ?? CultureInfo.CurrentCulture) : "—";

    /// <summary>Three significant figures or more: 12,345 · 456 · 45.6 · 4.56 · 0.46; "—" for missing values.</summary>
    public static string Number(double? value, IFormatProvider? culture = null)
    {
        if (value is not { } v || !double.IsFinite(v))
        {
            return "—";
        }

        var format = Math.Abs(v) >= 100 ? "N0" : Math.Abs(v) >= 10 ? "N1" : "N2";
        return v.ToString(format, culture ?? CultureInfo.CurrentCulture);
    }

    /// <summary>"456 MB/s".</summary>
    public static string Metric(BenchmarkMetric metric, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return Number(metric.Value, culture) + " " + metric.Unit;
    }

    /// <summary>"+12.3 %", "-4.0 %", "0.0 %"; "—" when there is no change to show.</summary>
    public static string Change(double? percent, IFormatProvider? culture = null)
    {
        if (percent is not { } p || !double.IsFinite(p))
        {
            return "—";
        }

        double rounded = Math.Round(p, 1, MidpointRounding.AwayFromZero);
        string sign = rounded > 0 ? "+" : rounded < 0 ? "-" : string.Empty;
        return sign + Math.Abs(rounded).ToString("0.0", culture ?? CultureInfo.CurrentCulture) + " %";
    }

    /// <summary>Plain words for a change direction.</summary>
    public static string ChangeWord(BenchmarkChange change) => change switch
    {
        BenchmarkChange.Better => "better",
        BenchmarkChange.Worse => "worse",
        BenchmarkChange.Same => "no real change",
        _ => "not compared",
    };

    /// <summary>"45 s" or "2 min 05 s".</summary>
    public static string Duration(double seconds)
    {
        int total = (int)Math.Round(Math.Max(0, seconds));
        return total < 60 ? $"{total} s" : $"{total / 60} min {total % 60:00} s";
    }

    /// <summary>"32 GB" from bytes (binary gigabytes, rounded); "unknown" when zero.</summary>
    public static string Memory(long bytes) =>
        bytes <= 0 ? "unknown" : Math.Max(1, Math.Round(bytes / (1024.0 * 1024 * 1024))).ToString("0", Invariant) + " GB";

    /// <summary>One line describing the PC: name, processor, threads, memory and graphics card.</summary>
    public static string SystemLine(BenchmarkSystemInfo system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var parts = new List<string> { system.ComputerName, system.CpuName, $"{system.LogicalProcessors} threads", Memory(system.MemoryBytes) + " memory" };
        if (!string.IsNullOrWhiteSpace(system.GpuName))
        {
            parts.Add(system.GpuName!);
        }

        return string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>"2026-10-27 14:05" plus the note when there is one.</summary>
    public static string RunTitle(BenchmarkRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var date = run.Started.ToString("yyyy-MM-dd HH:mm", Invariant);
        return string.IsNullOrWhiteSpace(run.Note) ? date : date + "  " + run.Note!.Trim();
    }

    /// <summary>What a test card shows instead of a score: the reason it was skipped or failed.</summary>
    public static string StatusText(BenchmarkTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            BenchmarkTestStatus.Skipped => "Skipped: " + (result.Note ?? "not available on this PC."),
            BenchmarkTestStatus.Failed => "Did not finish: " + (result.Note ?? "the test stopped with an error."),
            _ => BenchmarkScoring.Rating(result.Score),
        };
    }

    /// <summary>A run as plain text, ready to paste.</summary>
    public static string ExportRun(BenchmarkRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var text = new StringBuilder();
        text.AppendLine("PC Checker benchmark");
        text.AppendLine("Date:     " + run.Started.ToString("yyyy-MM-dd HH:mm", Invariant));
        if (!string.IsNullOrWhiteSpace(run.Note))
        {
            text.AppendLine("Note:     " + run.Note!.Trim());
        }

        text.AppendLine("PC:       " + SystemLine(run.System));
        text.AppendLine("Overall:  " + Score(run.OverallScore, Invariant) + " (reference PC = 1,000)" + (run.StoppedEarly ? ", stopped early" : string.Empty));
        text.AppendLine();
        foreach (var test in run.Tests)
        {
            if (!test.HasScore)
            {
                text.AppendLine(Pad(BenchmarkCatalog.Name(test.Test)) + StatusText(test));
                continue;
            }

            text.AppendLine(Pad(BenchmarkCatalog.Name(test.Test)) + Score(test.Score, Invariant).PadLeft(NumberWidth));
            foreach (var metric in test.Metrics)
            {
                text.AppendLine(Pad("  " + metric.Name) + Number(metric.Value, Invariant).PadLeft(NumberWidth) + " " + metric.Unit);
            }

            if (!string.IsNullOrWhiteSpace(test.Note))
            {
                text.AppendLine("  " + test.Note);
            }
        }

        if (run.Warnings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("When the run started:");
            foreach (var warning in run.Warnings)
            {
                text.AppendLine("- " + warning);
            }
        }

        text.AppendLine();
        text.AppendLine("Scores: 1,000 = a mid-range 2024 desktop. Higher is better.");
        return text.ToString();
    }

    /// <summary>A comparison as a plain-text table, ready to paste.</summary>
    public static string ExportComparison(BenchmarkComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var text = new StringBuilder();
        text.AppendLine("PC Checker benchmark comparison");
        text.AppendLine("Before:   " + RunTitle(comparison.Before));
        text.AppendLine("After:    " + RunTitle(comparison.After));
        text.AppendLine("PC:       " + SystemLine(comparison.After.System));
        text.AppendLine();
        text.AppendLine(Pad(string.Empty) + "Before".PadLeft(NumberWidth) + "After".PadLeft(NumberWidth) + "Change".PadLeft(NumberWidth));
        AppendRow(text, comparison.Overall);
        foreach (var row in comparison.Tests)
        {
            AppendRow(text, row);
            foreach (var detail in comparison.Details.Where(d => d.Test == row.Test))
            {
                AppendRow(text, detail);
            }
        }

        if (comparison.Notes.Count > 0)
        {
            text.AppendLine();
            foreach (var note in comparison.Notes)
            {
                text.AppendLine("- " + note);
            }
        }

        text.AppendLine();
        text.AppendLine($"Changes under {BenchmarkComparer.NoiseThresholdPercent:0} % are normal run-to-run variation.");
        return text.ToString();
    }

    private static void AppendRow(StringBuilder text, BenchmarkComparisonRow row)
    {
        string name = row.IsDetail ? $"  {row.Name} ({row.Unit})" : row.Name;
        string before = row.IsDetail ? Number(row.Before, Invariant) : Score(ToScore(row.Before), Invariant);
        string after = row.IsDetail ? Number(row.After, Invariant) : Score(ToScore(row.After), Invariant);
        string line = Pad(name) + before.PadLeft(NumberWidth) + after.PadLeft(NumberWidth) + Change(row.ChangePercent, Invariant).PadLeft(NumberWidth);
        if (row.Change is BenchmarkChange.Better or BenchmarkChange.Worse)
        {
            line += "  " + ChangeWord(row.Change);
        }

        text.AppendLine(line.TrimEnd());
    }

    private static int? ToScore(double? value) => value is { } v ? (int)Math.Round(v) : null;

    private static string Pad(string name) => name.Length >= NameWidth ? name[..(NameWidth - 1)] + " " : name.PadRight(NameWidth);
}
