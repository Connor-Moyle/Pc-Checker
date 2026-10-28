namespace PcChecker.Core.Crashes.Dumps;

/// <summary>
/// Reads the header of a Windows kernel crash dump: the 64-bit DUMP_HEADER64 ("PAGE" + "DU64", 0x2000 bytes) used by
/// MEMORY.DMP, the small dumps in %SystemRoot%\Minidump and LiveKernelReports, and the old 32-bit DUMP_HEADER32
/// ("PAGE" + "DUMP"). For a small (triage) dump it also reads the loaded driver list and the crashing thread's raw
/// stack from the TRIAGE_DUMP64 block that follows the header. Every offset is bounds-checked and every pointer is
/// validated, so a truncated or damaged file returns what it can and never throws.
/// </summary>
public static class KernelDumpReader
{
    /// <summary>Size of the 64-bit header; the triage block of a small dump starts here.</summary>
    public const int Header64Size = 0x2000;

    /// <summary>DUMP_TYPE of a small memory dump (triage dump).</summary>
    public const uint TriageDumpType = 4;

    private const int MaxDrivers = 4096;
    private const int MaxStackBytes = 0x10000;

    // DUMP_HEADER64 offsets.
    private const int H64MinorVersion = 0x0C;
    private const int H64MachineType = 0x30;
    private const int H64BugCheckCode = 0x38;
    private const int H64Parameters = 0x40;
    private const int H64ExceptionCode = 0xF00;
    private const int H64ExceptionAddress = 0xF10;
    private const int H64DumpType = 0xF98;
    private const int H64SystemTime = 0xFA8;

    // TRIAGE_DUMP64 offsets (relative to the start of the triage block).
    private const int TriageCallStackOffset = 0x28;
    private const int TriageSizeOfCallStack = 0x2C;
    private const int TriageDriverListOffset = 0x30;
    private const int TriageDriverCount = 0x34;

    // DUMP_DRIVER_ENTRY64: ULONG DriverNameOffset, padding, then KLDR_DATA_TABLE_ENTRY64 whose DllBase is at +0x30 and
    // SizeOfImage at +0x40. The entry has grown between Windows versions, so the stride is found by validation.
    private const int LdrEntryStart = 0x08;
    private const int LdrDllBase = 0x30;
    private const int LdrSizeOfImage = 0x40;
    private static readonly int[] CandidateStrides = [0x88, 0x90, 0x98, 0xA0, 0xA8, 0xB0, 0xB8, 0xC0, 0xC8, 0xD0];

    /// <summary>True when the bytes start with a kernel dump signature.</summary>
    public static bool IsKernelDump(ReadOnlySpan<byte> data)
    {
        var b = new DumpBytes(data);
        return b.Matches(0, "PAGE") && (b.Matches(4, "DU64") || b.Matches(4, "DUMP"));
    }

    /// <summary>Parses the header (and the triage block of a small dump); null when the signature or the stop code is missing.</summary>
    public static CrashDumpInfo? Read(ReadOnlySpan<byte> data)
    {
        var b = new DumpBytes(data);
        if (!b.Matches(0, "PAGE"))
        {
            return null;
        }

        if (b.Matches(4, "DU64"))
        {
            return Read64(b);
        }

        return b.Matches(4, "DUMP") ? Read32(b) : null;
    }

    private static CrashDumpInfo? Read64(DumpBytes b)
    {
        if (b.U32(H64BugCheckCode) is not { } code || !b.Has(H64Parameters, 32))
        {
            return null;
        }

        var parameters = new ulong[4];
        for (int i = 0; i < 4; i++)
        {
            parameters[i] = b.U64(H64Parameters + (i * 8))!.Value;
        }

        uint? dumpType = b.U32(H64DumpType);
        uint? exceptionCode = b.U32(H64ExceptionCode);
        ulong? exceptionAddress = b.U64(H64ExceptionAddress);
        bool truncated = !b.Has(0, Header64Size);
        IReadOnlyList<CrashModule> modules = [];
        IReadOnlyList<ulong> stack = [];
        if (dumpType == TriageDumpType)
        {
            if (b.Has(Header64Size, TriageDriverCount + 4))
            {
                bool cut;
                (modules, cut) = ReadDrivers(b, Header64Size);
                truncated |= cut;
                stack = ReadStack(b, Header64Size, ref truncated);
            }
            else
            {
                truncated = true;
            }
        }

        return new CrashDumpInfo
        {
            Format = DumpFormat.Kernel64,
            KindText = KindText(dumpType),
            DumpType = dumpType,
            BugCheckCode = code,
            Parameters = parameters,
            ExceptionCode = exceptionCode is 0 ? null : exceptionCode,
            ExceptionAddress = exceptionAddress is 0 ? null : exceptionAddress,
            OsBuild = Build(b.U32(H64MinorVersion)),
            Architecture = Machine(b.U32(H64MachineType)),
            CrashTime = DumpBytes.FileTime(b.U64(H64SystemTime)),
            Modules = modules,
            StackValues = stack,
            Truncated = truncated,
        };
    }

