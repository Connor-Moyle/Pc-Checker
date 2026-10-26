using System.Text;

namespace PcChecker.Core.Reports;

/// <summary>Tiny HTML emitter: every string it receives is encoded, so callers never have to think about it.</summary>
public sealed class HtmlWriter
{
    private readonly StringBuilder _sb = new(64 * 1024);

    public HtmlWriter Raw(string html)
    {
        _sb.Append(html);
        return this;
    }

    public HtmlWriter Line(string html = "")
    {
        _sb.Append(html).Append('\n');
        return this;
    }

    public HtmlWriter Text(string? text)
    {
        _sb.Append(ReportText.Html(text));
        return this;
    }

    public HtmlWriter Heading(int level, string text, string? id = null)
    {
        _sb.Append('<').Append('h').Append(level);
        if (id is not null)
        {
            _sb.Append(" id=\"").Append(ReportText.Html(id)).Append('"');
        }

        _sb.Append('>').Append(ReportText.Html(text)).Append("</h").Append(level).Append(">\n");
        return this;
    }

    public HtmlWriter Paragraph(string? text, string? cssClass = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return this;
        }

        _sb.Append(cssClass is null ? "<p>" : $"<p class=\"{cssClass}\">").Append(ReportText.Html(text)).Append("</p>\n");
        return this;
    }

    public HtmlWriter OpenSection(string id, string title, string? subtitle = null)
    {
        _sb.Append("<section id=\"").Append(ReportText.Html(id)).Append("\">\n");
        Heading(2, title);
        Paragraph(subtitle, "muted");
        return this;
    }

    public HtmlWriter CloseSection()
    {
        _sb.Append("</section>\n");
        return this;
    }

    public HtmlWriter OpenDetails(string summary, bool open = false)
    {
        _sb.Append(open ? "<details open>" : "<details>").Append("<summary>").Append(ReportText.Html(summary)).Append("</summary>\n");
        return this;
    }

    public HtmlWriter CloseDetails()
    {
        _sb.Append("</details>\n");
        return this;
    }

    /// <summary>A two-column key/value table.</summary>
    public HtmlWriter KeyValues(IEnumerable<KeyValuePair<string, string?>> pairs, bool skipEmpty = true)
    {
        _sb.Append("<table class=\"kv\"><tbody>\n");
        foreach (var (key, value) in pairs)
        {
            if (skipEmpty && string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            _sb.Append("<tr><th>").Append(ReportText.Html(key)).Append("</th><td>").Append(ReportText.Html(ReportText.Or(value))).Append("</td></tr>\n");
        }

        _sb.Append("</tbody></table>\n");
        return this;
    }

    /// <summary>A data table. Cells are encoded; a cell may carry a CSS class via <see cref="Cell"/>.</summary>
    public HtmlWriter Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<Cell>> rows, string? emptyText = null)
    {
        var rowList = rows.ToList();
        if (rowList.Count == 0)
        {
            Paragraph(emptyText ?? "Nothing to show.", "muted");
            return this;
        }

        _sb.Append("<table><thead><tr>");
        foreach (var header in headers)
        {
            _sb.Append("<th>").Append(ReportText.Html(header)).Append("</th>");
        }

        _sb.Append("</tr></thead><tbody>\n");
        foreach (var row in rowList)
        {
            _sb.Append("<tr>");
            foreach (var cell in row)
            {
                _sb.Append(cell.CssClass is null ? "<td>" : $"<td class=\"{cell.CssClass}\">").Append(ReportText.Html(cell.Text)).Append("</td>");
            }

            _sb.Append("</tr>\n");
        }

        _sb.Append("</tbody></table>\n");
        return this;
    }

    public HtmlWriter Badge(string text, string cssClass)
    {
        _sb.Append("<span class=\"badge ").Append(cssClass).Append("\">").Append(ReportText.Html(text)).Append("</span>");
        return this;
    }

    public override string ToString() => _sb.ToString();

    /// <summary>One table cell: text plus an optional CSS class (e.g. "good", "warn", "bad").</summary>
    public readonly record struct Cell(string? Text, string? CssClass = null)
    {
        public static implicit operator Cell(string? text) => new(text);
    }
}
