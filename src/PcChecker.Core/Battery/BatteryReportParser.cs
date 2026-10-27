using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PcChecker.Core.Battery;

/// <summary>
/// Reads the XML that <c>powercfg /batteryreport /xml</c> writes. The XML (unlike the HTML report) uses invariant
/// numbers, ISO 8601 dates and ISO 8601 durations whatever the Windows display language, so nothing here depends on
/// the locale. Element and attribute names are matched without regard to namespace or case, and every field is
/// optional: a report with missing sections still yields what it has.
/// </summary>
public static class BatteryReportParser
{
    /// <summary>The most rows read from each section; a report with more is cut short rather than read without limit.</summary>
    public const int MaxRowsPerSection = 5000;

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd",
    ];

    /// <summary>Parses the report. Throws <see cref="FormatException"/> when the text is not a battery report.</summary>
    public static BatteryReportData Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("The battery report is not valid XML: " + ex.Message, ex);
        }

        var root = doc.Root;
        if (root is null || !Is(root, "BatteryReport"))
        {
            throw new FormatException("The file is not a Windows battery report.");
        }

        var info = Child(root, "ReportInformation");
        var scanTime = Date(Text(info, "LocalScanTime") ?? Text(info, "ScanTime"));
        var platformRole = Text(Child(root, "SystemInformation"), "PlatformRole");

        var history = Rows(Child(root, "History"), "HistoryEntry").ToList();
        return new BatteryReportData(
            scanTime,
            platformRole,
            ReadBatteries(root),
            history.Select(CapacityPoint).Where(p => p is not null).Select(p => p!).ToList(),
            ReadRecentUsage(Child(root, "RecentUsage"), scanTime),
            history.Select(UsagePeriod).Where(p => p is not null).Select(p => p!).ToList(),
            Rows(Child(root, "Estimates"), "EstimateEntry").Select(LifeEstimate).Where(p => p is not null).Select(p => p!).ToList(),
            ReadRuntimeEstimate(Child(root, "RuntimeEstimates")));
    }

    /// <summary>Maps the report's EntryType ("Active", "Suspend", "ConnectedStandby", …) to a usage state.</summary>
    public static BatteryUsageState MapEntryType(string? entryType) => Normalize(entryType) switch
    {
        "active" => BatteryUsageState.Active,
        "suspend" or "suspended" or "connectedstandby" or "modernstandby" or "standby" or "sleep" => BatteryUsageState.Standby,
        "hibernate" or "hibernated" or "shutdown" or "poweroff" or "off" => BatteryUsageState.Off,
        "reportgenerated" => BatteryUsageState.ReportGenerated,
        _ => BatteryUsageState.Unknown,
    };

    /// <summary>A readable name for the report's chemistry code ("LION", "LiP", "NiMH").</summary>
    public static string? ChemistryName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return Normalize(code) switch
        {
            "lion" or "li-i" or "lii" or "liion" => "Lithium-ion",
            "lip" or "lipo" or "lipoly" => "Lithium polymer",
            "nimh" => "Nickel metal hydride",
            "nicd" => "Nickel cadmium",
            "pbac" => "Lead acid",
            "nizn" => "Nickel zinc",
            "ram" => "Rechargeable alkaline",
            _ => code.Trim(),
        };
    }

    private static List<BatteryPackInfo> ReadBatteries(XElement root)
    {
        var packs = new List<BatteryPackInfo>();
        foreach (var b in Rows(Child(root, "Batteries"), "Battery"))
        {
            var id = Value(b, "Id");
            var manufacturer = Value(b, "Manufacturer");
            packs.Add(new BatteryPackInfo(
                id ?? $"Battery {packs.Count + 1}",
                manufacturer,
                ChemistryName(Value(b, "Chemistry")),
                Positive(Value(b, "DesignCapacity")),
                Positive(Value(b, "FullChargeCapacity")),
                Positive(Value(b, "CycleCount"))));
        }

        return packs;
    }

    private static List<BatteryUsageEntry> ReadRecentUsage(XElement? section, DateTime? scanTime)
    {
        var raw = new List<(DateTime Start, BatteryUsageState State, bool OnAc, int? Charge, int? Full, TimeSpan? Duration)>();
        foreach (var e in Rows(section, "UsageEntry"))
        {
            if (Date(Value(e, "LocalTimestamp") ?? Value(e, "Timestamp")) is not { } start)
            {
                continue;
            }

            raw.Add((start, MapEntryType(Value(e, "EntryType")), Flag(Value(e, "Ac")),
                NonNegative(Value(e, "ChargeCapacity")), Positive(Value(e, "FullChargeCapacity")), IsoDuration.Parse(Value(e, "Duration"))));
        }

        raw.Sort((a, b) => a.Start.CompareTo(b.Start));
        var entries = new List<BatteryUsageEntry>();
        for (int i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            if (r.State == BatteryUsageState.ReportGenerated)
            {
                continue;
            }

            bool hasNext = i + 1 < raw.Count;
            TimeSpan duration = hasNext ? raw[i + 1].Start - r.Start
                : r.Duration ?? (scanTime is { } scan && scan > r.Start ? scan - r.Start : TimeSpan.Zero);
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            int? endCharge = hasNext ? raw[i + 1].Charge : null;
            entries.Add(new BatteryUsageEntry(r.Start, duration, r.State, r.OnAc, r.Charge, endCharge, r.Full ?? (hasNext ? raw[i + 1].Full : null)));
        }

        return entries;
    }

    private static BatteryCapacityPoint? CapacityPoint(XElement e)
    {
        if (Period(e) is not { } p)
        {
            return null;
        }

        var full = Positive(Value(e, "FullChargeCapacity"));
        var design = Positive(Value(e, "DesignCapacity"));
        return full is null && design is null ? null : new BatteryCapacityPoint(p.Start, p.End, design, full, Positive(Value(e, "CycleCount")));
    }

    private static BatteryUsagePeriod? UsagePeriod(XElement e)
    {
        if (Period(e) is not { } p)
        {
            return null;
        }

        var period = new BatteryUsagePeriod(p.Start, p.End,
            Duration(e, "ActiveDcTime"), Duration(e, "CsDcTime", "ConnectedStandbyDcTime", "StandbyDcTime", "SuspendDcTime"),
            Duration(e, "ActiveAcTime"), Duration(e, "CsAcTime", "ConnectedStandbyAcTime", "StandbyAcTime", "SuspendAcTime"));
        return period.OnBattery + period.OnAc > TimeSpan.Zero ? period : null;
    }

    private static BatteryLifeEstimate? LifeEstimate(XElement e)
    {
        if (Period(e) is not { } p)
        {
            return null;
        }

        var estimate = new BatteryLifeEstimate(p.Start, p.End,
            Positive(Value(e, "FullChargeCapacity")),
            PositiveDuration(Value(e, "FullChargeActiveRuntime")),
            PositiveDuration(Value(e, "FullChargeConnectedStandbyRuntime") ?? Value(e, "FullChargeStandbyRuntime")),
            Positive(Value(e, "DesignCapacity")),
            PositiveDuration(Value(e, "DesignActiveRuntime")),
            PositiveDuration(Value(e, "DesignConnectedStandbyRuntime") ?? Value(e, "DesignStandbyRuntime")));
        return estimate.ActiveAtFullCharge is null && estimate.ActiveAtDesign is null && estimate.StandbyAtFullCharge is null && estimate.StandbyAtDesign is null
            ? null
            : estimate;
    }

    private static BatteryRuntimeEstimate? ReadRuntimeEstimate(XElement? section)
    {
        if (section is null)
        {
            return null;
        }

        var full = Child(section, "FullChargeCapacity");
        var design = Child(section, "DesignCapacity");
        var estimate = new BatteryRuntimeEstimate(
            PositiveDuration(Value(full, "ActiveRuntime")),
            PositiveDuration(Value(full, "ConnectedStandbyRuntime") ?? Value(full, "StandbyRuntime")),
            PositiveDuration(Value(design, "ActiveRuntime")),
            PositiveDuration(Value(design, "ConnectedStandbyRuntime") ?? Value(design, "StandbyRuntime")));
        return estimate is { ActiveAtFullCharge: null, StandbyAtFullCharge: null, ActiveAtDesign: null, StandbyAtDesign: null } ? null : estimate;
    }

    private static (DateTime Start, DateTime End)? Period(XElement e)
    {
        var start = Date(Value(e, "LocalStartDate") ?? Value(e, "StartDate"));
        if (start is null)
        {
            return null;
        }

        var end = Date(Value(e, "LocalEndDate") ?? Value(e, "EndDate"));
        return (start.Value, end is { } en && en >= start.Value ? en : start.Value);
    }

    // ----- XML helpers: names are matched by local name, ignoring namespace and case -----

    private static bool Is(XElement e, string name) => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => Is(e, name));

    private static IEnumerable<XElement> Rows(XElement? section, string name) =>
        section is null ? [] : section.Elements().Where(e => Is(e, name)).Take(MaxRowsPerSection);

    private static string? Text(XElement? parent, string name) => Child(parent, name)?.Value is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    /// <summary>An attribute, or a child element of that name when the value is written as an element.</summary>
    private static string? Value(XElement? e, string name)
    {
        if (e is null)
        {
            return null;
        }

        var attribute = e.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        if (attribute is not null)
        {
            return string.IsNullOrWhiteSpace(attribute.Value) ? null : attribute.Value.Trim();
        }

        return Text(e, name);
    }

    private static TimeSpan Duration(XElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (IsoDuration.Parse(Value(e, name)) is { } d && d > TimeSpan.Zero)
            {
                return d;
            }
        }

        return TimeSpan.Zero;
    }

    private static TimeSpan? PositiveDuration(string? text) => IsoDuration.Parse(text) is { } d && d > TimeSpan.Zero ? d : null;

    private static DateTime? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (DateTimeOffset.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withOffset)
            && (trimmed.EndsWith('Z') || trimmed.Contains('+') || trimmed.LastIndexOf('-') > 9))
        {
            return withOffset.LocalDateTime;
        }

        return DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var plain)
            ? DateTime.SpecifyKind(plain, DateTimeKind.Unspecified)
            : null;
    }

    private static long? Number(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static int? Positive(string? text) => Number(text) is { } v && v > 0 && v <= int.MaxValue ? (int)v : null;

    private static int? NonNegative(string? text) => Number(text) is { } v && v >= 0 && v <= int.MaxValue ? (int)v : null;

    private static bool Flag(string? text) => text is not null && (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? text) =>
        (text ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
