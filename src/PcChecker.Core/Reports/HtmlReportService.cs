// TODO(dashboard): dashboard/reports module — full HTML/Markdown/JSON rendering.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.Reports;

/// <summary>Default <see cref="IReportService"/>. Currently writes a minimal document per format.</summary>
public sealed class HtmlReportService : IReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<string> ExportAsync(ReportBundle bundle, ReportFormat format, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var content = format switch
        {
            ReportFormat.Json => JsonSerializer.Serialize(bundle, JsonOptions),
            ReportFormat.Markdown => BuildMarkdown(bundle),
            _ => BuildHtml(bundle),
        };
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public string BuildHtml(ReportBundle bundle) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><title>PC Checker report</title></head><body>" +
        $"<h1>PC Checker report</h1><p>Generated {bundle.Generated:yyyy-MM-dd HH:mm} by PC Checker {bundle.AppVersion}.</p>" +
        "<p>Full report rendering is not implemented yet.</p></body></html>";

    public string BuildMarkdown(ReportBundle bundle) =>
        $"# PC Checker report{Environment.NewLine}{Environment.NewLine}Generated {bundle.Generated:yyyy-MM-dd HH:mm} by PC Checker {bundle.AppVersion}.{Environment.NewLine}{Environment.NewLine}Full report rendering is not implemented yet.{Environment.NewLine}";
}
