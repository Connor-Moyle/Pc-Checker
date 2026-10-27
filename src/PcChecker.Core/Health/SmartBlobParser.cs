namespace PcChecker.Core.Health;

/// <summary>
/// Pure parser for the 512-byte vendor-specific SMART blobs exposed by <c>MSStorageDriver_FailurePredictData</c>
/// (attribute values) and <c>MSStorageDriver_FailurePredictThresholds</c> (failure thresholds).
/// Layout: 2 header bytes, then up to 30 entries of 12 bytes:
/// id (1), flags (2, little-endian), current (1), worst (1), raw (6, little-endian), reserved (1).
/// Threshold entries: id (1), threshold (1), reserved (10).
/// </summary>
public static class SmartBlobParser
{
    public const int HeaderBytes = 2;
    public const int EntryBytes = 12;
    public const int MaxEntries = 30;

    /// <summary>Flag bit set on "pre-failure" attributes: crossing the threshold means imminent failure.</summary>
    public const int PreFailureFlag = 0x0001;

    /// <summary>Facts a disk verdict needs, pulled out of the attribute list.</summary>
    public sealed record SmartFacts(
        double? TemperatureC,
        long? PowerOnHours,
        long? ReallocatedSectors,
        long? PendingSectors,
        long? UncorrectableErrors,
        double? WearPercent,
        long? HostWritesGb);

    /// <summary>Parses the value blob and (optionally) the threshold blob into attributes. Unknown or empty entries are skipped.</summary>
    public static IReadOnlyList<SmartAttribute> Parse(ReadOnlySpan<byte> values, ReadOnlySpan<byte> thresholds = default)
    {
        var thresholdById = ParseThresholds(thresholds);
        var result = new List<SmartAttribute>();
        int entries = Math.Min(MaxEntries, Math.Max(0, (values.Length - HeaderBytes) / EntryBytes));
        for (int i = 0; i < entries; i++)
        {
            int offset = HeaderBytes + i * EntryBytes;
            int id = values[offset];
            if (id == 0)
            {
                continue;
            }

            int flags = values[offset + 1] | (values[offset + 2] << 8);
            int current = values[offset + 3];
            int worst = values[offset + 4];
            long raw = 0;
            for (int b = 5; b >= 0; b--)
            {
                raw = (raw << 8) | values[offset + 5 + b];
            }

            thresholdById.TryGetValue(id, out int threshold);
            bool preFailure = (flags & PreFailureFlag) != 0;
            bool failed = preFailure && threshold > 0 && current <= threshold;
            result.Add(new SmartAttribute(id, SmartAttributeNames.Name(id), current, worst, threshold > 0 || thresholdById.ContainsKey(id) ? threshold : null, raw, failed));
        }

        return result;
    }

    /// <summary>Reads the threshold blob into an id → threshold map. Empty input yields an empty map.</summary>
    public static IReadOnlyDictionary<int, int> ParseThresholds(ReadOnlySpan<byte> thresholds)
    {
        var map = new Dictionary<int, int>();
        int entries = Math.Min(MaxEntries, Math.Max(0, (thresholds.Length - HeaderBytes) / EntryBytes));
        for (int i = 0; i < entries; i++)
        {
            int offset = HeaderBytes + i * EntryBytes;
            int id = thresholds[offset];
            if (id != 0)
            {
                map[id] = thresholds[offset + 1];
            }
        }

        return map;
    }

    /// <summary>Builds a value blob from attributes (used by tests and the demo service).</summary>
    public static byte[] Build(IEnumerable<(int Id, int Flags, int Current, int Worst, long Raw)> attributes)
    {
        var blob = new byte[HeaderBytes + MaxEntries * EntryBytes];
        blob[0] = 0x10;
        int i = 0;
        foreach (var a in attributes)
        {
            if (i >= MaxEntries)
            {
                break;
            }

            int offset = HeaderBytes + i * EntryBytes;
            blob[offset] = (byte)a.Id;
            blob[offset + 1] = (byte)(a.Flags & 0xFF);
            blob[offset + 2] = (byte)((a.Flags >> 8) & 0xFF);
            blob[offset + 3] = (byte)a.Current;
            blob[offset + 4] = (byte)a.Worst;
            long raw = a.Raw;
            for (int b = 0; b < 6; b++)
            {
                blob[offset + 5 + b] = (byte)(raw & 0xFF);
                raw >>= 8;
            }

            i++;
        }

        return blob;
    }

    /// <summary>Builds a threshold blob (id → threshold) in the vendor layout.</summary>
    public static byte[] BuildThresholds(IEnumerable<(int Id, int Threshold)> thresholds)
    {
        var blob = new byte[HeaderBytes + MaxEntries * EntryBytes];
        blob[0] = 0x10;
        int i = 0;
        foreach (var t in thresholds)
        {
            if (i >= MaxEntries)
            {
                break;
            }

            int offset = HeaderBytes + i * EntryBytes;
            blob[offset] = (byte)t.Id;
            blob[offset + 1] = (byte)t.Threshold;
            i++;
        }

        return blob;
    }

