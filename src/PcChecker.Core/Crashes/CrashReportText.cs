using System.Globalization;
using System.Text;

namespace PcChecker.Core.Crashes;

/// <summary>
/// The full plain-text crash report for "Copy crash report" and "Ask Claude about this crash": every section of the
/// details pane in English, so it reads the same in a support forum whatever language the page is in. Pure.
/// </summary>
public static class CrashReportText
{
    public static string Build(CrashInsight insight)
    {
        ArgumentNullException.ThrowIfNull(insight);
        var e = insight.Crash;
        var sb = new StringBuilder();
        sb.AppendLine(insight.Advice.Title);
        Line(sb, "When", e.Time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        Line(sb, "Source", e.Source);
        if (e.BugCheckCode is { } code)
        {
            Line(sb, "Stop code", $"{StopCodeCatalog.NameOf(code)} {BugCheckFormat.WithParameters(code, e.Parameters)}");
        }

        Line(sb, "Graphics driver", e.Driver);
        Line(sb, "Program", e.AppName is null ? null : $"{e.AppName} {e.AppVersion}".Trim());
        Line(sb, "Program path", e.AppPath);
        Line(sb, "Faulting module", e.ModuleName is null ? null : $"{e.ModuleName} {e.ModuleVersion}".Trim());
        Line(sb, "Module path", e.ModulePath);
        Line(sb, "Exception", e.ExceptionCode is { } ex ? ExceptionCodeCatalog.Describe(ex) : null);
        Line(sb, "Offset", e.FaultOffset);
        Line(sb, "Windows build", e.OsBuild?.ToString(CultureInfo.InvariantCulture));
        Line(sb, "Dump file", e.DumpPath);
        Line(sb, "Report id", e.ReportId);

        Section(sb, "What happened", [insight.Advice.Explanation]);
        Section(sb, "What the details say", insight.Details.Select(l => l.Text()));
        Section(sb, "Probably caused by", insight.Causes.Select(c => $"{c.ConfidenceText}: {c.Text.Text()}"));
        if (insight.Driver is { } driver)
        {
            Section(sb, "The driver", CrashDriverRules.Describe(driver).Select(l => l.Text()));
        }

        if (insight.App is { } app)
        {
            Section(sb, "This program's crashes", AppLines(app));
        }

        Section(sb, "Patterns", insight.Patterns.Select(p => p.Text.Text()));
        if (insight.Context is { } context)
        {
            Section(sb, "Before the crash", context.Lines.Select(l => l.Text()));
        }

        Section(sb, "Try these fixes", insight.Fixes.Select((f, i) => $"{i + 1}. {f.Title}: {f.Why.Text()}"));
        if (insight.Advice.Steps.Count > 0)
        {
            Section(sb, "Steps", insight.Advice.Steps);
        }

        Section(sb, "More", e.BugCheckCode is { } c ? [$"Microsoft's reference: {StopCodeCatalog.DocsUrlFor(c)}"] : []);
        return sb.ToString().TrimEnd();
    }

    /// <summary>The question handed to Ask Claude: a short request followed by the full report.</summary>
    public static string AskPrompt(CrashInsight insight) =>
        "My PC had this crash. Explain in plain English what it most likely means for my PC and what I should try first, "
        + "safest steps first. Use the details, patterns and context below.\n\n" + Build(insight);

    /// <summary>"3 crashes, 1 hang; versions 3.0.78, 3.0.77; mostly in nvwgf2umx.dll (2)".</summary>
    public static IReadOnlyList<string> AppLines(AppCrashStats app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"{app.Crashes} crashes and {app.Hangs} hangs between {app.First:yyyy-MM-dd} and {app.Last:yyyy-MM-dd}."),
        };
        if (app.Versions.Count > 0)
        {
            lines.Add("Versions that crashed: " + string.Join(", ", app.Versions) + ".");
        }

        if (app.TopModules.Count > 0)
        {
            lines.Add("Faulting files: " + string.Join(", ", app.TopModules.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Module} ({m.Count})"))) + ".");
        }

        return lines;
    }

    private static void Line(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append(label).Append(": ").AppendLine(value);
        }
    }

    private static void Section(StringBuilder sb, string title, IEnumerable<string> lines)
    {
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (list.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine(title);
        foreach (var line in list)
        {
            sb.Append("- ").AppendLine(line);
        }
    }
}
