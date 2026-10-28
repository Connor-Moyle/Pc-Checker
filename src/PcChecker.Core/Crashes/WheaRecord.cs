using System.Buffers.Binary;
using System.Globalization;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>Where a WHEA hardware error came from (WHEA_ERROR_SOURCE_TYPE, also BugCheck 0x124 parameter 1).</summary>
public enum WheaErrorSource
{
    MachineCheck = 0,
    CorrectedMachineCheck = 1,
    CorrectedPlatform = 2,
    Nmi = 3,
    PciExpress = 4,
    Generic = 5,
    Init = 6,
    Boot = 7,
    SciGeneric = 8,
    GenericV2 = 0xC,
    DeviceDriver = 0x10,
    Unknown = -1,
}

/// <summary>What a WHEA error record says, from the BugCheck 0x124 parameters, the WHEA-Logger event or its raw CPER record.</summary>
public sealed record WheaErrorInfo
{
    public WheaErrorSource Source { get; init; } = WheaErrorSource.Unknown;

    /// <summary>True for an error that crashed Windows (event 18/20, record severity fatal).</summary>
    public bool Fatal { get; init; }

    /// <summary>The parts named by the record's sections ("Processor", "Memory", "PCI Express"…), English keys.</summary>
    public IReadOnlyList<string> Components { get; init; } = [];

    /// <summary>The processor's APIC id from the event or the processor section; core number ≈ APIC id ÷ 2 with SMT.</summary>
    public int? ApicId { get; init; }

    /// <summary>The machine-check bank that reported it.</summary>
    public int? Bank { get; init; }

    /// <summary>MCi_STATUS of that bank (also BugCheck 0x124 parameters 3 and 4 for a machine check).</summary>
    public ulong? MciStatus { get; init; }

    /// <summary>Processor error type from the processor section (cache, TLB, bus, micro-architecture), English key.</summary>
    public string? ProcessorErrorType { get; init; }

    /// <summary>When the event recorded it (null when it came from stop-code parameters).</summary>
    public DateTimeOffset? Time { get; init; }
}

/// <summary>A machine-check status (MCi_STATUS) in plain words.</summary>
public sealed record McaStatusInfo(bool Valid, bool Uncorrected, bool ContextCorrupt, bool Overflow, ushort ErrorCode, string ErrorClass);

/// <summary>
/// Pure decoding of WHEA hardware error data: the error source of BugCheck 0x124, a machine-check status register
/// (Intel/AMD architectural MCA error codes), the CPER error record WHEA-Logger stores as RawData, and the named
/// fields of WHEA-Logger events. Numbers only; never the translated message text.
/// </summary>
public static class WheaRecord
{
    // CPER notification types (UEFI spec, appendix N) that say which error source raised the record.
    private static readonly Dictionary<Guid, WheaErrorSource> NotifyTypes = new()
    {
        [new Guid("E8F56FFE-919C-4CC5-BA88-65ABE14913BB")] = WheaErrorSource.MachineCheck,
        [new Guid("2DCE8BB1-BDD7-450E-B9AD-9CF4EBD4F890")] = WheaErrorSource.CorrectedMachineCheck,
        [new Guid("4E292F96-D843-4A55-A8C2-D481F27EBEEE")] = WheaErrorSource.CorrectedPlatform,
        [new Guid("5BAD89FF-B7E6-42C9-814A-CF2485D6E98A")] = WheaErrorSource.Nmi,
        [new Guid("CF93C01F-1A16-4DFC-B8BC-9C4DAF67C104")] = WheaErrorSource.PciExpress,
        [new Guid("CC5263E8-9308-454A-89D0-340BD39BC98E")] = WheaErrorSource.Init,
        [new Guid("3D61A466-AB40-409A-A698-F362D464B38F")] = WheaErrorSource.Boot,
    };

    // CPER section types: which part of the PC the section describes.
    private static readonly Dictionary<Guid, string> SectionTypes = new()
    {
        [new Guid("9876CCAD-47B4-4BDB-B65E-16F193C4F3DB")] = T("Processor"),
        [new Guid("DC3EA0B0-A144-4797-B95B-53FA242B6E1D")] = T("Processor"),
        [new Guid("8A1E1D01-42F9-4557-9C33-565E5CCE47DB")] = T("Processor"),
        [new Guid("A5BC1114-6F64-4EDE-B863-3E83ED7C83B1")] = T("Memory"),
        [new Guid("D995E954-BBC1-430F-AD91-B44DCB3C6F35")] = T("PCI Express"),
        [new Guid("C5753963-3B84-4095-BF78-EDDAD3F9C9DD")] = T("PCI bus"),
        [new Guid("EB5E4685-CA66-4769-B6A2-26068B001326")] = T("PCI device"),
        [new Guid("81212A96-09ED-4996-9471-8D729C8E69ED")] = T("Firmware"),
    };

    private static readonly Guid ProcessorGenericSection = new("9876CCAD-47B4-4BDB-B65E-16F193C4F3DB");

