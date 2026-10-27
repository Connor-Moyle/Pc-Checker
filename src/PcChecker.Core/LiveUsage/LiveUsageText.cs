using System.Text;

namespace PcChecker.Core.LiveUsage;

/// <summary>Builds the plain-text copy of a resource's details (what Task Manager puts on the clipboard with Ctrl+C).</summary>
public static class LiveUsageText
{
    /// <summary>
    /// The title, the device name, then one "Label:  value" line per row with the values lined up. Rows with an empty
    /// value are left out.
    /// </summary>
    public static string BuildCopyText(string title, string? deviceName, IEnumerable<KeyValuePair<string, string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.Where(r => !string.IsNullOrWhiteSpace(r.Key) && !string.IsNullOrWhiteSpace(r.Value)).ToList();
        var text = new StringBuilder();
        text.AppendLine(title.Trim());
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            text.AppendLine(deviceName.Trim());
        }

        if (list.Count > 0)
        {
            text.AppendLine();
            int width = list.Max(r => r.Key.Trim().Length) + 1;
            foreach (var (label, value) in list)
            {
                text.Append((label.Trim() + ":").PadRight(width + 2)).AppendLine(value.Trim());
            }
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }
}
