using System.Globalization;
using System.Xml.Linq;

namespace PcChecker.Core.Crashes;

/// <summary>
/// The language-neutral parts of one event, read from the event's XML (<c>EventRecord.ToXml()</c>): provider, id,
/// UTC time and the EventData / UserData values by name and position. The XML never contains the translated message,
/// so the same parser works on every Windows language.
/// </summary>
public sealed record EventXmlRecord(
    string Provider,
    string? EventSourceName,
    int EventId,
    DateTimeOffset Time,
    string? Channel,
    long? RecordId,
    IReadOnlyDictionary<string, string?> Named,
    IReadOnlyList<string?> Values)
{
    /// <summary>The first non-blank named value among <paramref name="names"/>.</summary>
    public string? Get(params string[] names)
    {
        foreach (var n in names)
        {
            if (Named.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return null;
    }

    /// <summary>The positional value at <paramref name="index"/>, trimmed, or null.</summary>
    public string? At(int index) =>
        index >= 0 && index < Values.Count && !string.IsNullOrWhiteSpace(Values[index]) ? Values[index]!.Trim() : null;

    /// <summary>A named value, falling back to the positional one for older events whose data has no names.</summary>
    public string? Field(string name, int index) => Get(name) ?? (Named.Count == 0 ? At(index) : null);

    /// <summary>Parses event XML; null when it is blank, malformed or has no System section.</summary>
    public static EventXmlRecord? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var system = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "System");
        if (system is null)
        {
            return null;
        }

        XElement? Child(string name) => system.Elements().FirstOrDefault(e => e.Name.LocalName == name);

        var provider = Child("Provider");
        string providerName = provider?.Attribute("Name")?.Value ?? string.Empty;
        string? sourceName = provider?.Attribute("EventSourceName")?.Value;
        int id = int.TryParse(Child("EventID")?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
        var time = ParseTime(Child("TimeCreated")?.Attribute("SystemTime")?.Value) ?? DateTimeOffset.MinValue;
        long? recordId = long.TryParse(Child("EventRecordID")?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;

        var named = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var values = new List<string?>();
        var data = doc.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "EventData")
            ?? doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "UserData")?.Elements().FirstOrDefault();
        if (data is not null)
        {
            bool isEventData = data.Name.LocalName == "EventData";
            foreach (var d in data.Elements())
            {
                if (isEventData && d.Name.LocalName != "Data")
                {
                    continue; // <Binary> and friends
                }

                values.Add(d.Value);
                var name = isEventData ? d.Attribute("Name")?.Value : d.Name.LocalName;
                if (!string.IsNullOrEmpty(name))
                {
                    named[name] = d.Value;
                }
            }
        }

        return new EventXmlRecord(providerName, sourceName, id, time, Child("Channel")?.Value, recordId, named, values);
    }

    /// <summary>"2026-10-27T18:22:31.1234567Z" (always UTC in event XML) → a local-offset time.</summary>
    public static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t.ToLocalTime()
            : null;
}