    /// <summary>The CPER record header size and the size of one section descriptor.</summary>
    private const int HeaderSize = 128;
    private const int DescriptorSize = 72;

    /// <summary>The plain name of an error source ("Machine check exception", "PCI Express error"…), an English key.</summary>
    public static string SourceName(WheaErrorSource source) => source switch
    {
        WheaErrorSource.MachineCheck => T("Machine check exception (the processor)"),
        WheaErrorSource.CorrectedMachineCheck => T("Corrected machine check (the processor)"),
        WheaErrorSource.CorrectedPlatform => T("Corrected platform error"),
        WheaErrorSource.Nmi => T("Non-maskable interrupt (a hardware emergency signal)"),
        WheaErrorSource.PciExpress => T("PCI Express error (a graphics card, SSD or other card)"),
        WheaErrorSource.Generic or WheaErrorSource.GenericV2 => T("Generic hardware error reported by the firmware"),
        WheaErrorSource.Init => T("Initialization error"),
        WheaErrorSource.Boot => T("Boot error"),
        WheaErrorSource.SciGeneric => T("Platform error reported by the firmware"),
        WheaErrorSource.DeviceDriver => T("Error reported by a device driver"),
        _ => T("Unknown error source"),
    };

    /// <summary>BugCheck 0x124 parameter 1 (or an event's ErrorSource number) to an error source.</summary>
    public static WheaErrorSource SourceOf(ulong value) =>
        value <= 0x12 && Enum.IsDefined(typeof(WheaErrorSource), (int)value) ? (WheaErrorSource)(int)value : WheaErrorSource.Unknown;

    /// <summary>
    /// Decodes MCi_STATUS: the valid, overflow, uncorrected and processor-context-corrupt bits and the class of the
    /// architectural MCA error code in the low 16 bits (bit 12, the filter bit, is ignored).
    /// </summary>
    public static McaStatusInfo DecodeMcaStatus(ulong status)
    {
        ushort code = (ushort)(status & 0xFFFF);
        return new McaStatusInfo(
            (status & (1UL << 63)) != 0,
            (status & (1UL << 61)) != 0,
            (status & (1UL << 57)) != 0,
            (status & (1UL << 62)) != 0,
            code,
            McaErrorClass(code));
    }

    /// <summary>The plain class of an architectural MCA error code (English key).</summary>
    public static string McaErrorClass(ushort code)
    {
        int c = code & 0xEFFF;
        return c switch
        {
            0x0000 => T("No error code"),
            0x0001 => T("Unclassified processor error"),
            0x0002 => T("Microcode memory error inside the processor"),
            0x0003 => T("Error signalled by another part of the PC"),
            0x0004 => T("Processor self-check error"),
            0x0005 => T("Internal parity error inside the processor"),
            0x0400 => T("Internal timer error: a processor core stopped responding (often an unstable undervolt or overclock)"),
            _ when (c & 0xFC00) == 0x0400 => T("Internal processor error"),
            _ when (c & 0xE800) == 0x0800 => T("Bus or interconnect error (on Ryzen often the Infinity Fabric / memory speed)"),
            _ when (c & 0xEF00) == 0x0100 => T("Cache error inside the processor"),
            _ when (c & 0xEF80) == 0x0080 => T("Memory controller error (often RAM or memory settings such as XMP/EXPO)"),
            _ when (c & 0xEFF0) == 0x0010 => T("Address translation (TLB) error inside the processor"),
            _ when (c & 0xEFFC) == 0x000C => T("Cache error inside the processor"),
            _ => T("Processor error with a model-specific code"),
        };
    }

    /// <summary>
    /// Parses a CPER error record (the "CPER" signature, header, section descriptors and, when present, the processor
    /// section's error type and APIC id). Null when the bytes are not a record.
    /// </summary>
    public static WheaErrorInfo? ParseCper(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || data[0] != (byte)'C' || data[1] != (byte)'P' || data[2] != (byte)'E' || data[3] != (byte)'R')
        {
            return null;
        }

        ushort sections = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        uint severity = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var notify = new Guid(data.Slice(96, 16));
        var source = NotifyTypes.TryGetValue(notify, out var s) ? s : WheaErrorSource.Unknown;
        var components = new List<string>();
        int? apic = null;
        string? processorType = null;
        for (int i = 0; i < sections && i < 16; i++)
        {
            int at = HeaderSize + (i * DescriptorSize);
            if (at + DescriptorSize > data.Length)
            {
                break;
            }

            var descriptor = data.Slice(at, DescriptorSize);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
            var type = new Guid(descriptor.Slice(16, 16));
            if (SectionTypes.TryGetValue(type, out var component) && !components.Contains(component))
            {
                components.Add(component);
            }

            if (type == ProcessorGenericSection && offset + Math.Min(length, 160u) <= data.Length && length >= 160)
            {
                var section = data.Slice((int)offset, (int)length);
                ulong valid = BinaryPrimitives.ReadUInt64LittleEndian(section);
                if ((valid & (1 << 2)) != 0)
                {
                    processorType = ProcessorErrorType(section[10]);
                }

                if ((valid & (1 << 8)) != 0)
                {
                    ulong id = BinaryPrimitives.ReadUInt64LittleEndian(section[152..]);
                    apic = id <= int.MaxValue ? (int)id : null;
                }
            }
        }

