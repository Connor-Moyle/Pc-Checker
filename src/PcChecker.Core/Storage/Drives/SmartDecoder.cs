using System.Buffers.Binary;
using System.Globalization;
using PcChecker.Core.Health;

namespace PcChecker.Core.Storage;

/// <summary>Drive makers that pack SMART raw values in their own way.</summary>
public enum SmartVendor
{
    Generic,
    Seagate,
    WesternDigital,
    Toshiba,
    Hgst,
    Samsung,
}

/// <summary>
/// Decodes SMART raw values. The 6-byte raw field is vendor specific: Seagate packs three counters into attribute 188
/// and an error count above an operation count in attributes 1 and 7; power-on hours use the low 32 bits (Seagate puts
/// milliseconds in the bytes above); temperatures use the low byte only (see <see cref="DriveTemperatureRules"/>).
/// Pure, so every rule is unit-tested.
/// </summary>
public static class SmartDecoder
{
    public const int RawReadErrorRateId = 1;
    public const int ReallocatedId = 5;
    public const int SeekErrorRateId = 7;
    public const int PowerOnHoursId = 9;
    public const int SpinRetryId = 10;
    public const int PowerCyclesId = 12;
    public const int ReportedUncorrectableId = 187;
    public const int CommandTimeoutId = 188;
    public const int AirflowTemperatureId = 190;
    public const int TemperatureId = 194;
    public const int PendingId = 197;
    public const int OfflineUncorrectableId = 198;
    public const int CrcErrorsId = 199;
    public const int TotalLbasWrittenId = 241;

    /// <summary>More power-on hours than this (over 100 years) means the value was not hours at all.</summary>
    public const long MaxPlausibleHours = 900_000;

    /// <summary>The vendor whose raw-value packing applies, from the model text.</summary>
    public static SmartVendor VendorFromModel(string? model)
    {
        var m = (model ?? string.Empty).Trim().ToUpperInvariant();
        if (m.StartsWith("ST", StringComparison.Ordinal) && m.Length > 2 && char.IsDigit(m[2]) || m.Contains("SEAGATE", StringComparison.Ordinal))
        {
            return SmartVendor.Seagate;
        }

        if (m.StartsWith("WDC", StringComparison.Ordinal) || m.StartsWith("WD ", StringComparison.Ordinal) || m.Contains("WESTERN DIGITAL", StringComparison.Ordinal)
            || (m.StartsWith("WD", StringComparison.Ordinal) && m.Length > 2 && char.IsDigit(m[2])))
        {
            return SmartVendor.WesternDigital;
        }

        if (m.StartsWith("HGST", StringComparison.Ordinal) || m.StartsWith("HITACHI", StringComparison.Ordinal) || m.StartsWith("HUS", StringComparison.Ordinal))
        {
            return SmartVendor.Hgst;
        }

        if (m.StartsWith("TOSHIBA", StringComparison.Ordinal))
        {
            return SmartVendor.Toshiba;
        }

        return m.StartsWith("SAMSUNG", StringComparison.Ordinal) ? SmartVendor.Samsung : SmartVendor.Generic;
    }

    /// <summary>The meaningful number in an attribute's raw value, or null when the value is not believable.</summary>
    public static long? Decode(int id, long raw, SmartVendor vendor)
    {
        raw &= 0xFFFF_FFFF_FFFF; // the raw field is 6 bytes
        switch (id)
        {
            case PowerOnHoursId:
                {
                    // Hours live in the low 32 bits. Seagate (and some others) put a millisecond counter in the bytes above.
                    long hours = raw & 0xFFFF_FFFF;
                    return hours <= MaxPlausibleHours ? hours : null;
                }

            case CommandTimeoutId:
                // Seagate packs three 16-bit counters (timeouts over 0, 5 and 7.5 seconds); the low word is the count.
                // Other drives keep small counts that fit the low word too.
                return raw & 0xFFFF;

            case RawReadErrorRateId or SeekErrorRateId when vendor == SmartVendor.Seagate:
                // Seagate: error count in the top 16 bits, total operations in the low 32 bits. The huge "raw" number
                // other tools show is the operation count, not errors.
                return (raw >> 32) & 0xFFFF;

            case TemperatureId or AirflowTemperatureId:
                return DriveTemperatureRules.FromSmart194(raw) is { } t ? (long)t : null;

            case ReallocatedId or SpinRetryId or PowerCyclesId or ReportedUncorrectableId or PendingId or OfflineUncorrectableId or CrcErrorsId:
                // Counters: the low 32 bits. Some drives fill the upper word with a second counter or garbage.
                return raw & 0xFFFF_FFFF;

            default:
                return raw;
        }
    }

