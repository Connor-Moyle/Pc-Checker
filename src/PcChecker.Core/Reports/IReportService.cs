namespace PcChecker.Core.Reports;

/// <summary>Exports a full report as HTML, JSON or Markdown.</summary>
public interface IReportService
{
    /// <summary>Writes the report and returns the path written.</summary>
    Task<string> ExportAsync(ReportBundle bundle, ReportFormat format, string path, CancellationToken cancellationToken);
    string BuildHtml(ReportBundle bundle);
    string BuildMarkdown(ReportBundle bundle);
}
