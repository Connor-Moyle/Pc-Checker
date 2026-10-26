using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.Reports;

/// <summary>Default <see cref="IReportService"/>: HTML and Markdown through the pure builders, JSON through System.Text.Json.</summary>
public sealed class HtmlReportService : IReportService
{
    /// <summary>JSON options for the bundle: enums as names, fields (tuples) included, cycles ignored, nulls dropped.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<string> ExportAsync(ReportBundle bundle, ReportFormat format, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var content = Render(bundle, format);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public string BuildHtml(ReportBundle bundle) => HtmlReportBuilder.Build(bundle);

    public string BuildMarkdown(ReportBundle bundle) => MarkdownReportBuilder.Build(bundle);

    /// <summary>Renders the bundle in the requested format without writing a file.</summary>
    public string Render(ReportBundle bundle, ReportFormat format) => format switch
    {
        ReportFormat.Json => BuildJson(bundle),
        ReportFormat.Markdown => BuildMarkdown(bundle),
        _ => BuildHtml(bundle),
    };

    public static string BuildJson(ReportBundle bundle) => JsonSerializer.Serialize(bundle, JsonOptions);

    /// <summary>Picks the format from a file extension (".json", ".md"/".markdown", otherwise HTML).</summary>
    public static ReportFormat FormatFromPath(string? path)
    {
        var ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".json" => ReportFormat.Json,
            ".md" or ".markdown" => ReportFormat.Markdown,
            _ => ReportFormat.Html,
        };
    }

    /// <summary>The conventional extension for a format.</summary>
    public static string ExtensionFor(ReportFormat format) => format switch
    {
        ReportFormat.Json => ".json",
        ReportFormat.Markdown => ".md",
        _ => ".html",
    };
}