    /// <summary>The raw value as people should read it: "15,230 h", "36 °C", "8".</summary>
    public static string DisplayRaw(int id, long? raw, SmartVendor vendor)
    {
        if (raw is not { } r)
        {
            return "—";
        }

        if (Decode(id, r, vendor) is not { } value)
        {
            return r.ToString(CultureInfo.InvariantCulture);
        }

        return id switch
        {
            PowerOnHoursId => value.ToString("N0", CultureInfo.InvariantCulture) + " h",
            TemperatureId or AirflowTemperatureId => value.ToString(CultureInfo.InvariantCulture) + " °C",
            _ => value.ToString("N0", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Pulls the health values out of an ATA attribute list.</summary>
    public static SmartValues Summarize(IReadOnlyList<SmartAttribute> attributes, string? model, IReadOnlyDictionary<int, bool>? preFailById = null)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var vendor = VendorFromModel(model);
        long? Get(int id) => attributes.FirstOrDefault(a => a.Id == id) is { RawValue: { } raw } ? Decode(id, raw, vendor) : null;

        var failed = new List<string>();
        var failedPreFail = new List<string>();
        foreach (var a in attributes)
        {
            if (a.Threshold is > 0 and var t && a.Current is { } c && c <= t)
            {
                string label = $"{a.Id} {a.Name}";
                failed.Add(label);
                bool preFail = a.Failed || (preFailById is not null && preFailById.TryGetValue(a.Id, out bool pf) && pf);
                if (preFail)
                {
                    failedPreFail.Add(label);
                }
            }
        }

        long? writesGb = null;
        if (attributes.FirstOrDefault(a => a.Id == TotalLbasWrittenId) is { RawValue: > 0 } written)
        {
            writesGb = written.RawValue!.Value * 512 / 1_000_000_000;
        }

        return new SmartValues
        {
            ReallocatedSectors = Get(ReallocatedId),
            PowerOnHours = Get(PowerOnHoursId),
            SpinRetries = Get(SpinRetryId),
            PowerCycles = Get(PowerCyclesId),
            ReportedUncorrectable = Get(ReportedUncorrectableId),
            CommandTimeouts = Get(CommandTimeoutId),
            TemperatureC = DriveTemperatureRules.FromSmartAttributes(attributes),
            PendingSectors = Get(PendingId),
            OfflineUncorrectable = Get(OfflineUncorrectableId),
            CrcErrors = Get(CrcErrorsId),
            WearPercent = SmartRules.WearFromAttributes(attributes),
            HostWritesGb = writesGb,
            FailedAttributes = failed,
            FailedPreFailAttributes = failedPreFail,
        };
    }

    /// <summary>
    /// Parses the 512-byte NVMe SMART / Health Information log page (log 02h) into health values. Counters are 128-bit
    /// little-endian; the low 64 bits are used. Data units are 1000 × 512 bytes.
    /// </summary>
    public static SmartValues? ParseNvmeHealthLog(ReadOnlySpan<byte> log)
    {
        if (log.Length < 192)
        {
            return null;
        }

        static long? Counter(ReadOnlySpan<byte> l, int offset)
        {
            ulong low = BinaryPrimitives.ReadUInt64LittleEndian(l.Slice(offset, 8));
            ulong high = BinaryPrimitives.ReadUInt64LittleEndian(l.Slice(offset + 8, 8));
            return high != 0 || low > long.MaxValue ? long.MaxValue : (long)low;
        }

        long? unitsWritten = Counter(log, 48);
        return new SmartValues
        {
            NvmeCriticalWarning = log[0],
            TemperatureC = DriveTemperatureRules.NvmeCompositeFromHealthLog(log),
            AvailableSparePercent = log[3],
            AvailableSpareThreshold = log[4],
            WearPercent = Math.Min(255, (int)log[5]),
            HostWritesGb = unitsWritten is { } u ? (long)(u * 512_000d / 1_000_000_000d) : null,
            PowerCycles = Counter(log, 112),
            PowerOnHours = Counter(log, 128),
            UnsafeShutdowns = Counter(log, 144),
            MediaErrors = Counter(log, 160),
        };
    }

    /// <summary>The NVMe health log as a list of rows for the SMART table (id 0 rows, names in plain words).</summary>
    public static IReadOnlyList<SmartAttribute> NvmeRows(SmartValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var rows = new List<SmartAttribute>();
        void Add(int id, string name, long? raw)
        {
            if (raw is not null)
            {
                rows.Add(new SmartAttribute(id, name, null, null, null, raw, false));
            }
        }

        Add(1, "Critical warning", values.NvmeCriticalWarning);
        Add(2, "Temperature (°C)", values.TemperatureC is { } t ? (long)t : null);
        Add(3, "Available spare (%)", values.AvailableSparePercent);
        Add(4, "Available spare threshold (%)", values.AvailableSpareThreshold);
        Add(5, "Percentage used", values.WearPercent is { } w ? (long)w : null);
        Add(6, "Data written (GB)", values.HostWritesGb);
        Add(7, "Power cycles", values.PowerCycles);
        Add(8, "Power-on hours", values.PowerOnHours);
        Add(9, "Unsafe shutdowns", values.UnsafeShutdowns);
        Add(10, "Media and data integrity errors", values.MediaErrors);
        return rows;
    }
}
