using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PcChecker.Core.History;

/// <summary>
/// Reads and writes one history sample per line (JSON Lines):
/// <c>{"t":1761559260,"s":60,"v":{"cpu.temp":62.1,"power":143}}</c>, where <c>t</c> is Unix seconds (UTC),
/// <c>s</c> the seconds the sample covers and <c>v</c> the values. Numbers are invariant, so files read the same
/// on every Windows language. A line that a crash cut short or that is otherwise unreadable is skipped, never fatal.
/// </summary>
public static class HistoryLineCodec
{
    /// <summary>Values are rounded to this many decimals to keep the files small.</summary>
    public const int Decimals = 2;

    /// <summary>Lines longer than this are not samples (a damaged file); they are skipped without parsing.</summary>
    public const int MaxLineLength = 8192;

    /// <summary>One sample as a single line without the trailing newline. Non-finite values and invalid ids are left out.</summary>
    public static string Format(HistorySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("t", sample.Time.ToUnixTimeSeconds());
            writer.WriteNumber("s", Math.Max(0, sample.SpanSeconds));
            writer.WriteStartObject("v");
            foreach (var (metric, value) in sample.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (double.IsFinite(value) && HistoryMetrics.IsValidId(metric))
                {
                    writer.WriteNumber(metric, Math.Round(value, Decimals));
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Parses one line. False for blank, partial, corrupt or empty lines.</summary>
    public static bool TryParse(string? line, out HistorySample sample)
    {
        sample = null!;
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineLength)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out long seconds)
                || seconds < 0 || seconds > 253_402_300_799 // 9999-12-31
                || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            int span = 60;
            if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int parsedSpan))
            {
                span = Math.Clamp(parsedSpan, 0, 86_400);
            }

            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var property in v.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out double value)
                    && double.IsFinite(value) && HistoryMetrics.IsValidId(property.Name))
                {
                    values[property.Name] = value;
                }
            }

            if (values.Count == 0)
            {
                return false;
            }

            sample = new HistorySample(DateTimeOffset.FromUnixTimeSeconds(seconds), span, values);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            // Well-formed JSON with the wrong shape counts as a damaged line too.
            return false;
        }
    }

    /// <summary>Parses every line of a file. Blank lines are ignored; unreadable ones are counted in <c>Corrupt</c>.</summary>
    public static (IReadOnlyList<HistorySample> Samples, int Corrupt) ParseLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var samples = new List<HistorySample>();
        int corrupt = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParse(line, out var sample))
            {
                samples.Add(sample);
            }
            else
            {
                corrupt++;
            }
        }

        return (samples, corrupt);
    }
}