    private static CrashDumpInfo? Read32(DumpBytes b)
    {
        // DUMP_HEADER32: MinorVersion 0x0C, MachineImageType 0x20, BugCheckCode 0x28, four ULONG parameters from 0x2C.
        if (b.U32(0x28) is not { } code || !b.Has(0x2C, 16))
        {
            return null;
        }

        var parameters = new ulong[4];
        for (int i = 0; i < 4; i++)
        {
            parameters[i] = b.U32(0x2C + (i * 4))!.Value;
        }

        return new CrashDumpInfo
        {
            Format = DumpFormat.Kernel32,
            BugCheckCode = code,
            Parameters = parameters,
            OsBuild = Build(b.U32(0x0C)),
            Architecture = Machine(b.U32(0x20)),
            Truncated = !b.Has(0, 0x1000),
        };
    }

    /// <summary>Reads the driver list of a triage dump, finding the entry size by checking every entry's pointers.</summary>
    private static (IReadOnlyList<CrashModule> Modules, bool Truncated) ReadDrivers(DumpBytes b, int triage)
    {
        uint listOffset = b.U32(triage + TriageDriverListOffset) ?? 0;
        uint count = b.U32(triage + TriageDriverCount) ?? 0;
        if (listOffset == 0 || count == 0 || count > MaxDrivers || listOffset >= b.Length)
        {
            return ([], listOffset >= b.Length && count > 0);
        }

        List<CrashModule> best = [];
        foreach (int stride in CandidateStrides)
        {
            var found = new List<CrashModule>();
            for (int i = 0; i < count; i++)
            {
                if (TryDriver(b, listOffset + ((long)i * stride)) is not { } module)
                {
                    break;
                }

                found.Add(module);
            }

            if (found.Count == count)
            {
                return (found, false);
            }

            if (found.Count > best.Count)
            {
                best = found;
            }
        }

        // Only part of the list could be read (the file ends early or the layout is new): keep what was valid.
        bool truncated = !b.Has(listOffset, (long)count * CandidateStrides[0]);
        return (best, truncated);
    }

    private static CrashModule? TryDriver(DumpBytes b, long entry)
    {
        if (b.U32(entry) is not { } nameOffset
            || b.U64(entry + LdrEntryStart + LdrDllBase) is not { } dllBase
            || b.U32(entry + LdrEntryStart + LdrSizeOfImage) is not { } size)
        {
            return null;
        }

        // Kernel modules live in the upper half of the address space and are at most a few hundred MB.
        if (dllBase < 0xFFFF800000000000UL || size == 0 || size > 0x40000000)
        {
            return null;
        }

        // DUMP_STRING: ULONG Length (characters) followed by the UTF-16 name.
        if (b.U32(nameOffset) is not { } chars || chars is 0 or > 260)
        {
            return null;
        }

        var name = b.Utf16(nameOffset + 4L, chars);
        return string.IsNullOrWhiteSpace(name) ? null : new CrashModule(name.Trim(), dllBase, size);
    }

    private static IReadOnlyList<ulong> ReadStack(DumpBytes b, int triage, ref bool truncated)
    {
        uint offset = b.U32(triage + TriageCallStackOffset) ?? 0;
        uint size = b.U32(triage + TriageSizeOfCallStack) ?? 0;
        if (offset == 0 || size == 0)
        {
            return [];
        }

        long length = Math.Min(size, MaxStackBytes);
        if (!b.Has(offset, length))
        {
            truncated = true;
            length = Math.Max(0, b.Length - (long)offset);
            if (offset >= b.Length)
            {
                return [];
            }
        }

        var values = new List<ulong>((int)(length / 8));
        for (long p = offset; p + 8 <= offset + length; p += 8)
        {
            values.Add(b.U64(p)!.Value);
        }

        return values;
    }

    /// <summary>The DUMP_TYPE value in plain words.</summary>
    public static string KindText(uint? dumpType) => dumpType switch
    {
        1 or 5 => "Complete memory dump",
        2 or 6 => "Kernel memory dump",
        TriageDumpType => "Small memory dump",
        _ => "Memory dump",
    };

    private static int? Build(uint? minorVersion) => minorVersion is { } v and > 0 and < 100_000 ? (int)v : null;

    private static string? Machine(uint? type) => type switch
    {
        0x8664 => "x64",
        0xAA64 => "ARM64",
        0x014C => "x86",
        _ => null,
    };
}