        return new WheaErrorInfo
        {
            Source = source,
            Fatal = severity == 1,
            Components = components,
            ApicId = apic,
            ProcessorErrorType = processorType,
        };
    }

    /// <summary>The processor section's error type byte (cache, TLB, bus, micro-architecture) as an English key.</summary>
    public static string? ProcessorErrorType(byte value) => value switch
    {
        0x01 => T("Cache error"),
        0x02 => T("TLB error"),
        0x04 => T("Bus error"),
        0x08 => T("Micro-architecture error"),
        _ => null,
    };

    /// <summary>"4350455210…" (how event XML shows binary data) to bytes; null when it is not hex.</summary>
    public static byte[]? FromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        var text = hex.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (text.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// A WHEA-Logger event (17, 18, 19, 20, 47) from its named data: ErrorSource, ApicId, MCABank, MciStat and the CPER
    /// record in RawData. Event 18/20 are fatal. Null for other events.
    /// </summary>
    public static WheaErrorInfo? FromEvent(EventXmlRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Provider != "Microsoft-Windows-WHEA-Logger" || record.EventId is not (1 or 17 or 18 or 19 or 20 or 47))
        {
            return null;
        }

        var raw = FromHex(record.Get("RawData"));
        var parsed = raw is null ? null : ParseCper(raw);
        var source = BugCheckFormat.ParseNumber(record.Get("ErrorSource"), decimalWithoutPrefix: true) is { } n ? SourceOf(n) : parsed?.Source ?? WheaErrorSource.Unknown;
        if (source == WheaErrorSource.Unknown)
        {
            source = record.EventId switch
            {
                19 => WheaErrorSource.CorrectedMachineCheck,
                17 or 20 => WheaErrorSource.PciExpress,
                18 => WheaErrorSource.MachineCheck,
                _ => parsed?.Source ?? WheaErrorSource.Unknown,
            };
        }

        int? apic = int.TryParse(record.Get("ApicId", "ProcessorAPICID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : parsed?.ApicId;
        int? bank = int.TryParse(record.Get("MCABank"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : null;
        var components = parsed?.Components.ToList() ?? [];
        if (components.Count == 0)
        {
            components.Add(record.EventId switch
            {
                17 or 20 => T("PCI Express"),
                47 => T("Memory"),
                _ => T("Processor"),
            });
        }

        return new WheaErrorInfo
        {
            Source = source,
            Fatal = record.EventId is 18 or 20 || parsed?.Fatal == true,
            Components = components,
            ApicId = apic,
            Bank = bank,
            MciStatus = BugCheckFormat.ParseNumber(record.Get("MciStat")),
            ProcessorErrorType = parsed?.ProcessorErrorType,
            Time = record.Time,
        };
    }

    /// <summary>The plain lines for a WHEA error: where it came from, which part, which core and what the status says.</summary>
    public static IReadOnlyList<CrashLine> Describe(WheaErrorInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var lines = new List<CrashLine> { new(T("Error source: {0}."), new LocalizedText(SourceName(info.Source))) };
        if (info.Components.Count > 0)
        {
            lines.Add(new CrashLine(T("Part that reported it: {0}."), new LocalizedList(info.Components)));
        }

        if (info.ApicId is { } apic)
        {
            lines.Add(new CrashLine(T("Processor APIC ID {0} (core {1} when SMT/Hyper-Threading is on). The same core every time points at that core's Curve Optimizer or undervolt setting."),
                apic.ToString(CultureInfo.InvariantCulture), (apic / 2).ToString(CultureInfo.InvariantCulture)));
        }

        if (info.ProcessorErrorType is { } type)
        {
            lines.Add(new CrashLine(T("Processor error type: {0}."), new LocalizedText(type)));
        }

        if (info.MciStatus is { } status && status != 0)
        {
            var mca = DecodeMcaStatus(status);
            lines.Add(new CrashLine(T("Machine check status {0}: {1}."), "0x" + status.ToString("X16", CultureInfo.InvariantCulture), new LocalizedText(mca.ErrorClass)));
            if (mca.Uncorrected)
            {
                lines.Add(CrashLine.Of(mca.ContextCorrupt
                    ? T("The error could not be corrected and the processor could not continue safely, so Windows had to stop.")
                    : T("The error could not be corrected.")));
            }
        }

        return lines;
    }
}

/// <summary>An English key shown translated when its line is formatted (a value inside a <see cref="CrashLine"/>).</summary>
public sealed record LocalizedText(string English)
{
    public override string ToString() => English;
}

/// <summary>English keys shown translated and joined with commas (a value inside a <see cref="CrashLine"/>).</summary>
public sealed record LocalizedList(IReadOnlyList<string> English)
{
    public override string ToString() => string.Join(", ", English);
}
