using System.Globalization;
using System.Text;

namespace PcChecker.Core.History;

/// <summary>
/// Writes history as CSV: a UTC time column, a local time column, the seconds each row covers and one column per
/// metric (with its unit in the header). Numbers use a dot as the decimal separator so every spreadsheet can read them.
/// </summary>
public static class HistoryCsvExporter
{
    public static string Build(IReadOnlyList<HistorySample> samples, TimeZoneInfo? localZone = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var zone = localZone ?? TimeZoneInfo.Local;
        var metrics = HistoryMetrics.Order(samples.SelectMany(s => s.Values.Keys));
        var sb = new StringBuilder();
        sb.Append("Time (UTC),Local time,Seconds covered");
        foreach (var metric in metrics)
        {
            sb.Append(',').Append(Escape(Header(metric)));
        }

        sb.Append("\r\n");
        foreach (var sample in samples.OrderBy(s => s.Time))
        {
            sb.Append(sample.Time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(TimeZoneInfo.ConvertTime(sample.Time, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(sample.SpanSeconds.ToString(CultureInfo.InvariantCulture));
            foreach (var metric in metrics)
            {
                sb.Append(',');
                if (sample.Values.TryGetValue(metric, out var v) && double.IsFinite(v))
                {
                    sb.Append(v.ToString("0.##", CultureInfo.InvariantCulture));
                }
            }

            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>"CPU temperature (°C)".</summary>
    public static string Header(string metric)
    {
        var unit = HistoryMetrics.UnitOf(metric);
        return unit.Length == 0 || metric == HistoryMetrics.Health ? HistoryMetrics.LabelOf(metric) : $"{HistoryMetrics.LabelOf(metric)} ({unit})";
    }

    private static string Escape(string field)
    {
        // A leading =, +, - or @ would be read as a formula by spreadsheet programs.
        if (field.Length > 0 && "=+-@".Contains(field[0]))
        {
            field = "'" + field;
        }

        return field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