    /// <summary>Extracts temperature, hours, sector counts, wear and host writes from well-known attribute ids.</summary>
    public static SmartFacts ExtractFacts(IEnumerable<SmartAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var list = attributes.ToList();
        // 194 wins over 190 whatever order the drive lists them in, and only raw low bytes count as degrees
        // (the normalized value of 190 is "100 - temperature" on many drives).
        double? temp = Storage.DriveTemperatureRules.FromSmartAttributes(list);
        long? hours = null, reallocated = null, pending = null, uncorrectable = null, writesGb = null;

        foreach (var a in list)
        {
            switch (a.Id)
            {
                case SmartRules.PowerOnHoursId:
                    hours = a.RawValue is { } h ? h & 0xFFFFFFFF : null;
                    break;
                case SmartRules.ReallocatedSectorsId:
                    reallocated = a.RawValue is { } r ? r & 0xFFFFFFFF : null;
                    break;
                case SmartRules.PendingSectorsId:
                    pending = a.RawValue is { } p ? p & 0xFFFFFFFF : null;
                    break;
                case SmartRules.UncorrectableId:
                    uncorrectable = a.RawValue is { } u ? u & 0xFFFFFFFF : null;
                    break;
                case SmartAttributeNames.TotalLbasWrittenId:
                    if (a.RawValue is > 0)
                    {
                        // Most drives count 512-byte sectors; the value is only an estimate for vendors that use larger units.
                        writesGb = a.RawValue.Value * 512 / (1024L * 1024 * 1024);
                    }

                    break;
            }
        }

        double? wear = SmartRules.WearFromAttributes(list);
        return new SmartFacts(temp, hours, reallocated, pending, uncorrectable, wear, writesGb);
    }

    /// <summary>The temperature lives in the low byte of the raw value; values outside 1..125 °C are discarded.</summary>
    public static double? TemperatureFromRaw(long? raw) => Storage.DriveTemperatureRules.FromSmart194(raw);
}

/// <summary>Human names for ATA SMART attribute ids.</summary>
public static class SmartAttributeNames
{
    public const int AirflowTemperatureId = 190;
    public const int TotalLbasWrittenId = 241;
    public const int TotalLbasReadId = 242;

    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Raw Read Error Rate",
        [2] = "Throughput Performance",
        [3] = "Spin-Up Time",
        [4] = "Start/Stop Count",
        [5] = "Reallocated Sectors Count",
        [7] = "Seek Error Rate",
        [8] = "Seek Time Performance",
        [9] = "Power-On Hours",
        [10] = "Spin Retry Count",
        [11] = "Recalibration Retries",
        [12] = "Power Cycle Count",
        [13] = "Soft Read Error Rate",
        [170] = "Available Reserved Space",
        [171] = "Program Fail Count",
        [172] = "Erase Fail Count",
        [173] = "Wear Leveling Count",
        [174] = "Unexpected Power Loss Count",
        [175] = "Program Fail Count (Chip)",
        [176] = "Erase Fail Count (Chip)",
        [177] = "Wear Leveling Count",
        [178] = "Used Reserved Block Count (Chip)",
        [179] = "Used Reserved Block Count",
        [180] = "Unused Reserved Block Count",
        [181] = "Program Fail Count (Total)",
        [182] = "Erase Fail Count (Total)",
        [183] = "SATA Downshift Error Count",
        [184] = "End-to-End Error",
        [187] = "Reported Uncorrectable Errors",
        [188] = "Command Timeout",
        [189] = "High Fly Writes",
        [190] = "Airflow Temperature",
        [191] = "G-Sense Error Rate",
        [192] = "Power-Off Retract Count",
        [193] = "Load Cycle Count",
        [194] = "Temperature",
        [195] = "Hardware ECC Recovered",
        [196] = "Reallocation Event Count",
        [197] = "Current Pending Sector Count",
        [198] = "Offline Uncorrectable Sector Count",
        [199] = "UltraDMA CRC Error Count",
        [200] = "Multi-Zone Error Rate",
        [201] = "Soft Read Error Rate",
        [202] = "Data Address Mark Errors",
        [220] = "Disk Shift",
        [222] = "Loaded Hours",
        [223] = "Load Retry Count",
        [224] = "Load Friction",
        [225] = "Load Cycle Count",
        [226] = "Load-In Time",
        [227] = "Torque Amplification Count",
        [228] = "Power-Off Retract Cycle",
        [230] = "Drive Life Protection Status",
        [231] = "SSD Life Left",
        [232] = "Available Reserved Space",
        [233] = "Media Wearout Indicator",
        [234] = "Average Erase Count",
        [235] = "Good Block Count",
        [240] = "Head Flying Hours",
        [241] = "Total LBAs Written",
        [242] = "Total LBAs Read",
        [245] = "Remaining Life",
        [246] = "Total Host Sector Writes",
        [250] = "Read Error Retry Rate",
        [254] = "Free Fall Protection",
    };

    public static string Name(int id) => Names.TryGetValue(id, out var n) ? n : $"Vendor Specific ({id})";

    public static bool IsKnown(int id) => Names.ContainsKey(id);
}
